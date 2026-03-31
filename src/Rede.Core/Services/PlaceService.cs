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
        _conn.On(Msg.PlaceRoleSetOk, HandlePlaceRoleSetOk);
        _conn.On(Msg.PlaceBanOk, HandlePlaceBanOk);
        _conn.On(Msg.PlaceUnbanOk, HandlePlaceUnbanOk);
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
        if (place.AccentColor is not null) meta["accentColor"] = place.AccentColor;
        if (place.IconData is not null) meta["iconData"] = place.IconData;
        if (place.IconMimeType is not null) meta["iconMimeType"] = place.IconMimeType;
        if (place.Emotes.Count > 0) meta["emotes"] = JsonSerializer.SerializeToNode(place.Emotes);
        if (place.Bans.Count > 0) meta["bans"] = JsonSerializer.SerializeToNode(place.Bans);
        if (place.Categories.Count > 0) meta["categories"] = JsonSerializer.SerializeToNode(place.Categories);
        meta["ownerColor"] = place.OwnerColor;
        meta["adminColor"] = place.AdminColor;
        meta["memberColor"] = place.MemberColor;
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
            if (root.TryGetProperty("accentColor", out var acElem))
                place.AccentColor = acElem.GetString();
            if (root.TryGetProperty("iconData", out var iconElem))
                place.IconData = iconElem.GetString();
            if (root.TryGetProperty("iconMimeType", out var iconMimeElem))
                place.IconMimeType = iconMimeElem.GetString();
            if (root.TryGetProperty("emotes", out var emotesElem))
                place.Emotes = JsonSerializer.Deserialize<Dictionary<string, PlaceEmote>>(emotesElem) ?? new();
            if (root.TryGetProperty("bans", out var bansElem))
                place.Bans = JsonSerializer.Deserialize<Dictionary<string, PlaceBan>>(bansElem) ?? new();
            if (root.TryGetProperty("categories", out var catsElem))
                place.Categories = JsonSerializer.Deserialize<List<string>>(catsElem) ?? new();
            if (root.TryGetProperty("ownerColor", out var ownerColorElem))
                place.OwnerColor = ownerColorElem.GetString() ?? "#eab308";
            if (root.TryGetProperty("adminColor", out var adminColorElem))
                place.AdminColor = adminColorElem.GetString() ?? "#8b5cf6";
            if (root.TryGetProperty("memberColor", out var memberColorElem))
                place.MemberColor = memberColorElem.GetString() ?? "#6b7280";
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

    public void UpdatePlaceProfile(string placeId, string? accentColor, string? iconData, string? iconMimeType, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        place.AccentColor = accentColor;
        place.IconData = iconData;
        place.IconMimeType = iconMimeType;
        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));

        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Place profile updated for \"{place.Name}\".");
    }

    public void UpdateRoleColors(string placeId, string ownerColor, string adminColor, string memberColor, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("Only the owner or admins can change role colors.");
            return;
        }

        place.OwnerColor = ownerColor;
        place.AdminColor = adminColor;
        place.MemberColor = memberColor;
        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));

        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Role colors updated for \"{place.Name}\".");
        OnPlacesChanged?.Invoke();
    }

    // --- Permission helpers ---

    public static bool HasPermission(Place place, string userId, PlaceRole minRole)
    {
        if (place.CreatorId == userId) return true; // Owner always has all permissions
        if (!place.Roles.TryGetValue(userId, out var role)) role = PlaceRole.Member;
        return role >= minRole;
    }

    // --- Emote management ---
    // Emotes are stored in E2EE metadata — server never sees them.
    // Max 50 emotes per place, max 64KB per emote image.

    private const int MaxEmotesPerPlace = 50;
    private const int MaxEmoteSize = 64 * 1024; // 64KB

    public void AddEmote(string placeId, string name, byte[] imageData, string mimeType, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("Only the owner or admins can add emotes.");
            return;
        }

        if (place.Emotes.Count >= MaxEmotesPerPlace)
        {
            OnSystemMessage?.Invoke($"Emote limit reached ({MaxEmotesPerPlace}).");
            return;
        }

        if (imageData.Length > MaxEmoteSize)
        {
            OnSystemMessage?.Invoke("Emote image too large (max 64KB).");
            return;
        }

        // Sanitize name: alphanumeric + underscores, 2-32 chars
        var safeName = System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9_]", "");
        if (safeName.Length < 2 || safeName.Length > 32)
        {
            OnSystemMessage?.Invoke("Emote name must be 2-32 alphanumeric characters.");
            return;
        }

        var emoteId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        place.Emotes[emoteId] = new PlaceEmote
        {
            Name = safeName,
            ImageData = Convert.ToBase64String(imageData),
            MimeType = mimeType,
            UploadedBy = Profile.UserId,
        };

        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));
        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Emote :{safeName}: added to \"{place.Name}\".");
        OnPlacesChanged?.Invoke();
    }

    public void RemoveEmote(string placeId, string emoteId, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("Only the owner or admins can remove emotes.");
            return;
        }

        if (!place.Emotes.Remove(emoteId))
        {
            OnSystemMessage?.Invoke("Emote not found.");
            return;
        }

        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));
        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke("Emote removed.");
        OnPlacesChanged?.Invoke();
    }

    // --- Role management ---
    // Owner can promote members to Admin or demote Admins to Member.
    // Role change is both server-side (for permission enforcement) and E2EE metadata (for display).

    public void SetRole(string placeId, string targetUserId, PlaceRole role, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        if (place.CreatorId != Profile.UserId)
        {
            OnSystemMessage?.Invoke("Only the place owner can change roles.");
            return;
        }

        if (targetUserId == Profile.UserId)
        {
            OnSystemMessage?.Invoke("Cannot change your own role.");
            return;
        }

        if (!place.Members.Contains(targetUserId))
        {
            OnSystemMessage?.Invoke("User is not a member of this place.");
            return;
        }

        // Send to server for enforcement
        var serverRole = role >= PlaceRole.Admin ? "admin" : "member";
        _conn.Send(Msg.PlaceRoleSet, ProtocolSerializer.Payload(
            ("placeId", JsonValue.Create(placeId)),
            ("targetUserId", JsonValue.Create(targetUserId)),
            ("role", JsonValue.Create(serverRole))
        ));

        // Update local E2EE metadata
        place.Roles[targetUserId] = role;
        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));
        DistributeMetadata(placeId, place, chatService);

        var roleName = role switch { PlaceRole.Admin => "Admin", PlaceRole.Owner => "Owner", _ => "Member" };
        OnSystemMessage?.Invoke($"Set {targetUserId} to {roleName} in \"{place.Name}\".");
    }

    // --- Ban / Unban ---
    // Bans are stored both server-side (for enforcement) and E2EE metadata (for display).

    public void BanUser(string placeId, string targetUserId, string? reason, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("Only the owner or admins can ban users.");
            return;
        }

        if (targetUserId == Profile.UserId)
        {
            OnSystemMessage?.Invoke("Cannot ban yourself.");
            return;
        }

        if (targetUserId == place.CreatorId)
        {
            OnSystemMessage?.Invoke("Cannot ban the owner.");
            return;
        }

        _conn.Send(Msg.PlaceBan, ProtocolSerializer.Payload(
            ("placeId", JsonValue.Create(placeId)),
            ("targetUserId", JsonValue.Create(targetUserId)),
            ("reason", JsonValue.Create(reason ?? ""))
        ));

        // Update local state
        place.Bans[targetUserId] = new PlaceBan
        {
            UserId = targetUserId,
            BannedBy = Profile.UserId,
            Reason = reason,
            BannedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        place.Members.Remove(targetUserId);
        place.Roles.Remove(targetUserId);
        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));

        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Banned {targetUserId} from \"{place.Name}\".");
        OnPlacesChanged?.Invoke();
    }

    public void UnbanUser(string placeId, string targetUserId, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("Only the owner or admins can unban users.");
            return;
        }

        _conn.Send(Msg.PlaceUnban, ProtocolSerializer.Payload(
            ("placeId", JsonValue.Create(placeId)),
            ("targetUserId", JsonValue.Create(targetUserId))
        ));

        place.Bans.Remove(targetUserId);
        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));

        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Unbanned {targetUserId} from \"{place.Name}\".");
    }

    // --- Channel categories ---

    public void SetChannelCategory(string placeId, string channelId, string? category, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("Only the owner or admins can manage categories.");
            return;
        }

        if (!place.Channels.TryGetValue(channelId, out var channel))
        {
            OnSystemMessage?.Invoke("Channel not found.");
            return;
        }

        channel.Category = category;

        // Ensure category is in the list
        if (category is not null && !place.Categories.Contains(category))
            place.Categories.Add(category);

        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));
        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Channel #{channel.Name} moved to category \"{category ?? "None"}\".");
        OnPlacesChanged?.Invoke();
    }

    public void AddCategory(string placeId, string categoryName, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("Only the owner or admins can manage categories.");
            return;
        }

        if (place.Categories.Contains(categoryName))
        {
            OnSystemMessage?.Invoke("Category already exists.");
            return;
        }

        place.Categories.Add(categoryName);
        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));
        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Category \"{categoryName}\" added to \"{place.Name}\".");
        OnPlacesChanged?.Invoke();
    }

    public void RemoveCategory(string placeId, string categoryName, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("Only the owner or admins can manage categories.");
            return;
        }

        place.Categories.Remove(categoryName);
        // Move channels in that category to uncategorized
        foreach (var ch in place.Channels.Values)
        {
            if (ch.Category == categoryName) ch.Category = null;
        }

        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));
        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Category \"{categoryName}\" removed.");
        OnPlacesChanged?.Invoke();
    }

    // --- Channel topic ---

    public void SetChannelTopic(string placeId, string channelId, string topic, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;

        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }

        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("Only the owner or admins can set channel topics.");
            return;
        }

        if (!place.Channels.TryGetValue(channelId, out var channel))
        {
            OnSystemMessage?.Invoke("Channel not found.");
            return;
        }

        channel.Topic = topic;
        Task.Run(async () => await _store.SaveProfileAsync(Profile, Passphrase));
        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Topic set for #{channel.Name}.");
        OnPlacesChanged?.Invoke();
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

    private async void HandlePlaceRoleSetOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        var targetUserId = ProtocolSerializer.GetString(msg, "targetUserId");
        var role = ProtocolSerializer.GetString(msg, "role");
        var from = ProtocolSerializer.GetString(msg, "from");
        if (placeId is null || targetUserId is null || role is null) return;

        // If we are the target, update our local role
        if (targetUserId == Profile.UserId && Profile.Places.TryGetValue(placeId, out var place))
        {
            var newRole = role == "admin" ? PlaceRole.Admin : PlaceRole.Member;
            place.Roles[Profile.UserId] = newRole;
            await _store.SaveProfileAsync(Profile, Passphrase);
            var roleName = newRole == PlaceRole.Admin ? "Admin" : "Member";
            OnSystemMessage?.Invoke($"Your role in \"{place.Name}\" was changed to {roleName} by {from ?? "owner"}.");
            OnPlacesChanged?.Invoke();
        }
    }

    private void HandlePlaceBanOk(JsonObject msg)
    {
        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        var targetUserId = ProtocolSerializer.GetString(msg, "targetUserId");
        if (placeId is null || targetUserId is null) return;
        OnSystemMessage?.Invoke($"Banned {targetUserId} from place {placeId}.");
    }

    private void HandlePlaceUnbanOk(JsonObject msg)
    {
        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        var targetUserId = ProtocolSerializer.GetString(msg, "targetUserId");
        if (placeId is null || targetUserId is null) return;
        OnSystemMessage?.Invoke($"Unbanned {targetUserId} from place {placeId}.");
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
