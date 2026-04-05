using System.Text.Json.Nodes;
using Rede.Core.Crypto;
using Rede.Core.Networking;
using Rede.Core.Protocol;
using Rede.Core.Storage;

namespace Rede.Core.Services;

/// <summary>
/// Orchestrates registration, auth challenge-response, and device link flows.
/// Mirrors: authenticate(), REGISTER_OK, AUTH_CHALLENGE, AUTH_OK handlers in index.js
/// </summary>
public class AuthService : IDisposable
{
    private readonly RedeConnection _conn;
    private readonly ProfileStore _store;

    public void Dispose() { GC.SuppressFinalize(this); }

    public event Action<string>? OnStatusUpdate;
    public event Action<string>? OnSystemMessage;
    public event Action? OnAuthSuccess;
    public event Action<string>? OnAuthFailed;

    public Profile? Profile { get; private set; }
    /// <summary>
    /// UTF-8 bytes of the user's passphrase. Lives only in this property + the owning
    /// MainWindow, which zeros it on logout/close. Services hold references but never
    /// allocate their own copies.
    /// </summary>
    public byte[]? Passphrase { get; private set; }

    public AuthService(RedeConnection conn, ProfileStore store)
    {
        _conn = conn;
        _store = store;
        RegisterHandlers();
    }

    private void RegisterHandlers()
    {
        _conn.On(Msg.RegisterOk, HandleRegisterOk);
        _conn.On(Msg.RegisterFail, HandleRegisterFail);
        _conn.On(Msg.AuthChallenge, HandleAuthChallenge);
        _conn.On(Msg.AuthOk, HandleAuthOk);
        _conn.On(Msg.AuthFail, HandleAuthFail);
        _conn.On(Msg.DeviceLinkOk, HandleDeviceLinkOk);
        _conn.On(Msg.DeviceLinkFail, HandleDeviceLinkFail);
    }

    /// <summary>Login with existing profile. Caller owns <paramref name="passphrase"/>.</summary>
    public async Task<bool> LoginAsync(string userId, byte[] passphrase)
    {
        Passphrase = passphrase;
        Profile = await _store.LoadProfileAsync(userId, passphrase);
        if (Profile is null) return false;
        MigrateTrayDefault(Profile);

        _conn.On(Msg.AuthChallenge, HandleAuthChallenge);
        SendAuth();
        return true;
    }

    /// <summary>One-time upgrade: pre-v2.17 profiles had MinimizeToTray=false by default.
    /// Flip them to true (matching the new default) unless the user already migrated.</summary>
    private static void MigrateTrayDefault(Rede.Core.Storage.Profile profile)
    {
        if (profile.TrayDefaultMigratedV217) return;
        profile.MinimizeToTray = true;
        profile.TrayDefaultMigratedV217 = true;
    }

    /// <summary>Quick login by file hash — UserId is recovered from the decrypted profile.</summary>
    public async Task<bool> LoginByHashAsync(string hashHex, byte[] passphrase)
    {
        Passphrase = passphrase;
        Profile = await _store.LoadProfileByHashAsync(hashHex, passphrase);
        if (Profile is null) return false;
        MigrateTrayDefault(Profile);

        _conn.On(Msg.AuthChallenge, HandleAuthChallenge);
        SendAuth();
        return true;
    }

    /// <summary>Register a new account.</summary>
    public async Task RegisterAsync(string displayName, byte[] passphrase, string inviteCode)
    {
        Passphrase = passphrase;
        Profile = await _store.CreateProfileAsync("pending", displayName, passphrase);

        var publicKeyB64 = Convert.ToBase64String(Profile.PublicKey);
        var signingKeyB64 = Convert.ToBase64String(Profile.SigningKey);
        var proof = CryptoService.SignStringB64(
            displayName + publicKeyB64,
            Profile.SigningSecretKey);

        _conn.Send(Msg.Register, ProtocolSerializer.Payload(
            ("inviteCode", JsonValue.Create(inviteCode)),
            ("publicKey", JsonValue.Create(publicKeyB64)),
            ("signingKey", JsonValue.Create(signingKeyB64)),
            ("displayName", JsonValue.Create(displayName)),
            ("deviceId", JsonValue.Create(Profile.DeviceId)),
            ("proof", JsonValue.Create(proof))
        ));
    }

