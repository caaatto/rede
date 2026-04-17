using System.Text.Json;
using System.Text.Json.Nodes;
using Rede.Core.Crypto;
using Rede.Core.Networking;
using Rede.Core.Protocol;
using Rede.Core.Storage;

namespace Rede.Core.Services;

/// <summary>
/// Group management: create, invite, kick, rekey, group messaging.
/// Mirrors: GROUP_CREATE, GROUP_INVITE, GROUP_KICK, GROUP_MESSAGE handlers in index.js
/// </summary>
public class GroupService : IDisposable
{
    private readonly RedeConnection _conn;
    private readonly ProfileStore _store;
    private readonly NonceTracker _nonceTracker = new(); // H3: Replay protection for group messages

    // ACK FIFO: every persisted outgoing group message pushes its ChatMessage reference.
    // Server echoes GROUP_MESSAGE back to sender with a server-assigned msgId, in send order.
    // Pairing via FIFO is the only reliable method — scanning ChatHistory for "first own
    // null-MsgId" collides with orphan legacy entries from pre-fix versions.
    private readonly Queue<(string GroupId, ChatMessage Msg)> _pendingAck = new();
    private readonly object _pendingAckLock = new();

    /// <summary>Replay-protection tracker — exposed for persistence across restarts.</summary>
    public NonceTracker NonceTracker => _nonceTracker;

    public void Dispose() { GC.SuppressFinalize(this); }

    public Profile? Profile { get; set; }
    public byte[]? Passphrase { get; set; }

    public event Action<string>? OnSystemMessage;
    public event Action<string, string, string, DateTime>? OnGroupMessageReceived; // groupId, from, text, ts
    public event Action? OnGroupsChanged;
    public event Action<string, string, int>? OnGroupMessageSent; // groupId, text, ttl
    public event Action<string, string, string, Dictionary<string, List<string>>>? OnReactionUpdated; // chatKey, msgId, emoji, reactions
    public event Action<string, string, string>? OnMessageEdited; // chatKey, msgId, newText
    public event Action<string, string>? OnMessageDeleted; // chatKey, msgId
    public event Action<string, string>? OnOwnMessageIdAssigned; // groupId, msgId

    public GroupService(RedeConnection conn, ProfileStore store)
    {
        _conn = conn;
        _store = store;
        RegisterHandlers();
    }

    private void RegisterHandlers()
    {
        _conn.On(Msg.GroupCreateOk, HandleGroupCreateOk);
        _conn.On(Msg.GroupInvite, HandleGroupInvite);
        _conn.On(Msg.GroupKickOk, HandleGroupKickOk);
        _conn.On(Msg.GroupMessage, HandleGroupMessage);
    }

    public void CreateGroup(string name)
    {
        if (Profile is null) return;
        // M4: Don't send group key to server — server doesn't need it, key is generated locally in HandleGroupCreateOk
        _conn.Send(Msg.GroupCreate, ProtocolSerializer.Payload(
            ("name", JsonValue.Create(name))
        ));
    }

