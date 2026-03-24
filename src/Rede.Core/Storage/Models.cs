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
