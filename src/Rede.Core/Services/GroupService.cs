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

    // ACK FIFO: every WS GROUP_MESSAGE send pushes one entry. Server echoes GROUP_MESSAGE
    // back to sender with a server-assigned msgId, in send order. Control messages
    // (reactions/edits/deletes) push (groupId, Msg=null) sentinels so their echo consumes
    // its own slot and doesn't steal from the next user-visible message.
    private readonly Queue<(string GroupId, ChatMessage? Msg)> _pendingAck = new();
    private readonly object _pendingAckLock = new();
    private const int MaxPendingAck = 500;

    /// <summary>Drop all pending ACK slots — called on disconnect so post-reconnect echoes
    /// can't mispair with stale entries from the previous session.</summary>
    public void ClearPendingAcks()
    {
        lock (_pendingAckLock) _pendingAck.Clear();
    }

    /// <summary>Replay-protection tracker — exposed for persistence across restarts.</summary>
    public NonceTracker NonceTracker => _nonceTracker;

    public void Dispose() { GC.SuppressFinalize(this); }

    public Profile? Profile { get; set; }
    public byte[]? Passphrase { get; set; }

    /// <summary>Ratcheted DM channel used for sender-key / member-list distribution. Wired in MainWindow.</summary>
    public ChatService? Chat { get; set; }

    public event Action<string>? OnSystemMessage;
    public event Action<string, string, string, DateTime, ChatMessage?>? OnGroupMessageReceived; // groupId, from, text, ts, fullMsg
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
        var chat = chatService ?? Chat;
        if (chat is not null && Profile.Contacts.ContainsKey(userId))
        {
            // Maintain the local member list BEFORE building the key message,
            // so the invitee receives the complete (signed) list
            group.Members ??= new List<string>();
            if (!group.Members.Contains(Profile.UserId)) group.Members.Add(Profile.UserId);
            var isNewMember = !group.Members.Contains(userId);
            if (isNewMember) group.Members.Add(userId);
            _store.SaveProfileDebounced(Profile, Passphrase);

            var membersSig = SignMembersList(groupId, group.Members);

            var sig = CryptoService.SignGroupKey(groupId, group.Name, group.Key, Profile.SigningSecretKey);
            var keyMsg = JsonSerializer.Serialize(new
            {
                __rede_ctrl = "groupkey",
                groupId,
                name = group.Name,
                key = group.Key,
                sig,
                members = group.Members,
                membersSig,
            });
            chat.SendMessage(userId, keyMsg, 0);

            // Distribute our own sender key so the invitee can decrypt our group messages
            SendOwnSenderKeyTo(groupId, new[] { userId });

            // Tell the existing members about the new member so they accept the
            // invitee's sender key and redistribute their own
            if (isNewMember)
            {
                var updMsg = JsonSerializer.Serialize(new
                {
                    __rede_ctrl = "groupmembers",
                    groupId,
                    members = group.Members,
                    sig = membersSig,
                });
                foreach (var memberId in group.Members)
                {
                    if (memberId == Profile.UserId || memberId == userId) continue;
                    if (!Profile.Contacts.ContainsKey(memberId)) continue;
                    chat.SendMessage(memberId, updMsg, 0);
                }
            }

            OnSystemMessage?.Invoke($"Invited {userId} to \"{group.Name}\" - group key sent.");
        }
        else
        {
            OnSystemMessage?.Invoke($"Invited {userId} to \"{group.Name}\" (key must be sent manually - add them as contact first).");
        }
    }

    /// <summary>Signature over the sorted member list — same wire format as the v1 JS client:
    /// GROUPMEMBERS:{groupId}:{sortedMembers.join(',')}</summary>
    private string SignMembersList(string groupId, IEnumerable<string> members)
    {
        var payload = MembersSigPayload(groupId, members);
        return CryptoService.SignBytesB64(System.Text.Encoding.UTF8.GetBytes(payload), Profile!.SigningSecretKey);
    }

    private static string MembersSigPayload(string groupId, IEnumerable<string> members)
        => $"GROUPMEMBERS:{groupId}:{string.Join(",", members.OrderBy(m => m, StringComparer.Ordinal))}";

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

    public void SendGroupMessage(string groupId, string text, int ttl = 0,
        List<AttachmentInfo>? attachments = null)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Groups.TryGetValue(groupId, out var group))
        {
            OnSystemMessage?.Invoke("Group not found");
            return;
        }

        // Use the JSON envelope on the wire when attachments are present; keep
        // the user-visible text separate for local persistence and search.
        var wireText = attachments is { Count: > 0 }
            ? MessageEnvelope.Encode(text, attachments: attachments)
            : text;

        // Get or create sender key state. The full state JSON (incl. received member
        // keys and the distributedTo tracking) must be preserved — earlier versions
        // wrote back only "own" and wiped every member key on each send.
        var skStateJson = _store.LoadSenderKeyState(Profile, groupId);
        var stateRoot = skStateJson is not null
            ? JsonSerializer.Deserialize<JsonObject>(skStateJson.Value) ?? new JsonObject()
            : new JsonObject();

        SenderKeys.SenderKeyState skState;
        if (stateRoot["own"] is JsonObject ownNode)
        {
            var ckB64 = ownNode["chainKey"]?.GetValue<string>() ?? "";
            skState = new SenderKeys.SenderKeyState
            {
                ChainKey = ckB64.Length == 0 ? Array.Empty<byte>() : Convert.FromBase64String(ckB64),
                MessageNumber = ownNode["messageNumber"]?.GetValue<int>() ?? 0,
            };
        }
        else
        {
            skState = SenderKeys.Generate();
        }

        // Distribute the CURRENT own key (pre-advance) to members who don't have it
        // yet — they need the chain key at this messageNumber to decrypt this message
        DistributeOwnSenderKey(groupId, group.Members, stateRoot,
            Convert.ToBase64String(skState.ChainKey), skState.MessageNumber);

        var result = SenderKeys.Encrypt(skState, wireText, Profile.SigningSecretKey, groupId);

        // Save updated state (debounced — no scrypt, no Task.Run needed)
        stateRoot["own"] = new JsonObject
        {
            ["chainKey"] = Convert.ToBase64String(skState.ChainKey),
            ["messageNumber"] = skState.MessageNumber,
        };
        var elem = JsonSerializer.SerializeToElement(stateRoot);
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

        if (!_conn.Send(Msg.GroupMessage, payload)) return;

        // One ACK slot per WS send. Control messages (reactions/edits/deletes) push a
        // (groupId, null) sentinel — the echo consumes it but doesn't stamp anything.
        // Regular messages push the persisted ChatMessage so the echo stamps its MsgId.
        bool isControl = wireText.Contains("\"__rede_ctrl\"");
        ChatMessage? persistedMsg = null;
        if (!isControl)
        {
            persistedMsg = new ChatMessage
            {
                From = Profile.UserId,
                Text = text,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Ttl = ttl,
                Attachments = attachments,
            };
            _store.AddChatMessage(Profile, groupId, persistedMsg, Passphrase);
            OnGroupMessageSent?.Invoke(groupId, text, ttl);
        }

        lock (_pendingAckLock)
        {
            if (_pendingAck.Count < MaxPendingAck)
                _pendingAck.Enqueue((groupId, persistedMsg));
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

    // --- Sender-key distribution & member-list updates (wire-compatible with v1 JS client) ---

    /// <summary>
    /// Send our current sender key (chainKey at the given messageNumber) to all listed
    /// members that are not yet recorded in stateRoot.distributedTo. Mutates stateRoot.
    /// </summary>
    private void DistributeOwnSenderKey(string contextId, List<string>? members, JsonObject stateRoot, string chainKeyB64, int messageNumber)
    {
        if (Chat is null || Profile is null || members is null) return;

        var distributed = stateRoot["distributedTo"] as JsonArray ?? new JsonArray();
        var have = new HashSet<string>();
        foreach (var n in distributed)
            if (n?.GetValue<string>() is string s) have.Add(s);

        foreach (var memberId in members)
        {
            if (memberId == Profile.UserId || have.Contains(memberId)) continue;
            if (!Profile.Contacts.ContainsKey(memberId)) continue;
            SendSenderKeyCtrl(contextId, memberId, chainKeyB64, messageNumber);
            distributed.Add(JsonValue.Create(memberId));
            have.Add(memberId);
        }
        stateRoot["distributedTo"] = distributed;
    }

    private void SendSenderKeyCtrl(string contextId, string memberId, string chainKeyB64, int messageNumber)
    {
        if (Chat is null || Profile is null) return;
        var payload = $"SENDERKEY:{contextId}:{chainKeyB64}:{messageNumber}";
        var sig = CryptoService.SignBytesB64(System.Text.Encoding.UTF8.GetBytes(payload), Profile.SigningSecretKey);
        var ctrlMsg = JsonSerializer.Serialize(new
        {
            __rede_ctrl = "senderkey",
            groupId = contextId,
            chainKey = chainKeyB64,
            messageNumber,
            sig,
        });
        Chat.SendMessage(memberId, ctrlMsg, 0);
    }

    /// <summary>Send our current sender key to specific members (e.g. a fresh invitee), bypassing the distributedTo check.</summary>
    private void SendOwnSenderKeyTo(string groupId, IEnumerable<string> memberIds)
    {
        if (Profile is null || Passphrase is null || Chat is null) return;
        var skStateJson = _store.LoadSenderKeyState(Profile, groupId);
        if (skStateJson is null) return;
        var stateRoot = JsonSerializer.Deserialize<JsonObject>(skStateJson.Value);
        if (stateRoot?["own"] is not JsonObject own) return;
        var chainKey = own["chainKey"]?.GetValue<string>();
        var messageNumber = own["messageNumber"]?.GetValue<int>() ?? 0;
        if (chainKey is null) return;

        var distributed = stateRoot["distributedTo"] as JsonArray ?? new JsonArray();
        var have = new HashSet<string>();
        foreach (var n in distributed)
            if (n?.GetValue<string>() is string s) have.Add(s);

        bool changed = false;
        foreach (var memberId in memberIds)
        {
            if (memberId == Profile.UserId || !Profile.Contacts.ContainsKey(memberId)) continue;
            SendSenderKeyCtrl(groupId, memberId, chainKey, messageNumber);
            if (!have.Contains(memberId)) { distributed.Add(JsonValue.Create(memberId)); have.Add(memberId); changed = true; }
        }
        if (changed)
        {
            stateRoot["distributedTo"] = distributed;
            _store.SaveSenderKeyStateAsync(Profile, groupId, JsonSerializer.SerializeToElement(stateRoot), Passphrase);
        }
    }

    /// <summary>
    /// Accept a sender key received via ratcheted DM (__rede_ctrl = "senderkey").
    /// Verifies group membership and the Ed25519 signature before storing.
    /// </summary>
    public void AcceptSenderKey(string contextId, string chainKeyB64, int messageNumber, string sigB64, string from)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.Groups.TryGetValue(contextId, out var group)) return;

        if (group.Members is null || group.Members.Count == 0 || !group.Members.Contains(from))
        {
            OnSystemMessage?.Invoke($"[SECURITY] Sender key from non-member {from} for group - rejected.");
            return;
        }
        if (!Profile.Contacts.TryGetValue(from, out var contact) || contact.SigningKey is null) return;

        if (messageNumber < 0 || messageNumber >= SenderKeys.MaxMessageNumber) return;
        byte[] ck;
        try { ck = Convert.FromBase64String(chainKeyB64); } catch { return; }
        if (ck.Length != 32) return;

        var payload = $"SENDERKEY:{contextId}:{chainKeyB64}:{messageNumber}";
        if (!CryptoService.VerifyBytes(System.Text.Encoding.UTF8.GetBytes(payload), sigB64, contact.SigningKey))
        {
            OnSystemMessage?.Invoke($"[SECURITY] Invalid sender key signature from {from} - rejected.");
            return;
        }

        StoreMemberSenderKey(contextId, from, chainKeyB64, messageNumber);
        OnSystemMessage?.Invoke($"Received sender key for group from {from}");
    }

    /// <summary>
    /// Accept a signed member-list update (__rede_ctrl = "groupmembers"). Union-merge only —
    /// members are never removed via this path (kicks go through GROUP_KICK).
    /// </summary>
    public void AcceptGroupMembers(string groupId, List<string> members, string sigB64, string from)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.Groups.TryGetValue(groupId, out var group)) return;

        if (group.Members is not null && group.Members.Count > 0 && !group.Members.Contains(from))
        {
            OnSystemMessage?.Invoke($"[SECURITY] Member list update from non-member {from} - rejected.");
            return;
        }
        if (!Profile.Contacts.TryGetValue(from, out var contact) || contact.SigningKey is null) return;
        if (members.Count > 256) return;

        var payload = MembersSigPayload(groupId, members);
        if (!CryptoService.VerifyBytes(System.Text.Encoding.UTF8.GetBytes(payload), sigB64, contact.SigningKey))
        {
            OnSystemMessage?.Invoke($"[SECURITY] Invalid member list signature from {from} - rejected.");
            return;
        }

        group.Members ??= new List<string>();
        var added = new List<string>();
        foreach (var m in members)
        {
            if (!group.Members.Contains(m)) { group.Members.Add(m); added.Add(m); }
        }
        if (added.Count == 0) return;

        _store.SaveProfileDebounced(Profile, Passphrase);
        OnSystemMessage?.Invoke($"Group member list updated by {from} (+{added.Count})");
        OnGroupsChanged?.Invoke();

        // Redistribute our own sender key so the new members can decrypt us
        SendOwnSenderKeyTo(groupId, added);
    }

    internal void StoreMemberSenderKey(string contextKey, string from, string chainKeyB64, int messageNumber)
    {
        if (Profile is null || Passphrase is null) return;
        var skStateJson = _store.LoadSenderKeyState(Profile, contextKey);
        var stateRoot = skStateJson is not null
            ? JsonSerializer.Deserialize<JsonObject>(skStateJson.Value) ?? new JsonObject()
            : new JsonObject();
        var membersNode = stateRoot["members"] as JsonObject ?? new JsonObject();
        membersNode[from] = new JsonObject
        {
            ["chainKey"] = chainKeyB64,
            ["messageNumber"] = messageNumber,
        };
        stateRoot["members"] = membersNode;
        _store.SaveSenderKeyStateAsync(Profile, contextKey, JsonSerializer.SerializeToElement(stateRoot), Passphrase);
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
        if (groupId is null) return;

        // The signed groupkey DM may have arrived first — never overwrite an
        // existing group (would wipe the real key and the member list)
        if (Profile.Groups.ContainsKey(groupId))
        {
            OnSystemMessage?.Invoke($"You were invited to group '{name}'");
            return;
        }

        var key = CryptoService.GenerateSymmetricKey();
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

        // Drop the kicked member's sender key and forget that ours was distributed
        // to them — after a /rekey they must not decrypt us again
        var skStateJson = _store.LoadSenderKeyState(Profile, groupId);
        if (skStateJson is not null)
        {
            var stateRoot = JsonSerializer.Deserialize<JsonObject>(skStateJson.Value);
            if (stateRoot is not null)
            {
                bool changed = false;
                if (stateRoot["members"] is JsonObject membersNode && membersNode.ContainsKey(userId))
                {
                    membersNode.Remove(userId);
                    changed = true;
                }
                if (stateRoot["distributedTo"] is JsonArray distArr)
                {
                    for (int i = distArr.Count - 1; i >= 0; i--)
                    {
                        if (distArr[i]?.GetValue<string>() == userId) { distArr.RemoveAt(i); changed = true; }
                    }
                }
                if (changed)
                    _store.SaveSenderKeyStateAsync(Profile, groupId, JsonSerializer.SerializeToElement(stateRoot), Passphrase);
            }
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
            // Echo of a message sent by ANOTHER of our own devices — not ours to
            // dequeue (would steal a FIFO slot and misalign every following msgId).
            // Displaying it is not supported yet: sender keys are never distributed
            // to own sibling devices (multi-device gap, same as v1).
            var fromDeviceId = ProtocolSerializer.GetString(msg, "fromDeviceId");
            if (fromDeviceId is not null && Profile.DeviceId is not null && fromDeviceId != Profile.DeviceId)
                return;

            // Server echoes own GROUP_MESSAGE back with a server-assigned msgId in send order.
            // Dequeue exactly one slot per echo. Sentinel entries (Msg=null) from control
            // messages get consumed silently; regular messages get their MsgId stamped once.
            var ownMsgId = ProtocolSerializer.GetString(msg, "msgId");
            if (ownMsgId is null) return;

            (string GroupId, ChatMessage? Msg) entry;
            lock (_pendingAckLock)
            {
                if (_pendingAck.Count == 0) return;
                entry = _pendingAck.Dequeue();
            }
            if (entry.Msg is null) return; // control-message sentinel
            if (entry.Msg.MsgId is not null) return; // defensive — already stamped
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
        var text = MessageEnvelope.Decode(plaintext, out var replyToMsgId, out var replyToPreview, out var replyToAuthor, out var attachments);

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
            Attachments = attachments,
        };

        _store.AddChatMessage(Profile, groupId, chatMsg, Passphrase);
        OnGroupMessageReceived?.Invoke(groupId, from, sanitized, ts, chatMsg);
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
