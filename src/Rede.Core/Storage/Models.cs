using System.Text.Json;
using System.Text.Json.Serialization;
using Rede.Core.Crypto;

namespace Rede.Core.Storage;

/// <summary>
/// Profile model — matches store.js createProfile() exactly.
/// All field names use camelCase for JSON compat with the JS client.
/// </summary>
public class Profile
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("publicKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("secretKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] SecretKey { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("signingKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] SigningKey { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("signingSecretKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] SigningSecretKey { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("contacts")]
    public Dictionary<string, Contact> Contacts { get; set; } = new();

    [JsonPropertyName("groups")]
    public Dictionary<string, Group> Groups { get; set; } = new();

    [JsonPropertyName("places")]
    public Dictionary<string, Place> Places { get; set; } = new();

    [JsonPropertyName("chatHistory")]
    public Dictionary<string, List<ChatMessage>> ChatHistory { get; set; } = new();

    // Signal Protocol state (v3)
    [JsonPropertyName("signedPreKey")]
    public KeyPairData? SignedPreKey { get; set; }

    [JsonPropertyName("signedPreKeySig")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[]? SignedPreKeySig { get; set; }

    [JsonPropertyName("oneTimePreKeys")]
    public List<OneTimePreKey> OneTimePreKeys { get; set; } = new();

    [JsonPropertyName("nextPreKeyId")]
    public int NextPreKeyId { get; set; }

    [JsonPropertyName("ratchetStates")]
    public Dictionary<string, System.Text.Json.JsonElement> RatchetStates { get; set; } = new();

    [JsonPropertyName("senderKeys")]
    public Dictionary<string, System.Text.Json.JsonElement> SenderKeys { get; set; } = new();

    [JsonPropertyName("serverSigningKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[]? ServerSigningKey { get; set; }

    [JsonPropertyName("previousSignedPreKeys")]
    public List<ArchivedSignedPreKey>? PreviousSignedPreKeys { get; set; }

    [JsonPropertyName("ownDevices")]
    public Dictionary<string, DeviceKeys>? OwnDevices { get; set; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = 3;

    // Voice call settings
    [JsonPropertyName("defaultCallMode")]
    public string DefaultCallMode { get; set; } = "secure";

    [JsonPropertyName("allowFastCalls")]
    public bool AllowFastCalls { get; set; } = true;

    [JsonPropertyName("inputDeviceName")]
    public string? InputDeviceName { get; set; }

    [JsonPropertyName("outputDeviceName")]
    public string? OutputDeviceName { get; set; }

    [JsonPropertyName("inputVolume")]
    public float InputVolume { get; set; } = 1.0f;

    [JsonPropertyName("outputVolume")]
    public float OutputVolume { get; set; } = 1.0f;

    [JsonPropertyName("noiseGateThreshold")]
    public float NoiseGateThreshold { get; set; } = 0.02f;

    [JsonPropertyName("noiseSuppression")]
    public bool NoiseSuppression { get; set; }

    // Profile customization
    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; } // hex color, e.g. "#8b5cf6"

    [JsonPropertyName("avatarData")]
    public string? AvatarData { get; set; } // base64-encoded image (PNG/GIF/JPEG, max 256KB)

    [JsonPropertyName("avatarMimeType")]
    public string? AvatarMimeType { get; set; } // "image/png", "image/gif", "image/jpeg"

    [JsonPropertyName("themeVariant")]
    public string? ThemeVariant { get; set; } // "dark" (default), "midnight", "dim", "light"

    [JsonPropertyName("lastServerName")]
    public string? LastServerName { get; set; } // remembered server choice for quick login

    // Status / Presence
    [JsonPropertyName("status")]
    public string Status { get; set; } = "online"; // "online", "away", "dnd", "invisible"

    [JsonPropertyName("customStatus")]
    public string? CustomStatus { get; set; } // optional short status text

    // Notifications
    [JsonPropertyName("notificationsEnabled")]
    public bool NotificationsEnabled { get; set; } = true;

    [JsonPropertyName("notificationShowContent")]
    public bool NotificationShowContent { get; set; } // false = privacy mode (default)

    [JsonPropertyName("notificationSoundEnabled")]
    public bool NotificationSoundEnabled { get; set; } = true;

    // System integration
    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = true; // true = pressing X hides window to tray instead of quitting

    // One-time migration marker: flip pre-v2.17 profiles (which had MinimizeToTray=false by default)
    // to the new tray-by-default behavior on first load. Ensures existing users get the same UX as new installs.
    [JsonPropertyName("trayDefaultMigratedV217")]
    public bool TrayDefaultMigratedV217 { get; set; }

    [JsonPropertyName("autostart")]
    public bool Autostart { get; set; } // true = launch Rede on OS login

    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; } // true = when launched (or autostarted) start hidden in tray

    // Replay-protection: persisted NonceTracker snapshot (merged from chat/group/place trackers).
    // Loaded into all nonce trackers at login; re-exported and saved on flush. Closes the
    // restart-replay window that existed when nonces lived only in memory.
    [JsonPropertyName("seenNonces")]
    public Dictionary<string, long> SeenNonces { get; set; } = new();

    // Transient (not persisted in older profiles)
    [JsonPropertyName("_deliveryToken")]
    public string? DeliveryToken { get; set; }

    [JsonPropertyName("_pendingKeyChange")]
    public System.Text.Json.JsonElement? PendingKeyChange { get; set; }

    /// <summary>
    /// Zero all in-memory secret key material. Public keys and non-secret
    /// metadata are left intact — this is meant to be called on logout/close
    /// so long-lived process memory doesn't retain recoverable secrets.
    /// </summary>
    public void ZeroSecrets()
    {
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(SecretKey);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(SigningSecretKey);
        if (SignedPreKey is not null)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(SignedPreKey.SecretKey);
        foreach (var otpk in OneTimePreKeys)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(otpk.SecretKey);
        if (PreviousSignedPreKeys is not null)
            foreach (var old in PreviousSignedPreKeys)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(old.SecretKey);
        foreach (var group in Groups.Values)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(group.Key);
        foreach (var place in Places.Values)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(place.MetadataKey);
    }
}

public class Contact
{
    [JsonPropertyName("publicKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("signingKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[]? SigningKey { get; set; }

    [JsonPropertyName("devices")]
    public Dictionary<string, DeviceKeys> Devices { get; set; } = new();

    [JsonPropertyName("alias")]
    public string? Alias { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("addedAt")]
    public long AddedAt { get; set; }

    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; }

    [JsonPropertyName("avatarData")]
    public string? AvatarData { get; set; }

    [JsonPropertyName("avatarMimeType")]
    public string? AvatarMimeType { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; } // "online", "away", "dnd", "offline"

    [JsonPropertyName("customStatus")]
    public string? CustomStatus { get; set; }
}

public class DeviceKeys
{
    [JsonPropertyName("publicKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("signingKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[]? SigningKey { get; set; }
}

public class Group
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("key")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] Key { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("members")]
    public List<string> Members { get; set; } = new();
}

public class Place
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("metadataKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] MetadataKey { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("channels")]
    public Dictionary<string, PlaceChannel> Channels { get; set; } = new();

    [JsonPropertyName("members")]
    public List<string> Members { get; set; } = new();

    [JsonPropertyName("roles")]
    public Dictionary<string, PlaceRole> Roles { get; set; } = new();

    [JsonPropertyName("creatorId")]
    public string CreatorId { get; set; } = "";

    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; }

    [JsonPropertyName("iconData")]
    public string? IconData { get; set; } // base64-encoded image (max 256KB)

    [JsonPropertyName("iconMimeType")]
    public string? IconMimeType { get; set; }

    [JsonPropertyName("emotes")]
    public Dictionary<string, PlaceEmote> Emotes { get; set; } = new();

    [JsonPropertyName("bans")]
    public Dictionary<string, PlaceBan> Bans { get; set; } = new();

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new(); // ordered category names

    // Customizable role colors (hex strings)
    [JsonPropertyName("ownerColor")]
    public string OwnerColor { get; set; } = "#eab308"; // gold

    [JsonPropertyName("adminColor")]
    public string AdminColor { get; set; } = "#8b5cf6"; // violet

    [JsonPropertyName("memberColor")]
    public string MemberColor { get; set; } = "#6b7280"; // gray

    /// <summary>Pinned messages per channel: channelId → list of pins (max 50/channel).</summary>
    [JsonPropertyName("pins")]
    public Dictionary<string, List<PlacePin>> Pins { get; set; } = new();

    /// <summary>Nicknames: userId → display name override.</summary>
    [JsonPropertyName("nicknames")]
    public Dictionary<string, string> Nicknames { get; set; } = new();

    /// <summary>Custom roles: roleId → CustomRole. Replaces the 3-tier system.</summary>
    [JsonPropertyName("customRoles")]
    public Dictionary<string, CustomRole> CustomRoles { get; set; } = new();

    /// <summary>Member role assignments: userId → list of roleIds.</summary>
    [JsonPropertyName("memberRoles")]
    public Dictionary<string, List<string>> MemberRoles { get; set; } = new();
}

public class PlaceEmote
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = ""; // e.g. "pepe", referenced as :pepe: in messages

    [JsonPropertyName("imageData")]
    public string ImageData { get; set; } = ""; // base64-encoded image (max 64KB)

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "image/png"; // image/png, image/gif, image/jpeg

    [JsonPropertyName("uploadedBy")]
    public string UploadedBy { get; set; } = "";
}

public class PlaceBan
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("bannedBy")]
    public string BannedBy { get; set; } = "";

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("bannedAt")]
    public long BannedAt { get; set; }
}

