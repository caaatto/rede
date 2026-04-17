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
public class PlaceService : IDisposable
{
    private readonly RedeConnection _conn;
    private readonly ProfileStore _store;
    private readonly NonceTracker _nonceTracker = new();

    // ACK FIFO: every persisted outgoing channel message pushes its ChatMessage reference.
    // Server echoes PLACE_MESSAGE back to sender with a server-assigned msgId, in send order.
    // Pairing via FIFO is the only reliable method — scanning ChatHistory for "first own
    // null-MsgId" collides with orphan legacy entries from pre-fix versions.
    private readonly Queue<(string ChatKey, ChatMessage Msg)> _pendingAck = new();
    private readonly object _pendingAckLock = new();

    /// <summary>Replay-protection tracker — exposed for persistence across restarts.</summary>
    public NonceTracker NonceTracker => _nonceTracker;

    public void Dispose() { GC.SuppressFinalize(this); }

    public Profile? Profile { get; set; }
    public byte[]? Passphrase { get; set; }

    public event Action<string>? OnSystemMessage;
    public event Action<string, string, string, string, DateTime, ChatMessage?>? OnChannelMessageReceived; // placeId, channelId, from, text, ts, fullMsg
    public event Action? OnPlacesChanged;
    public event Action<string, string, int>? OnChannelMessageSent; // placeId:channelId, text, ttl
    public event Action<string, string, string, Dictionary<string, List<string>>>? OnReactionUpdated; // chatKey, msgId, emoji, reactions
    public event Action<string, string, string>? OnMessageEdited; // chatKey, msgId, newText
    public event Action<string, string>? OnMessageDeleted; // chatKey, msgId
    public event Action<string, string>? OnOwnMessageIdAssigned; // chatKey, msgId

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

