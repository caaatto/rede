using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rede.Core.Crypto;
using Sodium;

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
    private readonly SemaphoreSlim _historySaveLock = new(1, 1);
    // H2: Lock to prevent concurrent Profile dictionary mutation during serialization
    private readonly object _profileMutationLock = new();

    // --- Performance: cached scrypt key + debounced saves ---
    private byte[]? _cachedKey64;       // 64-byte derived key (32 enc + 32 hmac)
    private byte[]? _cachedSalt;        // salt used to derive the cached key
    private byte[]? _cachedPassphrase;  // owned copy — to detect passphrase changes (byte[] so we can zero on clear)

    private CancellationTokenSource? _debounceCts;
    private volatile bool _savePending;
    private CancellationTokenSource? _historyDebounceCts;
    private volatile bool _historySavePending;
    private const int DebounceMs = 500; // coalesce saves within 500ms

    private void EnsureDir()
    {
        if (!Directory.Exists(DataDir))
        {
            Directory.CreateDirectory(DataDir);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(DataDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        // M4: Clean up stale .tmp files from crashed writes
        try
        {
            foreach (var tmp in Directory.GetFiles(DataDir, "*.tmp"))
                File.Delete(tmp);
        }
        catch { }
    }

    private static string GetProfilePath(string userId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant();
        return Path.Combine(DataDir, $"{hash}.enc");
    }

    private static string GetHistoryPath(string userId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant();
        return Path.Combine(DataDir, $"{hash}.history.enc");
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
                    // L2: Use FileStream with flush to ensure overwrite hits disk
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
                    fs.Write(random);
                    fs.Flush(flushToDisk: true);
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
            if (File.Exists(p)) { SecureOverwrite(p); File.Delete(p); }
            var h = GetHistoryPath(userId);
            if (File.Exists(h)) { SecureOverwrite(h); File.Delete(h); }
        }
        catch { }
    }

    public async Task<Profile?> LoadProfileAsync(string userId, byte[] passphrase)
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

        return await DecryptProfileFileAsync(p, passphrase);
    }

    /// <summary>
    /// Load a profile directly by its file hash (sha256 hex of userId) without needing
    /// the plaintext userId. The decrypted profile contains UserId internally.
    /// Used by quick-login flow.
    /// </summary>
    public async Task<Profile?> LoadProfileByHashAsync(string hashHex, byte[] passphrase)
    {
        EnsureDir();
        // Validate hash format: 64 hex chars (no path traversal)
        if (hashHex.Length != 64 || !IsHexLower(hashHex)) return null;
        var p = Path.Combine(DataDir, $"{hashHex}.enc");
        if (!File.Exists(p)) return null;
        return await DecryptProfileFileAsync(p, passphrase);
    }

    private static bool IsHexLower(string s)
    {
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    private async Task<Profile?> DecryptProfileFileAsync(string path, byte[] passphrase)
    {
        var json = await File.ReadAllTextAsync(path);
        var envelope = JsonSerializer.Deserialize<ProfileEncryption.EncryptedEnvelope>(json, JsonOpts);
        if (envelope is null) return null;

        var profile = ProfileEncryption.Decrypt<Profile>(envelope, passphrase);
        if (profile is not null)
        {
            CacheKey(passphrase);
            var migrated = MigrateProfile(profile);

            // Migration: move embedded ChatHistory to separate history file.
            // Old profiles (pre-v2.17.2) stored history inside profile.enc.
            // Extract it, save to {hash}.history.enc, then re-save profile without it.
            var historyPath = GetHistoryPath(profile.UserId);
            if (profile.ChatHistory.Count > 0 && !File.Exists(historyPath))
            {
                await SaveChatHistoryAsync(profile, passphrase);
                migrated = true; // re-save profile.enc (now without ChatHistory)
            }
            else if (File.Exists(historyPath))
            {
                // Load history from separate file and merge into in-memory profile
                var history = await LoadChatHistoryAsync(profile.UserId, passphrase);
                profile.ChatHistory = history;
            }

            if (migrated)
                await SaveProfileAsync(profile, passphrase);
        }
        return profile;
    }

    // --- Quick-login hint: remembers the last profile's filename (sha256 hex of userId)
    // so the user only needs to type their passphrase on next launch. The hash is already
    // the .enc filename on disk, so this file leaks no information beyond directory listing.

    private static string HintPath => Path.Combine(DataDir, ".lastprofile");

    public record LastProfileHint(string Hash, string? ServerName);

    public void SaveLastProfileHint(string userId, string? serverName = null)
    {
        try
        {
            EnsureDir();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant();
            // Line 1: hash, Line 2 (optional): server name (one of hardcoded options, not sensitive)
            var content = serverName is null ? hash : $"{hash}\n{serverName}";
            File.WriteAllText(HintPath, content);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(HintPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { }
    }

    public LastProfileHint? ReadLastProfileHint()
    {
        try
        {
            if (!File.Exists(HintPath)) return null;
            var content = File.ReadAllText(HintPath);
            var lines = content.Split('\n', StringSplitOptions.TrimEntries);
            if (lines.Length == 0) return null;
            var hash = lines[0];
            if (hash.Length != 64 || !IsHexLower(hash)) return null;
            var p = Path.Combine(DataDir, $"{hash}.enc");
            if (!File.Exists(p)) return null;
            var server = lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]) ? lines[1] : null;
            return new LastProfileHint(hash, server);
        }
        catch { return null; }
    }

    public void ClearLastProfileHint()
    {
        try { if (File.Exists(HintPath)) File.Delete(HintPath); }
        catch { }
    }

    /// <summary>
    /// Derive and cache the 64-byte scrypt key. Only re-derives if passphrase changed.
    /// </summary>
    private void CacheKey(byte[] passphrase)
    {
        if (_cachedKey64 is not null && _cachedPassphrase is not null
            && _cachedPassphrase.Length == passphrase.Length
            && CryptographicOperations.FixedTimeEquals(_cachedPassphrase, passphrase))
            return;

        // Clear old cached key + passphrase
        if (_cachedKey64 is not null) CryptoService.ZeroOut(_cachedKey64);
        if (_cachedPassphrase is not null) CryptoService.ZeroOut(_cachedPassphrase);

        var salt = SodiumCore.GetRandomBytes(16);
        _cachedKey64 = ProfileEncryption.DeriveKey(passphrase, salt, ProfileEncryption.ScryptNCurrent, 64);
        _cachedSalt = salt;
        // Own a copy so caller can freely zero their buffer
        _cachedPassphrase = (byte[])passphrase.Clone();
    }

    public async Task SaveProfileAsync(Profile profile, byte[] passphrase)
    {
        EnsureDir();
        var p = GetProfilePath(profile.UserId);

        // Ensure key is cached (first save after CreateProfileAsync, or passphrase change)
        CacheKey(passphrase);

        await _saveLock.WaitAsync();
        try
        {
            // H2: Hold mutation lock during serialization to prevent concurrent dict modification.
            // Temporarily swap ChatHistory out so it's excluded from profile.enc —
            // history lives in a separate {hash}.history.enc file.
            byte[] bytes;
            lock (_profileMutationLock)
            {
                var chatHistory = profile.ChatHistory;
                profile.ChatHistory = new();
                try
                {
                    var envelope = _cachedKey64 is not null
                        ? ProfileEncryption.EncryptWithDerivedKey(profile, _cachedKey64, _cachedSalt)
                        : ProfileEncryption.Encrypt(profile, passphrase);
                    bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOpts);
                }
                finally { profile.ChatHistory = chatHistory; }
            }

            await AtomicWriteAsync(p, bytes);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Save only the chat history dictionary to a separate encrypted file.
    /// Much faster than SaveProfileAsync because it doesn't re-encrypt the
    /// entire profile (keys, contacts, ratchet states, sender keys, etc.).
    /// </summary>
    public async Task SaveChatHistoryAsync(Profile profile, byte[] passphrase)
    {
        EnsureDir();
        var p = GetHistoryPath(profile.UserId);
        CacheKey(passphrase);

        await _historySaveLock.WaitAsync();
        try
        {
            byte[] bytes;
            lock (_profileMutationLock)
            {
                var envelope = _cachedKey64 is not null
                    ? ProfileEncryption.EncryptWithDerivedKey(profile.ChatHistory, _cachedKey64, _cachedSalt)
                    : ProfileEncryption.Encrypt(profile.ChatHistory, passphrase);
                bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOpts);
            }
            await AtomicWriteAsync(p, bytes);
        }
        finally
        {
            _historySaveLock.Release();
        }
    }

    /// <summary>
    /// Load chat history from the separate history file. Returns empty dict if
    /// no history file exists (new profile or pre-migration).
    /// </summary>
    public async Task<Dictionary<string, List<ChatMessage>>> LoadChatHistoryAsync(string userId, byte[] passphrase)
    {
        EnsureDir();
        var p = GetHistoryPath(userId);
        if (!File.Exists(p)) return new();
        try
        {
            var json = await File.ReadAllTextAsync(p);
            var envelope = JsonSerializer.Deserialize<ProfileEncryption.EncryptedEnvelope>(json, JsonOpts);
            if (envelope is null) return new();
            var history = ProfileEncryption.Decrypt<Dictionary<string, List<ChatMessage>>>(envelope, passphrase);
            return history ?? new();
        }
        catch { return new(); }
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes)
    {
        var tmpFile = path + ".tmp";
        await using (var fs = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await fs.WriteAsync(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmpFile, path, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { }
        }
    }

    /// <summary>
    /// Debounced save — coalesces rapid saves into a single write after DebounceMs.
    /// Use this for high-frequency mutations (message receive, ratchet state, etc.)
    /// The profile object is snapshotted at write time, so mutations between calls are captured.
    /// </summary>
    /// <summary>Event raised when a debounced save fails (disk error, etc.)</summary>
    public event Action<string>? OnSaveError;

    public void SaveProfileDebounced(Profile profile, byte[] passphrase)
    {
        _savePending = true;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs, token);
                if (token.IsCancellationRequested) return;
                _savePending = false;
                await SaveProfileAsync(profile, passphrase);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // M1: Surface save errors instead of silently losing data
                _savePending = true; // Mark as still pending so flush retries
                OnSaveError?.Invoke($"Profile save failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Debounced save for chat history only — does NOT re-encrypt the profile.
    /// </summary>
    public void SaveChatHistoryDebounced(Profile profile, byte[] passphrase)
    {
        _historySavePending = true;
        _historyDebounceCts?.Cancel();
        _historyDebounceCts?.Dispose();
        _historyDebounceCts = new CancellationTokenSource();
        var token = _historyDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs, token);
                if (token.IsCancellationRequested) return;
                _historySavePending = false;
                await SaveChatHistoryAsync(profile, passphrase);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _historySavePending = true;
                OnSaveError?.Invoke($"Chat history save failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Flush any pending debounced save immediately. Call on app exit or logout.
    /// </summary>
    public async Task FlushAsync(Profile? profile, byte[]? passphrase)
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
        _historyDebounceCts?.Cancel();
        _historyDebounceCts?.Dispose();
        _historyDebounceCts = null;
        if (profile is not null && passphrase is not null)
        {
            if (_savePending)
            {
                _savePending = false;
                await SaveProfileAsync(profile, passphrase);
            }
            if (_historySavePending)
            {
                _historySavePending = false;
                await SaveChatHistoryAsync(profile, passphrase);
            }
        }
        // H3: Zero cached key material on flush/logout
        ClearCachedKey();
    }

    /// <summary>
    /// Zero and discard cached key material. Call on logout or app exit.
    /// </summary>
    public void ClearCachedKey()
    {
        if (_cachedKey64 is not null) { CryptoService.ZeroOut(_cachedKey64); _cachedKey64 = null; }
        if (_cachedSalt is not null) { CryptoService.ZeroOut(_cachedSalt); _cachedSalt = null; }
        if (_cachedPassphrase is not null) { CryptoService.ZeroOut(_cachedPassphrase); _cachedPassphrase = null; }
    }

    public async Task<Profile> CreateProfileAsync(string internalId, string displayName, byte[] passphrase)
    {
        var encKP = CryptoService.GenerateKeyPair();
        var sigKP = CryptoService.GenerateSigningKeyPair();
        var deviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        var profile = new Profile
        {
            UserId = internalId,
            DisplayName = displayName,
            DeviceId = deviceId,
            PublicKey = encKP.PublicKey,
            SecretKey = encKP.SecretKey,
            SigningKey = sigKP.SigningKey,
            SigningSecretKey = sigKP.SigningSecretKey,
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

        // L5: Expire archived signed pre-keys older than 30 days
        if (profile.PreviousSignedPreKeys is not null)
        {
            var thirtyDaysAgo = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
            var before = profile.PreviousSignedPreKeys.Count;
            profile.PreviousSignedPreKeys = profile.PreviousSignedPreKeys
                .Where(k => k.ArchivedAt == 0 || k.ArchivedAt > thirtyDaysAgo)
                .ToList();
            if (profile.PreviousSignedPreKeys.Count != before) changed = true;
        }

        // Migrate contacts: add devices map if flat keys
        foreach (var (_, contact) in profile.Contacts)
        {
            if (contact.Devices.Count == 0 && contact.PublicKey.Length > 0)
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
        Profile profile, string internalId, byte[] publicKey, byte[]? signingKey,
        string? displayName, byte[] passphrase, Dictionary<string, DeviceKeys>? devices = null)
    {
        var deviceMap = devices ?? new Dictionary<string, DeviceKeys>
        {
            ["primary"] = new() { PublicKey = publicKey, SigningKey = signingKey }
        };

        if (profile.Contacts.TryGetValue(internalId, out var existing) &&
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(existing.PublicKey, publicKey))
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
        Profile profile, string internalId, byte[] publicKey, byte[]? signingKey,
        string? displayName, byte[] passphrase, Dictionary<string, DeviceKeys>? devices = null)
    {
        var deviceMap = devices ?? new Dictionary<string, DeviceKeys>
        {
            ["primary"] = new() { PublicKey = publicKey, SigningKey = signingKey }
        };

        // M10: Clear old ratchet states for this contact (old keys are compromised/changed)
        var keysToRemove = profile.RatchetStates.Keys
            .Where(k => k == internalId || k.StartsWith(internalId + ":"))
            .ToList();
        foreach (var k in keysToRemove)
            profile.RatchetStates.Remove(k);

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

    public async Task AddGroupAsync(Profile profile, string groupId, string name, byte[] groupKey, List<string>? members, byte[] passphrase)
    {
        profile.Groups[groupId] = new Group
        {
            Name = name,
            Key = groupKey,
            Members = members ?? new(),
        };
        await SaveProfileAsync(profile, passphrase);
    }

    // --- Place operations ---

    public async Task SavePlaceAsync(Profile profile, string placeId, Place place, byte[] passphrase)
    {
        profile.Places[placeId] = place;
        await SaveProfileAsync(profile, passphrase);
    }

    // --- Chat history ---

    public void AddChatMessage(Profile profile, string chatId, ChatMessage message, byte[] passphrase)
    {
        lock (_profileMutationLock)
        {
            if (!profile.ChatHistory.ContainsKey(chatId))
                profile.ChatHistory[chatId] = new();

            profile.ChatHistory[chatId].Add(message);

            // H3: Cap total chat history entries to prevent unbounded growth
            if (profile.ChatHistory.Count > 500)
            {
                var oldest = profile.ChatHistory.Keys
                    .Where(k => k != chatId)
                    .OrderBy(k => profile.ChatHistory[k].LastOrDefault()?.Ts ?? 0)
                    .First();
                profile.ChatHistory.Remove(oldest);
            }

            if (profile.ChatHistory[chatId].Count > 1000)
                profile.ChatHistory[chatId] = profile.ChatHistory[chatId].TakeLast(1000).ToList();
        }
        SaveChatHistoryDebounced(profile, passphrase);
    }

    // Keep async overload for backward compat (redirects to debounced)
    public Task AddChatMessageAsync(Profile profile, string chatId, ChatMessage message, byte[] passphrase)
    {
        AddChatMessage(profile, chatId, message, passphrase);
        return Task.CompletedTask;
    }

    public async Task CleanupExpiredMessagesAsync(Profile profile, byte[] passphrase)
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

        if (changed) await SaveChatHistoryAsync(profile, passphrase);
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

    public Task SaveRatchetStateAsync(Profile profile, string contactId, JsonElement state, byte[] passphrase, string? deviceId = null)
    {
        var rk = RatchetKey(contactId, deviceId);
        lock (_profileMutationLock) { profile.RatchetStates[rk] = state; }
        SaveProfileDebounced(profile, passphrase);
        return Task.CompletedTask;
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

    public Task SaveSenderKeyStateAsync(Profile profile, string groupId, JsonElement state, byte[] passphrase)
    {
        lock (_profileMutationLock) { profile.SenderKeys[groupId] = state; }
        SaveProfileDebounced(profile, passphrase);
        return Task.CompletedTask;
    }
}