public class PlacePin
{
    [JsonPropertyName("msgId")]
    public string MsgId { get; set; } = "";

    [JsonPropertyName("pinnedBy")]
    public string PinnedBy { get; set; } = "";

    [JsonPropertyName("pinnedAt")]
    public long PinnedAt { get; set; }

    [JsonPropertyName("preview")]
    public string Preview { get; set; } = ""; // first ~100 chars of the message

    [JsonPropertyName("channelId")]
    public string ChannelId { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";
}

public class PlaceChannel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "";

    [JsonPropertyName("category")]
    public string? Category { get; set; } // optional grouping (e.g. "Text Channels", "Voice")

    [JsonPropertyName("position")]
    public int Position { get; set; } // ordering within category

    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("permOverrides")]
    public List<ChannelPermOverride>? PermissionOverrides { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlaceRole
{
    Member = 0,
    Admin = 1,
    Owner = 2,
}

/// <summary>Permission bitfield for custom roles.</summary>
[Flags]
public enum PlacePermission : long
{
    None            = 0,
    SendMessages    = 1 << 0,
    ManageMessages  = 1 << 1, // delete others' messages
    ManageChannels  = 1 << 2,
    ManageRoles     = 1 << 3, // assign roles (only below own level)
    KickMembers     = 1 << 4,
    BanMembers      = 1 << 5,
    ManageEmotes    = 1 << 6,
    ManagePlace     = 1 << 7, // name/icon/accent
    Administrator   = 1 << 8, // all permissions
    ViewAuditLog    = 1 << 9,
}

public class CustomRole
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#6b7280";

    [JsonPropertyName("position")]
    public int Position { get; set; } // higher = more power

    [JsonPropertyName("permissions")]
    public long Permissions { get; set; }

    /// <summary>Check if this role has a specific permission.</summary>
    public bool Has(PlacePermission perm)
        => (Permissions & (long)PlacePermission.Administrator) != 0 || (Permissions & (long)perm) != 0;
}