    public void InviteToGroup(string groupId, string userId, ChatService? chatService = null)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Groups.TryGetValue(groupId, out var group))
        {
            OnSystemMessage?.Invoke("Group not found.");
            return;
        }

        // Send invite via server (adds member on server side)
        _conn.Send(Msg.GroupInvite, ProtocolSerializer.Payload(
            ("groupId", JsonValue.Create(groupId)),
            ("inviteeId", JsonValue.Create(userId))
        ));

        // Send group key to invitee via ratcheted DM
        if (chatService is not null && Profile.Contacts.ContainsKey(userId))
        {
            var sig = CryptoService.SignGroupKey(groupId, group.Name, group.Key, Profile.SigningSecretKey);
            var keyMsg = JsonSerializer.Serialize(new
            {
                __rede_ctrl = "groupkey",
                groupId,
                name = group.Name,
                key = group.Key,
                sig,
            });
            chatService.SendMessage(userId, keyMsg, 0);
            OnSystemMessage?.Invoke($"Invited {userId} to \"{group.Name}\" - group key sent.");
        }
        else
        {
            OnSystemMessage?.Invoke($"Invited {userId} to \"{group.Name}\" (key must be sent manually - add them as contact first).");
        }
    }

    public void KickFromGroup(string groupId, string userId)
    {
        _conn.Send(Msg.GroupKick, ProtocolSerializer.Payload(
            ("groupId", JsonValue.Create(groupId)),
            ("userId", JsonValue.Create(userId))
        ));
    }

    /// <summary>
    /// Rotate group key and distribute via ratcheted DMs.
    /// Requires a ChatService reference for the ratcheted send.
    /// </summary>
    public void RekeyGroup(string groupId, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Groups.TryGetValue(groupId, out var group))
        {
            OnSystemMessage?.Invoke("Group not found.");
            return;
        }

        // Only group creator can rekey
        if (group.Members is not null && group.Members.Count > 0 && group.Members[0] != Profile.UserId)
        {
            OnSystemMessage?.Invoke("Only the group creator can rotate the group key.");
            return;
        }

        var newKey = CryptoService.GenerateSymmetricKey();
        group.Key = newKey;
        _store.SaveProfileDebounced(Profile, Passphrase);

        int sent = 0;
        if (group.Members is not null && chatService is not null)
        {
            foreach (var memberId in group.Members)
            {
                if (memberId == Profile.UserId) continue;
                if (!Profile.Contacts.ContainsKey(memberId)) continue;

                var sig = CryptoService.SignGroupKey(groupId, group.Name, newKey, Profile.SigningSecretKey);
                var keyMsg = System.Text.Json.JsonSerializer.Serialize(new
                {
                    __rede_ctrl = "groupkey",
                    groupId,
                    name = group.Name,
                    key = newKey,
                    sig,
                });
                // M9: Group key distribution must not expire — offline members need it
                chatService.SendMessage(memberId, keyMsg, 0);
                sent++;
            }
        }

        OnSystemMessage?.Invoke($"Group key rotated for \"{group.Name}\". New key sent to {sent} member(s).");
    }

    public void SendGroupMessage(string groupId, string text, int ttl = 0)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Groups.TryGetValue(groupId, out var group))
        {
            OnSystemMessage?.Invoke("Group not found");
            return;
        }

        // Get or create sender key state
        var skStateJson = _store.LoadSenderKeyState(Profile, groupId);
        SenderKeys.SenderKeyState skState;

        if (skStateJson is not null)
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(skStateJson.Value);
            var ownNode = parsed.GetProperty("own");
            var ckB64 = ownNode.GetProperty("chainKey").GetString() ?? "";
            skState = new SenderKeys.SenderKeyState
            {
                ChainKey = ckB64.Length == 0 ? Array.Empty<byte>() : Convert.FromBase64String(ckB64),
                MessageNumber = ownNode.GetProperty("messageNumber").GetInt32(),
            };
        }
        else
        {
            skState = SenderKeys.Generate();
        }

        var result = SenderKeys.Encrypt(skState, text, Profile.SigningSecretKey, groupId);

        // Save updated state (debounced — no scrypt, no Task.Run needed)
        var stateObj = new JsonObject
        {
            ["own"] = new JsonObject
            {
                ["chainKey"] = Convert.ToBase64String(skState.ChainKey),
                ["messageNumber"] = skState.MessageNumber,
            }
        };
        var elem = JsonSerializer.SerializeToElement(stateObj);
        _store.SaveSenderKeyStateAsync(Profile, groupId, elem, Passphrase);

        var payload = ProtocolSerializer.Payload(
            ("groupId", JsonValue.Create(groupId)),
            ("encrypted", JsonValue.Create(result.Ciphertext)),
            ("nonce", JsonValue.Create(result.Nonce)),
            ("senderKeyHeader", new JsonObject
            {
                ["messageNumber"] = result.MessageNumber,
                ["signature"] = result.Signature,
            })
        );
        if (ttl > 0) payload["ttl"] = ttl;

        _conn.Send(Msg.GroupMessage, payload);

        // Persist own group message (skip control messages)
        if (!text.Contains("\"__rede_ctrl\""))
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var persistedMsg = new ChatMessage
            {
                From = Profile.UserId, Text = text, Ts = ts, Ttl = ttl,
            };
            _store.AddChatMessage(Profile, groupId, persistedMsg, Passphrase);
            lock (_pendingAckLock) { _pendingAck.Enqueue((groupId, persistedMsg)); }
            OnGroupMessageSent?.Invoke(groupId, text, ttl);
        }
    }

    public IReadOnlyDictionary<string, Group>? GetGroups() => Profile?.Groups;

    public void SendReaction(string groupId, string msgId, string emoji, bool add)
    {
        if (Profile is null || Passphrase is null) return;
        var controlText = MessageEnvelope.EncodeReaction(msgId, emoji, add);
        // Reuse SendGroupMessage which handles Sender Keys encryption + GROUP_MESSAGE send.
        // Control messages are filtered from chat history by the __rede_ctrl check.
        SendGroupMessage(groupId, controlText);
        ApplyReaction(groupId, msgId, emoji, Profile.UserId, add);
    }

    // --- Handlers ---

    private async void HandleGroupCreateOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var groupId = ProtocolSerializer.GetString(msg, "groupId");
        var name = ProtocolSerializer.GetString(msg, "name");
        if (groupId is null || name is null) return;

        var key = CryptoService.GenerateSymmetricKey();
        await _store.AddGroupAsync(Profile, groupId, name, key, new List<string> { Profile.UserId }, Passphrase);

        OnSystemMessage?.Invoke($"Group '{name}' created ({groupId})");
        OnGroupsChanged?.Invoke();
    }

    private async void HandleGroupInvite(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var groupId = ProtocolSerializer.GetString(msg, "groupId");
        // M3: Sanitize group name from server — strip control chars, cap length
        var rawName = ProtocolSerializer.GetString(msg, "name") ?? groupId ?? "unnamed";
        var name = System.Text.RegularExpressions.Regex.Replace(rawName, @"[\x00-\x1f\x7f]", "");
        if (name.Length > 64) name = name[..64];
        // H2: Never accept group key from server — always generate locally.
        // Real key comes via signed ratcheted DM from the group creator.
        var key = CryptoService.GenerateSymmetricKey();

        if (groupId is null) return;

        await _store.AddGroupAsync(Profile, groupId, name, key, null, Passphrase);
        OnSystemMessage?.Invoke($"You were invited to group '{name}'");
        OnGroupsChanged?.Invoke();
    }

    private void HandleGroupKickOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var groupId = ProtocolSerializer.GetString(msg, "groupId");
        var userId = ProtocolSerializer.GetString(msg, "userId");
        if (groupId is null || userId is null) return;

        // M8: Update local member list on kick
        if (Profile.Groups.TryGetValue(groupId, out var group) && group.Members is not null)
        {
            group.Members.Remove(userId);
            _store.SaveProfileDebounced(Profile, Passphrase);
        }

        OnSystemMessage?.Invoke($"Removed {userId} from group {groupId}");
    }

    private void HandleGroupMessage(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var groupId = ProtocolSerializer.GetString(msg, "groupId");
        var from = ProtocolSerializer.GetString(msg, "from");
        if (groupId is null || from is null) return;
        if (from == Profile.UserId)
        {
            // Server echoes own GROUP_MESSAGE back with a server-assigned msgId.
            // Pair with the head of the pending-ACK FIFO — WS delivers echoes in send order.
            // Scanning ChatHistory for "first own null-MsgId" is unreliable (pre-fix orphans).
            var ownMsgId = ProtocolSerializer.GetString(msg, "msgId");
            if (ownMsgId is null) return;

            (string GroupId, ChatMessage Msg) entry;
            lock (_pendingAckLock)
            {
                // Echoes for control messages (reactions/edits/deletes) arrive with no
                // matching queue entry since we don't persist/enqueue those — drop silently.
                if (_pendingAck.Count == 0) return;
                entry = _pendingAck.Dequeue();
            }
            entry.Msg.MsgId = ownMsgId;
            _store.SaveChatHistoryDebounced(Profile, Passphrase);
            OnOwnMessageIdAssigned?.Invoke(entry.GroupId, ownMsgId);
            return;
        }

        // C4: Verify group exists and sender is a member
        if (!Profile.Groups.TryGetValue(groupId, out var group))
        {
            OnSystemMessage?.Invoke($"Message for unknown group {groupId} - dropped.");
            return;
        }
        // M5: Also reject if member list is empty (not yet populated)
        if (group.Members is null || group.Members.Count == 0 || !group.Members.Contains(from))
        {
            OnSystemMessage?.Invoke($"[SECURITY] Non-member {from} sent to group {groupId} - dropped.");
            return;
        }

        var encrypted = ProtocolSerializer.GetString(msg, "encrypted");
        var nonce = ProtocolSerializer.GetString(msg, "nonce");
        if (encrypted is null || nonce is null) return;

        // H3: Replay protection for group messages
        if (!_nonceTracker.Check(nonce)) return;

        var skHeader = msg["senderKeyHeader"];
        if (skHeader is null) return;

        var messageNumber = skHeader["messageNumber"]?.GetValue<int>() ?? 0;
        var signature = skHeader["signature"]?.GetValue<string>();
        if (signature is null) return;

        // Get sender's signing key
        if (!Profile.Contacts.TryGetValue(from, out var contact))
        {
            OnSystemMessage?.Invoke($"Unknown sender in group: {from}");
            return;
        }

        var signingKey = contact.SigningKey;
        if (signingKey is null) return;

        // Get sender key state for this user in this group
        var skStateJson = _store.LoadSenderKeyState(Profile, groupId);
        SenderKeys.SenderKeyState memberState;

        if (skStateJson is not null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(skStateJson.Value);
                if (parsed.TryGetProperty("members", out var members) &&
                    members.TryGetProperty(from, out var memberData))
                {
                    var mckB64 = memberData.GetProperty("chainKey").GetString() ?? "";
                    memberState = new SenderKeys.SenderKeyState
                    {
                        ChainKey = mckB64.Length == 0 ? Array.Empty<byte>() : Convert.FromBase64String(mckB64),
                        MessageNumber = memberData.GetProperty("messageNumber").GetInt32(),
                    };
                }
                else return; // No sender key for this member
            }
            catch { return; }
        }
        else return;

        var plaintext = SenderKeys.Decrypt(memberState, encrypted, nonce, messageNumber, signature, signingKey, groupId);
        if (plaintext is null) return;

        // M6: Save updated sender key state without re-loading (use already-parsed state)
        {
            var parsed = JsonSerializer.Deserialize<JsonObject>(skStateJson.Value);
            if (parsed is not null)
            {
                var membersNode = parsed["members"] as JsonObject ?? new JsonObject();
                membersNode[from] = new JsonObject
                {
                    ["chainKey"] = Convert.ToBase64String(memberState.ChainKey),
                    ["messageNumber"] = memberState.MessageNumber,
                };
                parsed["members"] = membersNode;
                var skElem = JsonSerializer.SerializeToElement(parsed);
                _store.SaveSenderKeyStateAsync(Profile, groupId, skElem, Passphrase);
            }
        }

        // Handle control messages (reactions, edits, deletes)
        var ctrl = MessageEnvelope.TryParseControl(plaintext);
        if (ctrl is not null)
        {
            HandleControlMessage(ctrl.Value.ctrl, ctrl.Value.obj, from, groupId);
            return;
        }

        // Decode JSON envelope (backward-compatible with plain-text messages)
        var text = MessageEnvelope.Decode(plaintext, out var replyToMsgId, out var replyToPreview, out var replyToAuthor);

        var sanitized = ChatService.EscapeContent(text);
        var serverMsgId = ProtocolSerializer.GetString(msg, "msgId");
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(ProtocolSerializer.GetLong(msg, "ts")).LocalDateTime;

        var chatMsg = new ChatMessage
        {
            From = from, Text = sanitized, Ts = ProtocolSerializer.GetLong(msg, "ts"),
            Ttl = ProtocolSerializer.GetInt(msg, "ttl"),
            MsgId = serverMsgId,
            ReplyToMsgId = replyToMsgId,
            ReplyToPreview = replyToPreview is not null ? ChatService.EscapeContent(replyToPreview) : null,
            ReplyToAuthor = replyToAuthor,
        };

        _store.AddChatMessage(Profile, groupId, chatMsg, Passphrase);
        OnGroupMessageReceived?.Invoke(groupId, from, sanitized, ts);
    }

    private void HandleControlMessage(string ctrl, JsonObject obj, string from, string chatKey)
    {
        switch (ctrl)
        {
            case "reaction":
            {
                var msgId = obj["mid"]?.GetValue<string>();
                var emoji = obj["emoji"]?.GetValue<string>();
                var action = obj["action"]?.GetValue<string>();
                if (msgId is null || emoji is null) return;
                if (emoji.Length > 32) emoji = emoji[..32];
                ApplyReaction(chatKey, msgId, emoji, from, action == "add");
                break;
            }
            case "edit":
            {
                var msgId = obj["mid"]?.GetValue<string>();
                var newText = obj["newText"]?.GetValue<string>();
                if (msgId is null || newText is null) return;
                ApplyEdit(chatKey, msgId, newText, from);
                break;
            }
            case "delete":
            {
                var msgId = obj["mid"]?.GetValue<string>();
                if (msgId is null) return;
                ApplyDelete(chatKey, msgId, from);
                break;
            }
        }
    }

    private void ApplyReaction(string chatKey, string msgId, string emoji, string userId, bool add)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.ChatHistory.TryGetValue(chatKey, out var messages)) return;

        var target = messages.FirstOrDefault(m => m.MsgId == msgId);
        if (target is null) return;

        target.Reactions ??= new();

        if (add)
        {
            if (!target.Reactions.TryGetValue(emoji, out var users))
            {
                users = new List<string>();
                target.Reactions[emoji] = users;
            }
            if (!users.Contains(userId))
                users.Add(userId);
        }
        else
        {
            if (target.Reactions.TryGetValue(emoji, out var users))
            {
                users.Remove(userId);
                if (users.Count == 0)
                    target.Reactions.Remove(emoji);
            }
        }

        _store.SaveChatHistoryDebounced(Profile, Passphrase);
        OnReactionUpdated?.Invoke(chatKey, msgId, emoji, target.Reactions);
    }

    private void ApplyEdit(string chatKey, string msgId, string newText, string? from = null)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.ChatHistory.TryGetValue(chatKey, out var messages)) return;

        var target = messages.FirstOrDefault(m => m.MsgId == msgId);
        if (target is null) return;

        // Only the original author can edit
        if (from is not null && target.From != from) return;

        target.Text = ChatService.EscapeContent(newText);
        target.EditedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _store.SaveChatHistoryDebounced(Profile, Passphrase);
        OnMessageEdited?.Invoke(chatKey, msgId, target.Text);
    }

    private void ApplyDelete(string chatKey, string msgId, string? from = null)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.ChatHistory.TryGetValue(chatKey, out var messages)) return;

        var target = messages.FirstOrDefault(m => m.MsgId == msgId);
        if (target is null) return;

        // Author can delete own messages
        if (from is not null && target.From != from) return; // Groups don't have admin roles for delete

        target.IsDeleted = true;
        target.Text = "";

        _store.SaveChatHistoryDebounced(Profile, Passphrase);
        OnMessageDeleted?.Invoke(chatKey, msgId);
    }
}
