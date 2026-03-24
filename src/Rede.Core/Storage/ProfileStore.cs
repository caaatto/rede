using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rede.Core.Crypto;

namespace Rede.Core.Storage;

/// <summary>
/// Encrypted profile storage with atomic writes and file locking.
/// Mirrors: store.js (loadProfile, saveProfile, createProfile, addContact, etc.)
/// </summary>
public class ProfileStore
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rede");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private void EnsureDir()
    {
        if (!Directory.Exists(DataDir))
        {
            Directory.CreateDirectory(DataDir);
            if (!OperatingSystem.IsWindows())
            {
                // Set 0700 permissions on Unix
                File.SetUnixFileMode(DataDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    private static string GetProfilePath(string userId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant();
        return Path.Combine(DataDir, $"{hash}.enc");
    }

    private static string GetLegacyProfilePath(string userId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant()[..16];
        return Path.Combine(DataDir, $"{hash}.enc");
    }

    private static void SecureOverwrite(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var size = new FileInfo(filePath).Length;
                if (size > 0)
                {
                    var random = RandomNumberGenerator.GetBytes((int)Math.Min(size, int.MaxValue));
                    File.WriteAllBytes(filePath, random);
                }
            }
        }
        catch { }
    }

    public bool ProfileExists(string userId)
    {
        EnsureDir();
        return File.Exists(GetProfilePath(userId)) || File.Exists(GetLegacyProfilePath(userId));
    }

    public void DeleteProfile(string userId)
    {
        try
        {
            var p = GetProfilePath(userId);
            if (File.Exists(p))
            {
                SecureOverwrite(p);
                File.Delete(p);
            }
        }
        catch { }
    }

    public async Task<Profile?> LoadProfileAsync(string userId, string passphrase)
    {
        EnsureDir();
        var p = GetProfilePath(userId);

        // Migration: check legacy path
        if (!File.Exists(p))
        {
            var legacy = GetLegacyProfilePath(userId);
            if (File.Exists(legacy))
                File.Move(legacy, p);
            else
                return null;
        }

        var json = await File.ReadAllTextAsync(p);
        var envelope = JsonSerializer.Deserialize<ProfileEncryption.EncryptedEnvelope>(json, JsonOpts);
        if (envelope is null) return null;

        var profile = ProfileEncryption.Decrypt<Profile>(envelope, passphrase);
        if (profile is not null && MigrateProfile(profile))
        {
            await SaveProfileAsync(profile, passphrase);
        }
        return profile;
    }

    public async Task SaveProfileAsync(Profile profile, string passphrase)
    {
        EnsureDir();
        var p = GetProfilePath(profile.UserId);

        await _saveLock.WaitAsync();
        try
        {
            var envelope = ProfileEncryption.Encrypt(profile, passphrase);
            var json = JsonSerializer.Serialize(envelope, JsonOpts);

            // Atomic write: temp file then rename
            var tmpFile = p + ".tmp";
            await File.WriteAllTextAsync(tmpFile, json);
            SecureOverwrite(p);
            File.Move(tmpFile, p, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task<Profile> CreateProfileAsync(string internalId, string displayName, string passphrase)
    {
        var (publicKey, secretKey) = CryptoService.GenerateKeyPair();
        var (signingKey, signingSecretKey) = CryptoService.GenerateSigningKeyPair();
        var deviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        var profile = new Profile
        {
            UserId = internalId,
            DisplayName = displayName,
            DeviceId = deviceId,
            PublicKey = publicKey,
            SecretKey = secretKey,
            SigningKey = signingKey,
            SigningSecretKey = signingSecretKey,
            ProtocolVersion = 3,
        };

        await SaveProfileAsync(profile, passphrase);
        return profile;
    }

    /// <summary>
    /// Migrate older profiles to v3 + multi-device. Mirrors: migrateProfile(profile)
    /// </summary>
    private static bool MigrateProfile(Profile profile)
    {
        bool changed = false;

        if (profile.ProtocolVersion < 3)
        {
            profile.RatchetStates ??= new();
            profile.SenderKeys ??= new();
            profile.OneTimePreKeys ??= new();
            profile.PreviousSignedPreKeys ??= new();
            profile.ProtocolVersion = 3;
            changed = true;
        }

        if (string.IsNullOrEmpty(profile.DeviceId))
        {
            profile.DeviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            changed = true;
        }

        // Migrate contacts: add devices map if flat keys
        foreach (var (_, contact) in profile.Contacts)
        {
            if (contact.Devices.Count == 0 && !string.IsNullOrEmpty(contact.PublicKey))
            {
                contact.Devices["primary"] = new DeviceKeys
                {
                    PublicKey = contact.PublicKey,
                    SigningKey = contact.SigningKey,
                };
                changed = true;
            }
        }

        return changed;
    }

    // --- Contact operations ---

    public record AddContactResult(bool Warning, string? OldFingerprint = null, string? NewFingerprint = null);

    public async Task<AddContactResult> AddContactAsync(
        Profile profile, string internalId, string publicKey, string? signingKey,
        string? displayName, string passphrase, Dictionary<string, DeviceKeys>? devices = null)
    {
        var deviceMap = devices ?? new Dictionary<string, DeviceKeys>
        {
            ["primary"] = new() { PublicKey = publicKey, SigningKey = signingKey }
        };

        if (profile.Contacts.TryGetValue(internalId, out var existing) && existing.PublicKey != publicKey)
        {
            return new AddContactResult(true,
                CryptoService.Fingerprint(existing.PublicKey),
                CryptoService.Fingerprint(publicKey));
        }

        profile.Contacts[internalId] = new Contact
        {
            PublicKey = publicKey,
            SigningKey = signingKey,
            Devices = deviceMap,
            Alias = displayName ?? internalId,
            DisplayName = displayName ?? internalId,
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await SaveProfileAsync(profile, passphrase);
        return new AddContactResult(false);
    }

    public async Task ConfirmContactKeyChangeAsync(
        Profile profile, string internalId, string publicKey, string? signingKey,
        string? displayName, string passphrase, Dictionary<string, DeviceKeys>? devices = null)
    {
        var deviceMap = devices ?? new Dictionary<string, DeviceKeys>
        {
            ["primary"] = new() { PublicKey = publicKey, SigningKey = signingKey }
        };

        profile.Contacts[internalId] = new Contact
        {
            PublicKey = publicKey,
            SigningKey = signingKey,
            Devices = deviceMap,
            Alias = displayName ?? internalId,
            DisplayName = displayName ?? internalId,
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await SaveProfileAsync(profile, passphrase);
    }

    // --- Group operations ---

    public async Task AddGroupAsync(Profile profile, string groupId, string name, string groupKey, List<string>? members, string passphrase)
    {
        profile.Groups[groupId] = new Group
        {
            Name = name,
            Key = groupKey,
            Members = members ?? new(),
        };
        await SaveProfileAsync(profile, passphrase);
    }

    // --- Chat history ---

    public async Task AddChatMessageAsync(Profile profile, string chatId, ChatMessage message, string passphrase)
    {
        if (!profile.ChatHistory.ContainsKey(chatId))
            profile.ChatHistory[chatId] = new();

        profile.ChatHistory[chatId].Add(message);

        if (profile.ChatHistory[chatId].Count > 1000)
            profile.ChatHistory[chatId] = profile.ChatHistory[chatId].TakeLast(1000).ToList();

        await SaveProfileAsync(profile, passphrase);
    }

    public async Task CleanupExpiredMessagesAsync(Profile profile, string passphrase)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool changed = false;

        foreach (var chatId in profile.ChatHistory.Keys.ToList())
        {
            var before = profile.ChatHistory[chatId].Count;
            profile.ChatHistory[chatId] = profile.ChatHistory[chatId]
                .Where(m => m.Ttl == 0 || now - m.Ts < m.Ttl * 86_400_000L)
                .ToList();
            if (profile.ChatHistory[chatId].Count != before) changed = true;
        }

        if (changed) await SaveProfileAsync(profile, passphrase);
    }

    // --- Ratchet state ---

    private static string RatchetKey(string contactId, string? deviceId)
        => deviceId is not null ? $"{contactId}:{deviceId}" : contactId;

    public JsonElement? LoadRatchetState(Profile profile, string contactId, string? deviceId = null)
    {
        var key = RatchetKey(contactId, deviceId);
        if (profile.RatchetStates.TryGetValue(key, out var state))
            return state;
        if (deviceId is not null && profile.RatchetStates.TryGetValue(contactId, out var legacy))
            return legacy;
        return null;
    }

    public async Task SaveRatchetStateAsync(Profile profile, string contactId, JsonElement state, string passphrase, string? deviceId = null)
    {
        var key = RatchetKey(contactId, deviceId);
        profile.RatchetStates[key] = state;
        await SaveProfileAsync(profile, passphrase);
    }

    public List<string?> GetRatchetDeviceIds(Profile profile, string contactId)
    {
        var ids = new List<string?>();
        foreach (var k in profile.RatchetStates.Keys)
        {
            if (k == contactId)
                ids.Add(null); // legacy
            else if (k.StartsWith(contactId + ":"))
                ids.Add(k[(contactId.Length + 1)..]);
        }
        return ids;
    }

    // --- Sender key state ---

    public JsonElement? LoadSenderKeyState(Profile profile, string groupId)
    {
        return profile.SenderKeys.TryGetValue(groupId, out var state) ? state : null;
    }

    public async Task SaveSenderKeyStateAsync(Profile profile, string groupId, JsonElement state, string passphrase)
    {
        profile.SenderKeys[groupId] = state;
        await SaveProfileAsync(profile, passphrase);
    }
}
