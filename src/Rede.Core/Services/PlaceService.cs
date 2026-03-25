using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rede.Core.Crypto;
using Rede.Core.Networking;
using Rede.Core.Protocol;
using Rede.Core.Storage;

namespace Rede.Core.Services;

/// <summary>
/// Place management: create, invite, kick, leave, channels, messaging.
/// Places are Discord-like servers with multiple channels.
/// Channel metadata (names, topics) is E2EE - server only sees opaque IDs.
/// </summary>
public class PlaceService
{
    private readonly RedeConnection _conn;
    private readonly ProfileStore _store;
    private readonly NonceTracker _nonceTracker = new();

    public Profile? Profile { get; set; }
    public string? Passphrase { get; set; }

    public event Action<string>? OnSystemMessage;
    public event Action<string, string, string, string, DateTime>? OnChannelMessageReceived; // placeId, channelId, from, text, ts
    public event Action? OnPlacesChanged;
    public event Action<string, string, int>? OnChannelMessageSent; // placeId:channelId, text, ttl

    public PlaceService(RedeConnection conn, ProfileStore store)
    {
        _conn = conn;
        _store = store;
        RegisterHandlers();
    }

    private void RegisterHandlers()
    {
        _conn.On(Msg.PlaceCreateOk, HandlePlaceCreateOk);
        _conn.On(Msg.PlaceInvite, HandlePlaceInvite);
        _conn.On(Msg.PlaceKickOk, HandlePlaceKickOk);
        _conn.On(Msg.PlaceLeaveOk, HandlePlaceLeaveOk);
        _conn.On(Msg.PlaceChannelAddOk, HandlePlaceChannelAddOk);
        _conn.On(Msg.PlaceChannelRemoveOk, HandlePlaceChannelRemoveOk);
        _conn.On(Msg.PlaceMessage, HandlePlaceMessage);
    }

    // --- Channel ID generation ---

    private static string DeriveChannelId(string placeId, string channelName, string nonce)
    {
        var input = $"{placeId}:{channelName}:{nonce}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    // --- Place metadata encryption ---
    // Metadata (name, channels, roles) is encrypted with a shared symmetric key.
    // Server never sees this data.

    private static string EncryptMetadata(Place place, string metadataKey)
    {
        var meta = new JsonObject
        {
            ["name"] = place.Name,
            ["channels"] = JsonSerializer.SerializeToNode(place.Channels),
            ["roles"] = JsonSerializer.SerializeToNode(place.Roles),
            ["creatorId"] = place.CreatorId,
        };
        var json = meta.ToJsonString();
        var keyBytes = Convert.FromBase64String(metadataKey);
        var nonce = Sodium.SodiumCore.GetRandomBytes(24);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var ciphertext = Sodium.SecretBox.Create(plaintext, nonce, keyBytes);
        CryptoService.ZeroOut(keyBytes);
        return Convert.ToBase64String(nonce) + ":" + Convert.ToBase64String(ciphertext);
    }

    private static bool DecryptMetadata(string encrypted, string metadataKey, Place place)
    {
        try
        {
            var parts = encrypted.Split(':', 2);
            if (parts.Length != 2) return false;
            var nonce = Convert.FromBase64String(parts[0]);
            var ciphertext = Convert.FromBase64String(parts[1]);
            var keyBytes = Convert.FromBase64String(metadataKey);
            var plaintext = Sodium.SecretBox.Open(ciphertext, nonce, keyBytes);
            CryptoService.ZeroOut(keyBytes);
            if (plaintext is null) return false;

            var json = Encoding.UTF8.GetString(plaintext);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            place.Name = root.GetProperty("name").GetString() ?? "";
            if (root.TryGetProperty("channels", out var chElem))
                place.Channels = JsonSerializer.Deserialize<Dictionary<string, PlaceChannel>>(chElem) ?? new();
            if (root.TryGetProperty("roles", out var rolesElem))
                place.Roles = JsonSerializer.Deserialize<Dictionary<string, PlaceRole>>(rolesElem) ?? new();
            if (root.TryGetProperty("creatorId", out var creatorElem))
                place.CreatorId = creatorElem.GetString() ?? "";
            return true;
        }
        catch { return false; }
    }

    // --- Public API ---

    public void CreatePlace(string name)
    {
        if (Profile is null) return;
        _conn.Send(Msg.PlaceCreate, ProtocolSerializer.Payload());
        // Name is stored only in encrypted metadata, not sent to server
        _pendingPlaceName = name;
    }

    private string? _pendingPlaceName;

    public void InviteToPlace(string placeId, string userId, ChatService? chatService = null)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        _conn.Send(Msg.PlaceInvite, ProtocolSerializer.Payload(
            ("placeId", JsonValue.Create(placeId)),
            ("inviteeId", JsonValue.Create(userId))
        ));

        if (chatService is not null && Profile.Contacts.ContainsKey(userId))
        {
            var encMeta = EncryptMetadata(place, place.MetadataKey);
            var keyMsg = JsonSerializer.Serialize(new
            {
                __rede_ctrl = "placekey",
                placeId,
                metadataKey = place.MetadataKey,
                metadata = encMeta,
            });
            chatService.SendMessage(userId, keyMsg, 0);
            OnSystemMessage?.Invoke($"Invited {userId} to \"{place.Name}\" - metadata key sent.");
        }
        else
        {
            OnSystemMessage?.Invoke($"Invited {userId} to \"{place.Name}\" (add them as contact first to send metadata key).");
        }
    }