    /// <summary>Link a new device to existing account.</summary>
    public async Task LinkDeviceAsync(string userId, byte[] passphrase, string linkCode)
    {
        Passphrase = passphrase;
        Profile = await _store.CreateProfileAsync("pending", userId, passphrase);

        var codeHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(linkCode))).ToLowerInvariant();

        var publicKeyB64 = Convert.ToBase64String(Profile.PublicKey);
        var signingKeyB64 = Convert.ToBase64String(Profile.SigningKey);
        var proof = CryptoService.SignStringB64(
            $"DEVICE_LINK:{codeHash}:{publicKeyB64}",
            Profile.SigningSecretKey);

        _conn.Send(Msg.DeviceLinkUse, ProtocolSerializer.Payload(
            ("userId", JsonValue.Create(userId)),
            ("codeHash", JsonValue.Create(codeHash)),
            ("publicKey", JsonValue.Create(publicKeyB64)),
            ("signingKey", JsonValue.Create(signingKeyB64)),
            ("deviceId", JsonValue.Create(Profile.DeviceId)),
            ("proof", JsonValue.Create(proof))
        ));
    }

    private void SendAuth()
    {
        if (Profile is null) return;
        _conn.Send(Msg.Auth, ProtocolSerializer.Payload(
            ("userId", JsonValue.Create(Profile.UserId)),
            ("deviceId", JsonValue.Create(Profile.DeviceId))
        ));
    }

    /// <summary>Re-authenticate on reconnect.</summary>
    public void Reauthenticate()
    {
        if (Profile is not null)
            SendAuth();
    }

    // --- Handlers ---

    private async void HandleRegisterOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var oldUserId = Profile.UserId;
        Profile.UserId = ProtocolSerializer.GetString(msg, "userId") ?? Profile.UserId;
        Profile.DisplayName = ProtocolSerializer.GetString(msg, "displayName") ?? Profile.DisplayName;

        var deviceId = ProtocolSerializer.GetString(msg, "deviceId");
        if (deviceId is not null) Profile.DeviceId = deviceId;

        // TOFU pin server signing key (validate 32-byte base64 format)
        var serverSigKey = ProtocolSerializer.GetString(msg, "serverSigningKey");
        if (serverSigKey is not null && IsValidBase64Key(serverSigKey, 32))
        {
            var serverSigKeyBytes = Convert.FromBase64String(serverSigKey);
            Profile.ServerSigningKey = serverSigKeyBytes;
            _conn.ServerSigningKey = serverSigKeyBytes;
        }

        // Save under the real userId (server-assigned)
        await _store.SaveProfileAsync(Profile, Passphrase);

        // Clean up the temp "pending" profile file
        if (oldUserId != Profile.UserId)
            _store.DeleteProfile(oldUserId);

        OnSystemMessage?.Invoke("Registration successful!");
        OnSystemMessage?.Invoke($"Your ID: {Profile.UserId}");
        OnSystemMessage?.Invoke($"Fingerprint: {CryptoService.Fingerprint(Profile.PublicKey)}");
        OnStatusUpdate?.Invoke($"{Profile.DisplayName} ({Profile.UserId}) | E2EE + PFS");

        // Upload initial pre-key bundle
        UploadPreKeysIfNeeded(0);
        OnAuthSuccess?.Invoke();
    }

    private void HandleRegisterFail(JsonObject msg)
    {
        var raw = ProtocolSerializer.GetString(msg, "error") ?? "Unknown error";
        // H9: Sanitize server error messages
        OnAuthFailed?.Invoke($"Registration failed: {SanitizeServerError(raw)}");
    }

    private void HandleAuthChallenge(JsonObject msg)
    {
        if (Profile is null) return;
        var challenge = ProtocolSerializer.GetString(msg, "challenge");
        if (challenge is null) return;

        // Domain-separated: sign "AUTH_CHALLENGE:<base64>" to prevent cross-protocol signature reuse
        var signature = CryptoService.SignStringB64("AUTH_CHALLENGE:" + challenge, Profile.SigningSecretKey);
        _conn.Send(Msg.AuthResponse, ProtocolSerializer.Payload(
            ("signature", JsonValue.Create(signature))
        ));
    }

    private async void HandleAuthOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var name = Profile.DisplayName;
        OnStatusUpdate?.Invoke($"{name} ({Profile.UserId}) | E2EE + PFS");
        OnSystemMessage?.Invoke("Authenticated.");

        // TOFU pin server signing key
        var serverSigKey = ProtocolSerializer.GetString(msg, "serverSigningKey");
        if (serverSigKey is not null && IsValidBase64Key(serverSigKey, 32))
        {
            var serverSigKeyBytes = Convert.FromBase64String(serverSigKey);
            if (Profile.ServerSigningKey is null || Profile.ServerSigningKey.Length == 0)
            {
                Profile.ServerSigningKey = serverSigKeyBytes;
                _conn.ServerSigningKey = serverSigKeyBytes;
                await _store.SaveProfileAsync(Profile, Passphrase);
            }
            else if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Profile.ServerSigningKey, serverSigKeyBytes))
            {
                OnSystemMessage?.Invoke("WARNING: Server signing key has CHANGED! Possible MITM attack.");
            }
            else
            {
                _conn.ServerSigningKey = serverSigKeyBytes;
            }
        }

        // Store delivery token for sealed sender
        var deliveryToken = ProtocolSerializer.GetString(msg, "deliveryToken");
        if (deliveryToken is not null)
        {
            Profile.DeliveryToken = deliveryToken;
            await _store.SaveProfileAsync(Profile, Passphrase);
        }

        // Upload pre-keys if needed
        var prekeyCount = ProtocolSerializer.GetInt(msg, "prekeyCount", -1);
        if (prekeyCount >= 0) UploadPreKeysIfNeeded(prekeyCount);

        OnAuthSuccess?.Invoke();
    }

    private void HandleAuthFail(JsonObject msg)
    {
        var raw = ProtocolSerializer.GetString(msg, "error") ?? "Authentication failed";
        // H9: Sanitize server error messages
        OnAuthFailed?.Invoke(SanitizeServerError(raw));
    }

    private static string SanitizeServerError(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return "Unknown error.";
        var s = System.Text.RegularExpressions.Regex.Replace(msg, @"<[^>]+>", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"https?://\S+", "[link]");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\x00-\x1f\x7f]", "");
        return s.Length > 200 ? s[..200] + "..." : s;
    }

    private void HandleDeviceLinkOk(JsonObject msg)
    {
        if (Profile is null || Passphrase is null) return;

        var assignedUserId = ProtocolSerializer.GetString(msg, "userId");
        if (assignedUserId is not null) Profile.UserId = assignedUserId;

        OnSystemMessage?.Invoke("Device linked successfully!");
        OnSystemMessage?.Invoke($"Linked to: {Profile.UserId}");

        _store.SaveProfileDebounced(Profile, Passphrase);
        OnAuthSuccess?.Invoke();
    }

    private void HandleDeviceLinkFail(JsonObject msg)
    {
        var error = ProtocolSerializer.GetString(msg, "error") ?? "Device link failed";
        OnAuthFailed?.Invoke(SanitizeServerError(error)); // H12: Sanitize like other handlers
    }

    private static bool IsValidBase64Key(string key, int expectedBytes)
    {
        try
        {
            var bytes = Convert.FromBase64String(key);
            return bytes.Length == expectedBytes;
        }
        catch { return false; }
    }

    private void UploadPreKeysIfNeeded(int prekeyCount)
    {
        if (Profile is null || Passphrase is null) return;
        const int threshold = 5;
        if (prekeyCount > threshold) return;

        var bundle = X3dh.GeneratePreKeyBundle(Profile.SigningSecretKey);

        // Store private keys
        Profile.SignedPreKey = new KeyPairData
        {
            PublicKey = bundle.PrivateKeys.SignedPreKey.PublicKey,
            SecretKey = bundle.PrivateKeys.SignedPreKey.SecretKey,
        };
        Profile.SignedPreKeySig = Convert.FromBase64String(bundle.PublicBundle.SignedPreKeySig);

        var startId = Profile.NextPreKeyId;
        foreach (var (otpk, i) in bundle.PrivateKeys.OneTimePreKeys.Select((v, i) => (v, i)))
        {
            Profile.OneTimePreKeys.Add(new OneTimePreKey
            {
                Id = startId + i,
                PublicKey = otpk.PublicKey,
                SecretKey = otpk.SecretKey,
            });
        }
        Profile.NextPreKeyId = startId + bundle.PrivateKeys.OneTimePreKeys.Count;

        _store.SaveProfileDebounced(Profile, Passphrase);

        // Upload public bundle
        var otpkArray = new JsonArray();
        foreach (var otpk in bundle.PublicBundle.OneTimePreKeys)
            otpkArray.Add(JsonValue.Create(otpk.Key));

        _conn.Send(Msg.UploadPrekeys, ProtocolSerializer.Payload(
            ("signedPreKey", JsonValue.Create(bundle.PublicBundle.SignedPreKey)),
            ("signedPreKeySig", JsonValue.Create(bundle.PublicBundle.SignedPreKeySig)),
            ("oneTimePreKeys", otpkArray)
        ));
    }
}
