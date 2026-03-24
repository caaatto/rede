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
public class GroupService
{
    private readonly RedeConnection _conn;
    private readonly ProfileStore _store;

    public Profile? Profile { get; set; }
    public string? Passphrase { get; set; }

    public event Action<string>? OnSystemMessage;
    public event Action<string, string, string, DateTime>? OnGroupMessageReceived; // groupId, from, text, ts
    public event Action? OnGroupsChanged;

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
        var groupKey = CryptoService.GenerateGroupKey();
        _conn.Send(Msg.GroupCreate, ProtocolSerializer.Payload(
            ("name", JsonValue.Create(name)),
            ("key", JsonValue.Create(groupKey))
        ));
    }

    public void InviteToGroup(string groupId, string userId)
    {
        _conn.Send(Msg.GroupInvite, ProtocolSerializer.Payload(
            ("groupId", JsonValue.Create(groupId)),
            ("userId", JsonValue.Create(userId))
        ));
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

        var newKey = CryptoService.GenerateGroupKey();
        group.Key = newKey;
        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));

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
                chatService.SendMessage(memberId, keyMsg, 120);
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
            skState = new SenderKeys.SenderKeyState
            {
                ChainKey = ownNode.GetProperty("chainKey").GetString() ?? "",
                MessageNumber = ownNode.GetProperty("messageNumber").GetInt32(),
            };
        }
        else
        {
            skState = SenderKeys.Generate();
        }

        var result = SenderKeys.Encrypt(skState, text, Profile.SigningSecretKey);

        // Save updated state
        var stateObj = new JsonObject
        {
            ["own"] = new JsonObject
            {
                ["chainKey"] = skState.ChainKey,
                ["messageNumber"] = skState.MessageNumber,
            }
        };
        Task.Run(async () =>
        {
            var elem = JsonSerializer.SerializeToElement(stateObj);
            await _store.SaveSenderKeyStateAsync(Profile, groupId, elem, Passphrase);
        });

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
    }

    public IReadOnlyDictionary<string, Group>? GetGroups() => Profile?.Groups;

    // --- Handlers ---

    private async void HandleGroupCreateOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var groupId = ProtocolSerializer.GetString(msg, "groupId");
        var name = ProtocolSerializer.GetString(msg, "name");
        if (groupId is null || name is null) return;

        var key = CryptoService.GenerateGroupKey();
        await _store.AddGroupAsync(Profile, groupId, name, key, new List<string> { Profile.UserId }, Passphrase);

        OnSystemMessage?.Invoke($"Group '{name}' created ({groupId})");
        OnGroupsChanged?.Invoke();
    }

    private async void HandleGroupInvite(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var groupId = ProtocolSerializer.GetString(msg, "groupId");
        var name = ProtocolSerializer.GetString(msg, "name") ?? groupId ?? "unnamed";
        var key = ProtocolSerializer.GetString(msg, "key") ?? CryptoService.GenerateGroupKey();

        if (groupId is null) return;

        await _store.AddGroupAsync(Profile, groupId, name, key, null, Passphrase);
        OnSystemMessage?.Invoke($"You were invited to group '{name}'");
        OnGroupsChanged?.Invoke();
    }

    private void HandleGroupKickOk(JsonObject msg)
    {
        var groupId = ProtocolSerializer.GetString(msg, "groupId");
        var userId = ProtocolSerializer.GetString(msg, "userId");
        OnSystemMessage?.Invoke($"Removed {userId} from group {groupId}");
    }

    private void HandleGroupMessage(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var groupId = ProtocolSerializer.GetString(msg, "groupId");
        var from = ProtocolSerializer.GetString(msg, "from");
        if (groupId is null || from is null) return;
        if (from == Profile.UserId) return; // Skip own messages

        var encrypted = ProtocolSerializer.GetString(msg, "encrypted");
        var nonce = ProtocolSerializer.GetString(msg, "nonce");
        if (encrypted is null || nonce is null) return;

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
                    memberState = new SenderKeys.SenderKeyState
                    {
                        ChainKey = memberData.GetProperty("chainKey").GetString() ?? "",
                        MessageNumber = memberData.GetProperty("messageNumber").GetInt32(),
                    };
                }
                else return; // No sender key for this member
            }
            catch { return; }
        }
        else return;

        var plaintext = SenderKeys.Decrypt(memberState, encrypted, nonce, messageNumber, signature, signingKey);
        if (plaintext is null) return;

        var ts = DateTimeOffset.FromUnixTimeMilliseconds(ProtocolSerializer.GetLong(msg, "ts")).LocalDateTime;

        Task.Run(async () => await _store.AddChatMessageAsync(Profile, groupId, new ChatMessage
        {
            From = from, Text = plaintext, Ts = ProtocolSerializer.GetLong(msg, "ts"),
            Ttl = ProtocolSerializer.GetInt(msg, "ttl"),
        }, Passphrase));

        OnGroupMessageReceived?.Invoke(groupId, from, plaintext, ts);
    }
}
