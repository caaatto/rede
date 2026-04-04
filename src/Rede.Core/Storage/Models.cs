using System.Text.Json;
using System.Text.Json.Serialization;

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
    public string PublicKey { get; set; } = "";

    [JsonPropertyName("secretKey")]
    public string SecretKey { get; set; } = "";

    [JsonPropertyName("signingKey")]
    public string SigningKey { get; set; } = "";

    [JsonPropertyName("signingSecretKey")]
    public string SigningSecretKey { get; set; } = "";

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
    public string? SignedPreKeySig { get; set; }

    [JsonPropertyName("oneTimePreKeys")]
    public List<OneTimePreKey> OneTimePreKeys { get; set; } = new();

    [JsonPropertyName("nextPreKeyId")]
    public int NextPreKeyId { get; set; }

    [JsonPropertyName("ratchetStates")]
    public Dictionary<string, System.Text.Json.JsonElement> RatchetStates { get; set; } = new();

    [JsonPropertyName("senderKeys")]
    public Dictionary<string, System.Text.Json.JsonElement> SenderKeys { get; set; } = new();

    [JsonPropertyName("serverSigningKey")]
    public string? ServerSigningKey { get; set; }

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

    // Profile customization
    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; } // hex color, e.g. "#8b5cf6"

    [JsonPropertyName("avatarData")]
    public string? AvatarData { get; set; } // base64-encoded image (PNG/GIF/JPEG, max 256KB)

    [JsonPropertyName("avatarMimeType")]
    public string? AvatarMimeType { get; set; } // "image/png", "image/gif", "image/jpeg"

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

    // Transient (not persisted in older profiles)
    [JsonPropertyName("_deliveryToken")]
    public string? DeliveryToken { get; set; }

    [JsonPropertyName("_pendingKeyChange")]
    public System.Text.Json.JsonElement? PendingKeyChange { get; set; }
}

public class Contact
{
    [JsonPropertyName("publicKey")]
    public string PublicKey { get; set; } = "";

    [JsonPropertyName("signingKey")]
    public string? SigningKey { get; set; }

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
    public string PublicKey { get; set; } = "";

    [JsonPropertyName("signingKey")]
    public string? SigningKey { get; set; }
}

public class Group
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("members")]
    public List<string> Members { get; set; } = new();
}

public class Place
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("metadataKey")]
    public string MetadataKey { get; set; } = "";

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
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlaceRole
{
    Member = 0,
    Admin = 1,
    Owner = 2,
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
}

public class KeyPairData
{
    [JsonPropertyName("publicKey")]
    public string PublicKey { get; set; } = "";

    [JsonPropertyName("secretKey")]
    public string SecretKey { get; set; } = "";
}

public class OneTimePreKey
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("publicKey")]
    public string PublicKey { get; set; } = "";

    [JsonPropertyName("secretKey")]
    public string SecretKey { get; set; } = "";
}

public class ArchivedSignedPreKey
{
    [JsonPropertyName("publicKey")]
    public string PublicKey { get; set; } = "";

    [JsonPropertyName("secretKey")]
    public string SecretKey { get; set; } = "";

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