    public void KickFromPlace(string placeId, string userId)
    {
        _conn.Send(Msg.PlaceKick, ProtocolSerializer.Payload(
            ("placeId", JsonValue.Create(placeId)),
            ("targetUserId", JsonValue.Create(userId))
        ));
    }

    public void LeavePlace(string placeId)
    {
        _conn.Send(Msg.PlaceLeave, ProtocolSerializer.Payload(
            ("placeId", JsonValue.Create(placeId))
        ));
    }

    public void CreateChannel(string placeId, string name, ChatService? chatService = null)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var channelId = DeriveChannelId(placeId, name, nonce);

        _conn.Send(Msg.PlaceChannelAdd, ProtocolSerializer.Payload(
            ("placeId", JsonValue.Create(placeId)),
            ("channelId", JsonValue.Create(channelId))
        ));

        place.Channels[channelId] = new PlaceChannel
        {
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));

        // Distribute updated metadata to all members
        DistributeMetadata(placeId, place, chatService);

        OnSystemMessage?.Invoke($"Channel #{name} created in \"{place.Name}\".");
        OnPlacesChanged?.Invoke();
    }

    public void RemoveChannel(string placeId, string channelId)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        _conn.Send(Msg.PlaceChannelRemove, ProtocolSerializer.Payload(
            ("placeId", JsonValue.Create(placeId)),
            ("channelId", JsonValue.Create(channelId))
        ));
    }

    public void RekeyPlace(string placeId, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        place.MetadataKey = CryptoService.GenerateGroupKey();
        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));

        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Metadata key rotated for \"{place.Name}\".");
    }

    private void DistributeMetadata(string placeId, Place place, ChatService? chatService)
    {
        if (Profile is null || chatService is null) return;

        var encMeta = EncryptMetadata(place, place.MetadataKey);
        foreach (var memberId in place.Members)
        {
            if (memberId == Profile.UserId) continue;
            if (!Profile.Contacts.ContainsKey(memberId)) continue;

            var keyMsg = JsonSerializer.Serialize(new
            {
                __rede_ctrl = "placekey",
                placeId,
                metadataKey = place.MetadataKey,
                metadata = encMeta,
            });
            chatService.SendMessage(memberId, keyMsg, 0);
        }
    }

    // --- Chat key: place:{placeId}:{channelId} ---

    private static string ChatKey(string placeId, string channelId)
        => $"place:{placeId}:{channelId}";

    private static string SenderKeyKey(string placeId, string channelId)
        => $"place:{placeId}:{channelId}";

    public void SendChannelMessage(string placeId, string channelId, string text, int ttl = 0)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found");
            return;
        }

        if (!place.Channels.ContainsKey(channelId))
        {
            OnSystemMessage?.Invoke("Channel not found");
            return;
        }

        var skKey = SenderKeyKey(placeId, channelId);
        var skStateJson = _store.LoadSenderKeyState(Profile, skKey);
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
            await _store.SaveSenderKeyStateAsync(Profile, skKey, elem, Passphrase);
        });

        var payload = ProtocolSerializer.Payload(
            ("placeId", JsonValue.Create(placeId)),
            ("channelId", JsonValue.Create(channelId)),
            ("encrypted", JsonValue.Create(result.Ciphertext)),
            ("nonce", JsonValue.Create(result.Nonce)),
            ("senderKeyHeader", new JsonObject
            {
                ["messageNumber"] = result.MessageNumber,
                ["signature"] = result.Signature,
            })
        );
        if (ttl > 0) payload["ttl"] = ttl;

        _conn.Send(Msg.PlaceMessage, payload);

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var chatKey = ChatKey(placeId, channelId);
        Task.Run(async () => await _store.AddChatMessageAsync(Profile, chatKey, new ChatMessage
        {
            From = Profile.UserId, Text = text, Ts = ts, Ttl = ttl,
        }, Passphrase));
        OnChannelMessageSent?.Invoke(chatKey, text, ttl);
    }

    public IReadOnlyDictionary<string, Place>? GetPlaces() => Profile?.Places;

    // --- Handlers ---

    private async void HandlePlaceCreateOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        if (placeId is null) return;

        var name = _pendingPlaceName ?? "Unnamed";
        _pendingPlaceName = null;

        var metadataKey = CryptoService.GenerateGroupKey();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var generalChannelId = DeriveChannelId(placeId, "general", nonce);

        // Register default channel on server
        _conn.Send(Msg.PlaceChannelAdd, ProtocolSerializer.Payload(
            ("placeId", JsonValue.Create(placeId)),
            ("channelId", JsonValue.Create(generalChannelId))
        ));

        var place = new Place
        {
            Name = name,
            MetadataKey = metadataKey,
            CreatorId = Profile.UserId,
            Members = new List<string> { Profile.UserId },
            Roles = new Dictionary<string, PlaceRole>
            {
                [Profile.UserId] = PlaceRole.Owner,
            },
            Channels = new Dictionary<string, PlaceChannel>
            {
                [generalChannelId] = new PlaceChannel
                {
                    Name = "general",
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            },
        };

        Profile.Places[placeId] = place;
        await _store.SaveProfileAsync(Profile, Passphrase);

        OnSystemMessage?.Invoke($"Place \"{name}\" created with #general channel.");
        OnPlacesChanged?.Invoke();
    }

    private async void HandlePlaceInvite(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        var from = ProtocolSerializer.GetString(msg, "from");
        if (placeId is null) return;

        // Create placeholder - real metadata comes via placekey DM
        if (!Profile.Places.ContainsKey(placeId))
        {
            Profile.Places[placeId] = new Place
            {
                Name = placeId, // Temporary until metadata arrives
                MetadataKey = "",
            };
            await _store.SaveProfileAsync(Profile, Passphrase);
        }

        OnSystemMessage?.Invoke($"You were invited to a Place by {from ?? "unknown"}");
        OnPlacesChanged?.Invoke();
    }

    private async void HandlePlaceKickOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        var targetUserId = ProtocolSerializer.GetString(msg, "targetUserId");
        if (placeId is null || targetUserId is null) return;

        if (Profile.Places.TryGetValue(placeId, out var place))
        {
            place.Members.Remove(targetUserId);
            place.Roles.Remove(targetUserId);
            await _store.SaveProfileAsync(Profile, Passphrase);
        }

        OnSystemMessage?.Invoke($"Removed {targetUserId} from place {placeId}");
    }

    private async void HandlePlaceLeaveOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        if (placeId is null) return;

        Profile.Places.Remove(placeId);
        await _store.SaveProfileAsync(Profile, Passphrase);

        OnSystemMessage?.Invoke("Left place.");
        OnPlacesChanged?.Invoke();
    }

    private void HandlePlaceChannelAddOk(JsonObject msg)
    {
        // Channel already added locally in CreateChannel
    }

    private async void HandlePlaceChannelRemoveOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        var channelId = ProtocolSerializer.GetString(msg, "channelId");
        if (placeId is null || channelId is null) return;

        if (Profile.Places.TryGetValue(placeId, out var place))
        {
            place.Channels.Remove(channelId);
            await _store.SaveProfileAsync(Profile, Passphrase);
        }

        OnPlacesChanged?.Invoke();
    }

    private void HandlePlaceMessage(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        var channelId = ProtocolSerializer.GetString(msg, "channelId");
        var from = ProtocolSerializer.GetString(msg, "from");
        if (placeId is null || channelId is null || from is null) return;
        if (from == Profile.UserId) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke($"Message for unknown place {placeId} - dropped.");
            return;
        }

        if (!place.Members.Contains(from))
        {
            OnSystemMessage?.Invoke($"[SECURITY] Non-member {from} sent to place {placeId} - dropped.");
            return;
        }

        var encrypted = ProtocolSerializer.GetString(msg, "encrypted");
        var nonce = ProtocolSerializer.GetString(msg, "nonce");
        if (encrypted is null || nonce is null) return;

        if (!_nonceTracker.Check(nonce)) return;

        var skHeader = msg["senderKeyHeader"];
        if (skHeader is null) return;

        var messageNumber = skHeader["messageNumber"]?.GetValue<int>() ?? 0;
        var signature = skHeader["signature"]?.GetValue<string>();
        if (signature is null) return;

        if (!Profile.Contacts.TryGetValue(from, out var contact))
        {
            OnSystemMessage?.Invoke($"Unknown sender in place: {from}");
            return;
        }

        var signingKey = contact.SigningKey;
        if (signingKey is null) return;

        var skKey = SenderKeyKey(placeId, channelId);
        var skStateJson = _store.LoadSenderKeyState(Profile, skKey);
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
                else return;
            }
            catch { return; }
        }
        else return;

        var plaintext = SenderKeys.Decrypt(memberState, encrypted, nonce, messageNumber, signature, signingKey);
        if (plaintext is null) return;

        // Save updated sender key state
        {
            var parsed = JsonSerializer.Deserialize<JsonObject>(skStateJson.Value);
            if (parsed is not null)
            {
                var membersNode = parsed["members"] as JsonObject ?? new JsonObject();
                membersNode[from] = new JsonObject
                {
                    ["chainKey"] = memberState.ChainKey,
                    ["messageNumber"] = memberState.MessageNumber,
                };
                parsed["members"] = membersNode;
                var elem = JsonSerializer.SerializeToElement(parsed);
                Task.Run(async () => await _store.SaveSenderKeyStateAsync(Profile, skKey, elem, Passphrase));
            }
        }

        var sanitized = ChatService.EscapeContent(plaintext);
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(ProtocolSerializer.GetLong(msg, "ts")).LocalDateTime;
        var chatKey = ChatKey(placeId, channelId);

        Task.Run(async () => await _store.AddChatMessageAsync(Profile, chatKey, new ChatMessage
        {
            From = from, Text = sanitized, Ts = ProtocolSerializer.GetLong(msg, "ts"),
            Ttl = ProtocolSerializer.GetInt(msg, "ttl"),
        }, Passphrase));

        OnChannelMessageReceived?.Invoke(placeId, channelId, from, sanitized, ts);
    }

    /// <summary>
    /// Called when a placekey control message arrives via ratcheted DM.
    /// Decrypts metadata and updates local Place.
    /// </summary>
    public async Task HandlePlaceKeyReceived(string placeId, string metadataKey, string encryptedMetadata, string senderId)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            place = new Place();
            Profile.Places[placeId] = place;
        }

        place.MetadataKey = metadataKey;

        if (DecryptMetadata(encryptedMetadata, metadataKey, place))
        {
            // Add ourselves to local member list if not present
            if (!place.Members.Contains(Profile.UserId))
                place.Members.Add(Profile.UserId);

            await _store.SaveProfileAsync(Profile, Passphrase);
            OnSystemMessage?.Invoke($"Received metadata for \"{place.Name}\"");
            OnPlacesChanged?.Invoke();
        }
        else
        {
            OnSystemMessage?.Invoke($"Failed to decrypt place metadata from {senderId}");
        }
    }
}