    private static string EncryptMetadata(Place place, byte[] metadataKey)
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
        var nonce = Sodium.SodiumCore.GetRandomBytes(24);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var ciphertext = Sodium.SecretBox.Create(plaintext, nonce, metadataKey);
        return Convert.ToBase64String(nonce) + ":" + Convert.ToBase64String(ciphertext);
    }

    // H1: Maximum decrypted metadata size (5MB) to prevent OOM
    private const int MaxMetadataSize = 5 * 1024 * 1024;

    private static bool DecryptMetadata(string encrypted, byte[] metadataKey, Place place)
    {
        try
        {
            // H1: Reject oversized encrypted metadata before decrypting
            if (encrypted.Length > MaxMetadataSize * 2) return false;
            var parts = encrypted.Split(':', 2);
            if (parts.Length != 2) return false;
            var nonce = Convert.FromBase64String(parts[0]);
            var ciphertext = Convert.FromBase64String(parts[1]);
            var plaintext = Sodium.SecretBox.Open(ciphertext, nonce, metadataKey);
            if (plaintext is null) return false;

            var json = Encoding.UTF8.GetString(plaintext);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // M10: Use TryGetProperty to avoid exception on missing fields
            if (!root.TryGetProperty("name", out var nameElem)) return false;
            place.Name = nameElem.GetString() ?? "";
            if (root.TryGetProperty("channels", out var chElem))
            {
                var channels = JsonSerializer.Deserialize<Dictionary<string, PlaceChannel>>(chElem) ?? new();
                // M15: Sanitize deserialized channel data (including bidi overrides)
                foreach (var ch in channels.Values)
                {
                    ch.Name = SanitizeMetadataString(ch.Name, 64);
                    ch.Topic = SanitizeMetadataString(ch.Topic, 200);
                }
                place.Channels = channels;
            }
            if (root.TryGetProperty("roles", out var rolesElem))
                place.Roles = JsonSerializer.Deserialize<Dictionary<string, PlaceRole>>(rolesElem) ?? new();
            if (root.TryGetProperty("creatorId", out var creatorElem))
                place.CreatorId = creatorElem.GetString() ?? "";
            if (root.TryGetProperty("accentColor", out var acElem))
                place.AccentColor = ValidateColor(acElem.GetString(), "#8b5cf6");
            if (root.TryGetProperty("iconData", out var iconElem))
            {
                var iconStr = iconElem.GetString();
                place.IconData = iconStr is not null && iconStr.Length <= 350_000 ? iconStr : null;
            }
            if (root.TryGetProperty("iconMimeType", out var iconMimeElem))
                place.IconMimeType = iconMimeElem.GetString();
            if (root.TryGetProperty("emotes", out var emotesElem))
            {
                var emotes = JsonSerializer.Deserialize<Dictionary<string, PlaceEmote>>(emotesElem) ?? new();
                // Cap emote count AND per-emote image size (64KB base64 ≈ 87KB)
                var filtered = new Dictionary<string, PlaceEmote>();
                foreach (var (k, e) in emotes)
                {
                    if (filtered.Count >= 50) break;
                    if (e.ImageData is not null && e.ImageData.Length > 87_000) continue;
                    filtered[k] = e;
                }
                place.Emotes = filtered;
            }
            if (root.TryGetProperty("bans", out var bansElem))
            {
                var bans = JsonSerializer.Deserialize<Dictionary<string, PlaceBan>>(bansElem) ?? new();
                // Cap bans count + truncate reasons
                if (bans.Count > 1000)
                    bans = bans.Take(1000).ToDictionary(b => b.Key, b => b.Value);
                foreach (var ban in bans.Values)
                    if (ban.Reason is not null && ban.Reason.Length > 200) ban.Reason = ban.Reason[..200];
                place.Bans = bans;
            }
            if (root.TryGetProperty("categories", out var catsElem))
            {
                var cats = JsonSerializer.Deserialize<List<string>>(catsElem) ?? new();
                // M16: Cap categories count
                if (cats.Count > 100) cats = cats.Take(100).ToList();
                place.Categories = cats;
            }
            // Validate color hex format — reject invalid colors with safe fallbacks
            if (root.TryGetProperty("ownerColor", out var ownerColorElem))
                place.OwnerColor = ValidateColor(ownerColorElem.GetString(), "#eab308");
            if (root.TryGetProperty("adminColor", out var adminColorElem))
                place.AdminColor = ValidateColor(adminColorElem.GetString(), "#8b5cf6");
            if (root.TryGetProperty("memberColor", out var memberColorElem))
                place.MemberColor = ValidateColor(memberColorElem.GetString(), "#6b7280");
            return true;
        }
        catch { return false; }
    }

    // --- Public API ---

    public void CreatePlace(string name)
    {
        if (Profile is null) return;
        _conn.Send(Msg.PlaceCreate, ProtocolSerializer.Payload());
        // H10: Use queue instead of single-slot to handle rapid creation
        _pendingPlaceNames.Enqueue(name);
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingPlaceNames = new();

    // M16: Any member can invite — intentional (Discord-like default, no InviteMembers permission).
    // Server also allows any member to invite. Metadata key is sent via E2EE DM.
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
                metadataKey = Convert.ToBase64String(place.MetadataKey),
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
        if (Profile is null) return;
        if (!Profile.Places.TryGetValue(placeId, out var place))
        {
            OnSystemMessage?.Invoke("Place not found.");
            return;
        }
        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("You don't have permission to kick members.");
            return;
        }
        if (userId == place.CreatorId)
        {
            OnSystemMessage?.Invoke("Cannot kick the place owner.");
            return;
        }
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

        // Permission check: admin+ required to create channels
        if (!HasPermission(place, Profile.UserId!, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("Only admins can create channels.");
            return;
        }

        // Sanitize channel name
        name = SanitizeMetadataString(name, 64);
        if (string.IsNullOrEmpty(name))
        {
            OnSystemMessage?.Invoke("Channel name is required.");
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
        _store.SaveProfileDebounced(Profile, Passphrase);

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

        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("You don't have permission to remove channels.");
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

        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("You don't have permission to edit the place profile.");
            return;
        }

        // M7: Validate on sending side too
        place.AccentColor = ValidateColor(accentColor, place.AccentColor ?? "#8b5cf6");
        if (iconData is not null && iconData.Length > 350_000) return; // reject oversized icons
        place.IconData = iconData;
        place.IconMimeType = iconMimeType;
        _store.SaveProfileDebounced(Profile, Passphrase);

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

        // M8: Validate colors on sending side
        place.OwnerColor = ValidateColor(ownerColor, "#eab308");
        place.AdminColor = ValidateColor(adminColor, "#8b5cf6");
        place.MemberColor = ValidateColor(memberColor, "#6b7280");
        _store.SaveProfileDebounced(Profile, Passphrase);

        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Role colors updated for \"{place.Name}\".");
        OnPlacesChanged?.Invoke();
    }

    /// <summary>Pin a message in a channel. Admin+ only. Max 50 pins per channel.</summary>
    public void PinMessage(string placeId, string channelId, string msgId, string preview, string author, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.Places.TryGetValue(placeId, out var place)) return;
        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("Only admins can pin messages.");
            return;
        }

        if (!place.Pins.TryGetValue(channelId, out var pins))
        {
            pins = new List<PlacePin>();
            place.Pins[channelId] = pins;
        }

        if (pins.Count >= 50)
        {
            OnSystemMessage?.Invoke("Maximum 50 pins per channel.");
            return;
        }

        if (pins.Any(p => p.MsgId == msgId)) return; // Already pinned

        pins.Add(new PlacePin
        {
            MsgId = msgId,
            PinnedBy = Profile.UserId,
            PinnedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Preview = preview.Length > 100 ? preview[..100] : preview,
            ChannelId = channelId,
            Author = author,
        });
        _store.SaveProfileDebounced(Profile, Passphrase);
        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke("Message pinned.");
        OnPlacesChanged?.Invoke();
    }

    /// <summary>Unpin a message. Admin+ only.</summary>
    public void UnpinMessage(string placeId, string channelId, string msgId, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.Places.TryGetValue(placeId, out var place)) return;
        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin)) return;

        if (place.Pins.TryGetValue(channelId, out var pins))
        {
            pins.RemoveAll(p => p.MsgId == msgId);
            if (pins.Count == 0) place.Pins.Remove(channelId);
            _store.SaveProfileDebounced(Profile, Passphrase);
            DistributeMetadata(placeId, place, chatService);
            OnSystemMessage?.Invoke("Message unpinned.");
            OnPlacesChanged?.Invoke();
        }
    }

    /// <summary>Set a nickname for a user in a place. User can set own, admin can set any.</summary>
    public void SetNickname(string placeId, string userId, string? nickname, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.Places.TryGetValue(placeId, out var place)) return;

        // User can set own nickname, admin can set anyone's
        if (userId != Profile.UserId && !HasPermission(place, Profile.UserId, PlaceRole.Admin))
        {
            OnSystemMessage?.Invoke("You can only set your own nickname.");
            return;
        }

        if (string.IsNullOrWhiteSpace(nickname))
        {
            place.Nicknames.Remove(userId);
        }
        else
        {
            if (nickname.Length > 32) nickname = nickname[..32];
            place.Nicknames[userId] = nickname;
        }

        _store.SaveProfileDebounced(Profile, Passphrase);
        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke(string.IsNullOrWhiteSpace(nickname)
            ? $"Nickname cleared for {userId}."
            : $"Nickname set to \"{nickname}\" for {userId}.");
        OnPlacesChanged?.Invoke();
    }

    /// <summary>Get pins for a channel.</summary>
    public List<PlacePin>? GetPins(string placeId, string channelId)
    {
        if (Profile?.Places.TryGetValue(placeId, out var place) == true &&
            place.Pins.TryGetValue(channelId, out var pins))
            return pins;
        return null;
    }

    /// <summary>Get a user's nickname in a place, or null.</summary>
    public string? GetNickname(string placeId, string userId)
    {
        if (Profile?.Places.TryGetValue(placeId, out var place) == true &&
            place.Nicknames.TryGetValue(userId, out var nick))
            return nick;
        return null;
    }

    // --- Permission helpers ---

    public static bool HasPermission(Place place, string userId, PlaceRole minRole)
    {
        if (place.CreatorId == userId) return true; // Owner always has all permissions
        // Check custom roles first (if they exist)
        if (place.CustomRoles.Count > 0)
            return HasCustomPermission(place, userId, MinRoleToPermission(minRole));
        // Fallback to legacy 3-tier
        if (!place.Roles.TryGetValue(userId, out var role)) role = PlaceRole.Member;
        return role >= minRole;
    }

    /// <summary>Check a specific permission using the custom roles system.</summary>
    public static bool HasCustomPermission(Place place, string userId, PlacePermission perm)
    {
        if (place.CreatorId == userId) return true;
        if (!place.MemberRoles.TryGetValue(userId, out var roleIds)) return false;
        foreach (var roleId in roleIds)
        {
            if (place.CustomRoles.TryGetValue(roleId, out var role) && role.Has(perm))
                return true;
        }
        return false;
    }

    /// <summary>Resolve effective permissions for a user in a specific channel.</summary>
    public static long ResolveChannelPermissions(Place place, string userId, string channelId)
    {
        if (place.CreatorId == userId) return long.MaxValue; // Owner = all

        // Base permissions from roles
        long perms = 0;
        if (place.MemberRoles.TryGetValue(userId, out var roleIds))
        {
            foreach (var roleId in roleIds)
            {
                if (place.CustomRoles.TryGetValue(roleId, out var role))
                    perms |= role.Permissions;
            }
        }

        // Apply channel overrides
        if (place.Channels.TryGetValue(channelId, out var ch) && ch.PermissionOverrides is not null)
        {
            var userRoleIds = roleIds ?? new List<string>();
            foreach (var ov in ch.PermissionOverrides)
            {
                if (userRoleIds.Contains(ov.RoleId))
                {
                    perms |= ov.Allow;
                    perms &= ~ov.Deny;
                }
            }
        }

        // Administrator grants everything
        if ((perms & (long)PlacePermission.Administrator) != 0) return long.MaxValue;
        return perms;
    }

    private static PlacePermission MinRoleToPermission(PlaceRole minRole) => minRole switch
    {
        PlaceRole.Owner => PlacePermission.Administrator,
        PlaceRole.Admin => PlacePermission.KickMembers, // Admin-level proxy
        _ => PlacePermission.SendMessages,
    };

    /// <summary>Get the highest role for a user (for display). Returns role name and color.</summary>
    public static (string Name, string Color, int Position) GetHighestRole(Place place, string userId)
    {
        if (place.CreatorId == userId)
            return ("Owner", place.OwnerColor, int.MaxValue);

        if (place.CustomRoles.Count == 0)
        {
            // Legacy fallback
            if (place.Roles.TryGetValue(userId, out var legacyRole))
            {
                return legacyRole switch
                {
                    PlaceRole.Owner => ("Owner", place.OwnerColor, 2),
                    PlaceRole.Admin => ("Admin", place.AdminColor, 1),
                    _ => ("Member", place.MemberColor, 0),
                };
            }
            return ("Member", place.MemberColor, 0);
        }

        if (!place.MemberRoles.TryGetValue(userId, out var roleIds) || roleIds.Count == 0)
            return ("Member", place.MemberColor, 0);

        CustomRole? highest = null;
        foreach (var roleId in roleIds)
        {
            if (place.CustomRoles.TryGetValue(roleId, out var role))
            {
                if (highest is null || role.Position > highest.Position)
                    highest = role;
            }
        }

        return highest is not null
            ? (highest.Name, highest.Color, highest.Position)
            : ("Member", place.MemberColor, 0);
    }

    // --- Custom Role management ---

    /// <summary>Create a new custom role. Owner only.</summary>
    public void CreateCustomRole(string placeId, string name, string color, long permissions, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.Places.TryGetValue(placeId, out var place)) return;
        if (place.CreatorId != Profile.UserId)
        {
            OnSystemMessage?.Invoke("Only the owner can create roles.");
            return;
        }
        if (place.CustomRoles.Count >= 50)
        {
            OnSystemMessage?.Invoke("Maximum 50 custom roles.");
            return;
        }

        var roleId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var maxPos = place.CustomRoles.Values.Any() ? place.CustomRoles.Values.Max(r => r.Position) : 0;

        place.CustomRoles[roleId] = new CustomRole
        {
            Id = roleId,
            Name = name.Length > 32 ? name[..32] : name,
            Color = ValidateColor(color, "#6b7280"),
            Position = maxPos + 1,
            Permissions = permissions,
        };
        _store.SaveProfileDebounced(Profile, Passphrase);
        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Role \"{name}\" created.");
        OnPlacesChanged?.Invoke();
    }

    /// <summary>Delete a custom role. Owner only.</summary>
    public void DeleteCustomRole(string placeId, string roleId, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.Places.TryGetValue(placeId, out var place)) return;
        if (place.CreatorId != Profile.UserId) return;

        if (!place.CustomRoles.Remove(roleId)) return;

        // Remove from all members
        foreach (var (_, roles) in place.MemberRoles)
            roles.Remove(roleId);

        // Remove from channel overrides
        foreach (var (_, ch) in place.Channels)
            ch.PermissionOverrides?.RemoveAll(o => o.RoleId == roleId);

        _store.SaveProfileDebounced(Profile, Passphrase);
        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke("Role deleted.");
        OnPlacesChanged?.Invoke();
    }

    /// <summary>Assign a role to a member. Requires ManageRoles permission, can only assign roles below own level.</summary>
    public void AssignRole(string placeId, string userId, string roleId, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.Places.TryGetValue(placeId, out var place)) return;

        // Permission check
        if (place.CreatorId != Profile.UserId)
        {
            if (!HasCustomPermission(place, Profile.UserId, PlacePermission.ManageRoles))
            {
                OnSystemMessage?.Invoke("No permission to manage roles.");
                return;
            }
            // Can only assign roles below own level
            var ownHighest = GetHighestRole(place, Profile.UserId);
            if (place.CustomRoles.TryGetValue(roleId, out var targetRole) && targetRole.Position >= ownHighest.Position)
            {
                OnSystemMessage?.Invoke("Cannot assign a role at or above your own level.");
                return;
            }
        }

        if (!place.CustomRoles.ContainsKey(roleId)) return;

        if (!place.MemberRoles.TryGetValue(userId, out var roles))
        {
            roles = new List<string>();
            place.MemberRoles[userId] = roles;
        }
        if (!roles.Contains(roleId))
            roles.Add(roleId);

        _store.SaveProfileDebounced(Profile, Passphrase);
        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Role assigned to {userId}.");
        OnPlacesChanged?.Invoke();
    }

    /// <summary>Remove a role from a member.</summary>
    public void RemoveRole(string placeId, string userId, string roleId, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.Places.TryGetValue(placeId, out var place)) return;

        if (place.CreatorId != Profile.UserId &&
            !HasCustomPermission(place, Profile.UserId, PlacePermission.ManageRoles))
            return;

        if (place.MemberRoles.TryGetValue(userId, out var roles))
        {
            roles.Remove(roleId);
            if (roles.Count == 0) place.MemberRoles.Remove(userId);
        }

        _store.SaveProfileDebounced(Profile, Passphrase);
        DistributeMetadata(placeId, place, chatService);
        OnSystemMessage?.Invoke($"Role removed from {userId}.");
        OnPlacesChanged?.Invoke();
    }

    /// <summary>Set channel permission overrides. Admin+ only.</summary>
    public void SetChannelPermOverride(string placeId, string channelId, string roleId, long allow, long deny, ChatService? chatService)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.Places.TryGetValue(placeId, out var place)) return;
        if (!HasPermission(place, Profile.UserId, PlaceRole.Admin)) return;
        if (!place.Channels.TryGetValue(channelId, out var ch)) return;

        ch.PermissionOverrides ??= new List<ChannelPermOverride>();

        // Update or add
        var existing = ch.PermissionOverrides.FirstOrDefault(o => o.RoleId == roleId);
        if (existing is not null)
        {
            existing.Allow = allow;
            existing.Deny = deny;
        }
        else
        {
            ch.PermissionOverrides.Add(new ChannelPermOverride { RoleId = roleId, Allow = allow, Deny = deny });
        }

        _store.SaveProfileDebounced(Profile, Passphrase);
        DistributeMetadata(placeId, place, chatService);
        OnPlacesChanged?.Invoke();
    }

    /// <summary>Initialize default roles for a new or migrating place.</summary>
    public void InitializeDefaultRoles(Place place, string creatorId)
    {
        if (place.CustomRoles.Count > 0) return; // Already initialized

        // @everyone (base role for all members)
        var everyoneId = "everyone";
        place.CustomRoles[everyoneId] = new CustomRole
        {
            Id = everyoneId, Name = "@everyone", Color = place.MemberColor,
            Position = 0, Permissions = (long)PlacePermission.SendMessages,
        };

        // Admin
        var adminId = "admin";
        place.CustomRoles[adminId] = new CustomRole
        {
            Id = adminId, Name = "Admin", Color = place.AdminColor,
            Position = 100,
            Permissions = (long)(PlacePermission.SendMessages | PlacePermission.ManageMessages |
                PlacePermission.ManageChannels | PlacePermission.KickMembers | PlacePermission.BanMembers |
                PlacePermission.ManageEmotes | PlacePermission.ManagePlace),
        };

        // Owner (built-in, immutable)
        var ownerId = "owner";
        place.CustomRoles[ownerId] = new CustomRole
        {
            Id = ownerId, Name = "Owner", Color = place.OwnerColor,
            Position = int.MaxValue, Permissions = (long)PlacePermission.Administrator,
        };

        // Assign owner role to creator
        place.MemberRoles[creatorId] = new List<string> { ownerId, everyoneId };

        // Migrate existing roles
        foreach (var (userId, legacyRole) in place.Roles)
        {
            if (userId == creatorId) continue;
            var roleList = new List<string> { everyoneId };
            if (legacyRole >= PlaceRole.Admin) roleList.Add(adminId);
            place.MemberRoles[userId] = roleList;
        }

        // Ensure all members have @everyone
        foreach (var memberId in place.Members)
        {
            if (!place.MemberRoles.ContainsKey(memberId))
                place.MemberRoles[memberId] = new List<string> { everyoneId };
        }
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

        _store.SaveProfileDebounced(Profile, Passphrase);
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

        _store.SaveProfileDebounced(Profile, Passphrase);
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
        _store.SaveProfileDebounced(Profile, Passphrase);
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

        // M3: Truncate ban reason to 200 chars
        if (reason is not null && reason.Length > 200) reason = reason[..200];

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
        _store.SaveProfileDebounced(Profile, Passphrase);

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
        _store.SaveProfileDebounced(Profile, Passphrase);

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

        _store.SaveProfileDebounced(Profile, Passphrase);
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

        // M5: Sanitize category name
        categoryName = SanitizeMetadataString(categoryName, 64);
        if (string.IsNullOrEmpty(categoryName))
        {
            OnSystemMessage?.Invoke("Invalid category name.");
            return;
        }

        if (place.Categories.Contains(categoryName))
        {
            OnSystemMessage?.Invoke("Category already exists.");
            return;
        }

        place.Categories.Add(categoryName);
        _store.SaveProfileDebounced(Profile, Passphrase);
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

        _store.SaveProfileDebounced(Profile, Passphrase);
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

        // M5: Sanitize topic
        channel.Topic = SanitizeMetadataString(topic, 200);
        _store.SaveProfileDebounced(Profile, Passphrase);
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

        place.MetadataKey = CryptoService.GenerateSymmetricKey();
        _store.SaveProfileDebounced(Profile, Passphrase);

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
                metadataKey = Convert.ToBase64String(place.MetadataKey),
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

    public void SendChannelMessage(string placeId, string channelId, string text, int ttl = 0,
        string? replyToMsgId = null, string? replyToPreview = null, string? replyToAuthor = null,
        List<AttachmentInfo>? attachments = null)
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

        // Build JSON envelope for structured message data
        var plaintext = MessageEnvelope.Encode(text, replyToMsgId, replyToPreview, replyToAuthor, attachments);

        var skKey = SenderKeyKey(placeId, channelId);
        var skStateJson = _store.LoadSenderKeyState(Profile, skKey);
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

        var result = SenderKeys.Encrypt(skState, plaintext, Profile.SigningSecretKey, skKey);

        var stateObj = new JsonObject
        {
            ["own"] = new JsonObject
            {
                ["chainKey"] = Convert.ToBase64String(skState.ChainKey),
                ["messageNumber"] = skState.MessageNumber,
            }
        };
        var skElem = JsonSerializer.SerializeToElement(stateObj);
        _store.SaveSenderKeyStateAsync(Profile, skKey, skElem, Passphrase);

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
        var persistedMsg = new ChatMessage
        {
            From = Profile.UserId, Text = text, Ts = ts, Ttl = ttl,
            ReplyToMsgId = replyToMsgId, ReplyToPreview = replyToPreview, ReplyToAuthor = replyToAuthor,
            Attachments = attachments,
        };
        _store.AddChatMessage(Profile, chatKey, persistedMsg, Passphrase);
        lock (_pendingAckLock) { _pendingAck.Enqueue((chatKey, persistedMsg)); }
        OnChannelMessageSent?.Invoke(chatKey, text, ttl);
    }

    public IReadOnlyDictionary<string, Place>? GetPlaces() => Profile?.Places;

    /// <summary>Send a reaction on a message in a Place channel.</summary>
    public void SendReaction(string placeId, string channelId, string msgId, string emoji, bool add)
    {
        if (Profile is null || Passphrase is null) return;
        var controlText = MessageEnvelope.EncodeReaction(msgId, emoji, add);
        SendControlMessage(placeId, channelId, controlText);
        ApplyReaction(ChatKey(placeId, channelId), msgId, emoji, Profile.UserId, add);
    }

    /// <summary>Send an edit control message for a message you authored.</summary>
    public void SendEdit(string placeId, string channelId, string msgId, string newText)
    {
        if (Profile is null || Passphrase is null) return;
        var controlText = MessageEnvelope.EncodeEdit(msgId, newText);
        SendControlMessage(placeId, channelId, controlText);
        ApplyEdit(ChatKey(placeId, channelId), msgId, newText, Profile.UserId);
    }

    /// <summary>Send a delete control message. Author or admin can delete.</summary>
    public void SendDelete(string placeId, string channelId, string msgId)
    {
        if (Profile is null || Passphrase is null) return;
        var controlText = MessageEnvelope.EncodeDelete(msgId);
        SendControlMessage(placeId, channelId, controlText);
        ApplyDelete(ChatKey(placeId, channelId), msgId);
    }

    /// <summary>Encrypt and send a control message via Sender Keys (shared with SendReaction).</summary>
    private void SendControlMessage(string placeId, string channelId, string controlText)
    {
        if (Profile is null || Passphrase is null) return;
        if (!Profile.Places.TryGetValue(placeId, out var place)) return;
        if (!place.Channels.ContainsKey(channelId)) return;

        var skKey = SenderKeyKey(placeId, channelId);
        var skStateJson = _store.LoadSenderKeyState(Profile, skKey);
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

        var result = SenderKeys.Encrypt(skState, controlText, Profile.SigningSecretKey, skKey);

        var stateObj = new JsonObject
        {
            ["own"] = new JsonObject
            {
                ["chainKey"] = Convert.ToBase64String(skState.ChainKey),
                ["messageNumber"] = skState.MessageNumber,
            }
        };
        _store.SaveSenderKeyStateAsync(Profile, skKey, JsonSerializer.SerializeToElement(stateObj), Passphrase);

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
        _conn.Send(Msg.PlaceMessage, payload);
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
                // Only the original author can edit
                ApplyEdit(chatKey, msgId, newText, from);
                break;
            }
            case "delete":
            {
                var msgId = obj["mid"]?.GetValue<string>();
                if (msgId is null) return;
                // Author or admin can delete — verify in ApplyDelete
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

        // Author can delete own. Admin/Owner can delete any (check place role).
        if (from is not null && target.From != from)
        {
            // Check if sender is admin/owner for this place
            var parts = chatKey.Split(':');
            if (parts.Length >= 3)
            {
                var placeId = parts[1];
                if (Profile.Places.TryGetValue(placeId, out var place) &&
                    place.Roles.TryGetValue(from, out var role) && role >= PlaceRole.Admin)
                {
                    // Admin/owner can delete — fall through
                }
                else return;
            }
            else return;
        }

        target.IsDeleted = true;
        target.Text = "";

        _store.SaveChatHistoryDebounced(Profile, Passphrase);
        OnMessageDeleted?.Invoke(chatKey, msgId);
    }

    // --- Handlers ---

    private void HandlePlaceCreateOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        if (placeId is null) return;

        _pendingPlaceNames.TryDequeue(out var name);
        name ??= "Unnamed";

        var metadataKey = CryptoService.GenerateSymmetricKey();
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
        _store.SaveProfileDebounced(Profile, Passphrase);

        OnSystemMessage?.Invoke($"Place \"{name}\" created with #general channel.");
        OnPlacesChanged?.Invoke();
    }

    private void HandlePlaceInvite(JsonObject msg)
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
                MetadataKey = Array.Empty<byte>(),
            };
            _store.SaveProfileDebounced(Profile, Passphrase);
        }

        OnSystemMessage?.Invoke($"You were invited to a Place by {from ?? "unknown"}");
        OnPlacesChanged?.Invoke();
    }

    private void HandlePlaceKickOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        var targetUserId = ProtocolSerializer.GetString(msg, "targetUserId");
        if (placeId is null || targetUserId is null) return;

        if (Profile.Places.TryGetValue(placeId, out var place))
        {
            place.Members.Remove(targetUserId);
            place.Roles.Remove(targetUserId);
            _store.SaveProfileDebounced(Profile, Passphrase);
        }

        OnSystemMessage?.Invoke($"Removed {targetUserId} from place {placeId}");
    }

    private void HandlePlaceLeaveOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        if (placeId is null) return;

        Profile.Places.Remove(placeId);
        _store.SaveProfileDebounced(Profile, Passphrase);

        OnSystemMessage?.Invoke("Left place.");
        OnPlacesChanged?.Invoke();
    }

    private void HandlePlaceChannelAddOk(JsonObject msg)
    {
        // Channel already added locally in CreateChannel
    }

    private void HandlePlaceChannelRemoveOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var placeId = ProtocolSerializer.GetString(msg, "placeId");
        var channelId = ProtocolSerializer.GetString(msg, "channelId");
        if (placeId is null || channelId is null) return;

        if (Profile.Places.TryGetValue(placeId, out var place))
        {
            place.Channels.Remove(channelId);
            _store.SaveProfileDebounced(Profile, Passphrase);
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
        if (from == Profile.UserId)
        {
            // Server echoes own PLACE_MESSAGE back with a server-assigned msgId.
            // Pair with the head of the pending-ACK FIFO — WS delivers echoes in send order.
            // Scanning ChatHistory for "first own null-MsgId" is unreliable (pre-fix orphans).
            var ownMsgId = ProtocolSerializer.GetString(msg, "msgId");
            if (ownMsgId is null) return;

            (string ChatKey, ChatMessage Msg) entry;
            lock (_pendingAckLock)
            {
                // Echoes for control messages (reactions/edits/deletes) arrive with no
                // matching queue entry since we don't persist/enqueue those — drop silently.
                if (_pendingAck.Count == 0) return;
                entry = _pendingAck.Dequeue();
            }
            entry.Msg.MsgId = ownMsgId;
            _store.SaveChatHistoryDebounced(Profile, Passphrase);
            OnOwnMessageIdAssigned?.Invoke(entry.ChatKey, ownMsgId);
            return;
        }

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
                    var mckB64 = memberData.GetProperty("chainKey").GetString() ?? "";
                    memberState = new SenderKeys.SenderKeyState
                    {
                        ChainKey = mckB64.Length == 0 ? Array.Empty<byte>() : Convert.FromBase64String(mckB64),
                        MessageNumber = memberData.GetProperty("messageNumber").GetInt32(),
                    };
                }
                else return;
            }
            catch { return; }
        }
        else return;

        var plaintext = SenderKeys.Decrypt(memberState, encrypted, nonce, messageNumber, signature, signingKey, skKey);
        if (plaintext is null) return;

        // Save updated sender key state
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
                var elem = JsonSerializer.SerializeToElement(parsed);
                _store.SaveSenderKeyStateAsync(Profile, skKey, elem, Passphrase);
            }
        }

        // Handle control messages (reactions, edits, deletes)
        var ctrl = MessageEnvelope.TryParseControl(plaintext);
        if (ctrl is not null)
        {
            var chatKey2 = ChatKey(placeId, channelId);
            HandleControlMessage(ctrl.Value.ctrl, ctrl.Value.obj, from, chatKey2);
            return;
        }

        // Decode JSON envelope (backward-compatible with plain-text messages)
        var text = MessageEnvelope.Decode(plaintext, out var replyToMsgId, out var replyToPreview, out var replyToAuthor, out var attachments);

        var sanitized = ChatService.EscapeContent(text);
        var serverMsgId = ProtocolSerializer.GetString(msg, "msgId");
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(ProtocolSerializer.GetLong(msg, "ts")).LocalDateTime;
        var chatKey = ChatKey(placeId, channelId);

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

        _store.AddChatMessage(Profile, chatKey, chatMsg, Passphrase);
        OnChannelMessageReceived?.Invoke(placeId, channelId, from, sanitized, ts, chatMsg);
    }

    private void HandlePlaceRoleSetOk(JsonObject msg)
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
            _store.SaveProfileDebounced(Profile, Passphrase);
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
    public Task HandlePlaceKeyReceived(string placeId, string metadataKey, string encryptedMetadata, string senderId)
    {
        if (Profile is null || Passphrase is null) return Task.CompletedTask;

        // C2: Only accept placekey for places we already know about (invited via server)
        if (!Profile.Places.TryGetValue(placeId, out var place))
            return Task.CompletedTask;

        // H11: Validate metadataKey format (must be exactly 32 bytes base64)
        byte[] metadataKeyBytes;
        try
        {
            metadataKeyBytes = Convert.FromBase64String(metadataKey);
            if (metadataKeyBytes.Length != 32) return Task.CompletedTask;
        }
        catch { return Task.CompletedTask; }

        place.MetadataKey = metadataKeyBytes;

        if (DecryptMetadata(encryptedMetadata, metadataKeyBytes, place))
        {
            // Add ourselves to local member list if not present
            if (!place.Members.Contains(Profile.UserId))
                place.Members.Add(Profile.UserId);

            _store.SaveProfileDebounced(Profile, Passphrase);
            OnSystemMessage?.Invoke($"Received metadata for \"{place.Name}\"");
            OnPlacesChanged?.Invoke();
        }
        else
        {
            OnSystemMessage?.Invoke($"Failed to decrypt place metadata from {senderId}");
        }
        return Task.CompletedTask;
    }

    // M5/M4: Sanitize metadata strings — strip control chars, bidi overrides, enforce length
    private static string SanitizeMetadataString(string input, int maxLength)
    {
        if (string.IsNullOrEmpty(input)) return input;
        // Strip control characters and Unicode bidi overrides
        var s = System.Text.RegularExpressions.Regex.Replace(input,
            @"[\x00-\x1f\x7f\u200E\u200F\u202A-\u202E\u2066-\u2069]", "");
        if (s.Length > maxLength) s = s[..maxLength];
        return s.Trim();
    }

    // Validate hex color format — returns fallback if invalid
    private static string ValidateColor(string? color, string fallback)
        => color is not null && System.Text.RegularExpressions.Regex.IsMatch(color, @"^#[0-9a-fA-F]{6}$")
            ? color : fallback;
}
