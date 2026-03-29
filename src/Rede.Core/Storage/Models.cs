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
}

public class PlaceChannel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "";

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