public class ChannelPermOverride
{
    [JsonPropertyName("roleId")]
    public string RoleId { get; set; } = "";

    [JsonPropertyName("allow")]
    public long Allow { get; set; }

    [JsonPropertyName("deny")]
    public long Deny { get; set; }
}

public class ChatMessage
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("ts")]
    public long Ts { get; set; }

    [JsonPropertyName("ttl")]
    public int Ttl { get; set; }

    [JsonPropertyName("mid")]
    public string? MsgId { get; set; }

    [JsonPropertyName("ref")]
    public string? ReplyToMsgId { get; set; }

    [JsonPropertyName("rp")]
    public string? ReplyToPreview { get; set; }

    [JsonPropertyName("ra")]
    public string? ReplyToAuthor { get; set; }

    /// <summary>Reactions: emoji → list of userIds who reacted.</summary>
    [JsonPropertyName("rx")]
    public Dictionary<string, List<string>>? Reactions { get; set; }

    [JsonPropertyName("eat")]
    public long? EditedAt { get; set; }

    [JsonPropertyName("del")]
    public bool IsDeleted { get; set; }

    [JsonPropertyName("att")]
    public List<AttachmentInfo>? Attachments { get; set; }
}

public class AttachmentInfo
{
    [JsonPropertyName("bid")]
    public string BlobId { get; set; } = "";

    [JsonPropertyName("key")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] Key { get; set; } = Array.Empty<byte>(); // symmetric key for decryption

    [JsonPropertyName("nonce")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] Nonce { get; set; } = Array.Empty<byte>(); // nonce

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("mime")]
    public string? MimeType { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public class KeyPairData
{
    [JsonPropertyName("publicKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("secretKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] SecretKey { get; set; } = Array.Empty<byte>();
}

public class OneTimePreKey
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("publicKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("secretKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] SecretKey { get; set; } = Array.Empty<byte>();
}

public class ArchivedSignedPreKey
{
    [JsonPropertyName("publicKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("secretKey")]
    [JsonConverter(typeof(Base64BytesJsonConverter))]
    public byte[] SecretKey { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("archivedAt")]
    public long ArchivedAt { get; set; }
}

/// <summary>
/// Source-generated JSON serializer for Profile — eliminates reflection overhead on every save.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(Profile))]
internal partial class ProfileJsonContext : JsonSerializerContext { }
