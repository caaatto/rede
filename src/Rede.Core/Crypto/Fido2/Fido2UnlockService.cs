using System.Security.Cryptography;
using System.Text;
using Rede.Core.Storage;
using Sodium;

namespace Rede.Core.Crypto.Fido2;

/// <summary>
/// High-level FIDO2 profile-unlock orchestration: enroll/remove hardware keys, generate and
/// consume recovery codes, and unlock the profile by recovering the Profile Master Secret (PMS).
///
/// The PMS is a random 32-byte secret mixed into the scrypt-derived profile key
/// (<see cref="ProfileEncryption.DeriveKey"/>) — so once a key is enrolled, the passphrase alone
/// no longer decrypts the profile. The PMS itself is stored only in wrapped form in the unlock
/// sidecar (<see cref="Fido2SidecarStore"/>): once per hardware key (wrap key = HKDF of the key's
/// hmac-secret output) and once for the recovery code (wrap key = scrypt of the code). All wraps
/// hold the SAME PMS, so any enrolled factor unlocks.
///
/// Owns the session PMS for the lifetime of a login; <see cref="ClearSession"/> zeros it on logout.
/// </summary>
public sealed class Fido2UnlockService
{
    private const string WrapInfo = "rede-pms-wrap-v1";
    private const int PmsLen = 32;

    private readonly IFido2Authenticator _auth;
    private readonly ProfileStore _store;
    private byte[]? _sessionPms;

    public Fido2UnlockService(IFido2Authenticator auth, ProfileStore store)
    {
        _auth = auth;
        _store = store;
    }

    /// <summary>True if a usable FIDO2 backend (libfido2 / platform WebAuthn) is loaded.</summary>
    public bool BackendAvailable => _auth.IsAvailable;

    /// <summary>True if at least one hardware key is enrolled for this profile.</summary>
    public bool HasFidoEnrolled(string hashHex) => Fido2SidecarStore.HasFidoEnrolled(hashHex);

    /// <summary>True if a recovery code has been generated for this profile.</summary>
    public bool HasRecovery(string hashHex) => Fido2SidecarStore.Load(hashHex)?.Recovery is not null;

    /// <summary>Enrolled keys for display in settings (no secret material).</summary>
    public IReadOnlyList<Fido2KeyEntry> ListKeys(string hashHex)
        => Fido2SidecarStore.Load(hashHex)?.Keys ?? new List<Fido2KeyEntry>();

    // --- Unlock ---

    /// <summary>
    /// Unlock via a connected hardware key. Returns the PMS (also pushed into the ProfileStore for
    /// this session), or null if no FIDO enrollment exists for this profile at all (no sidecar).
    /// Throws <see cref="Fido2Exception"/> for actionable problems (no backend, no device, PIN, touch,
    /// or a connected key that responded but matches none of the enrolled wraps → NoCredentials).
    /// </summary>
    public byte[]? TryUnlockWithKey(string hashHex, string? pin)
    {
        var sc = Fido2SidecarStore.Load(hashHex);
        if (sc is null || sc.Keys.Count == 0) return null;
        EnsureBackend();
        if (!_auth.HasDevice())
            throw new Fido2Exception(Fido2ErrorKind.NoDevice, "No security key detected. Plug it in and try again.");

        var allow = sc.Keys.Select(k => Convert.FromBase64String(k.CredentialId)).ToList();
        var salt = Convert.FromBase64String(sc.HmacSalt);
        var res = _auth.GetHmacSecret(sc.RpId, allow, salt, pin); // throws on PIN/no-touch/no-credentials

        byte[]? wrapKey = null;
        try
        {
            wrapKey = Hkdf.DeriveKey(res.HmacSecret, Array.Empty<byte>(), WrapInfo, 32);
            var credB64 = Convert.ToBase64String(res.CredentialId);

            // Fast path: the assertion told us which credential responded.
            var entry = sc.Keys.FirstOrDefault(k => k.CredentialId == credB64);
            if (entry is not null)
            {
                var pms = TryOpen(entry.WrappedPms, entry.Nonce, wrapKey);
                if (pms is not null) { SetSession(pms); return pms; }
            }

            // CTAP2 lets the authenticator OMIT the credential id in an assertion response when
            // the allow-list has exactly one entry — so res.CredentialId can come back empty (or,
            // rarely, not match). The hmac-secret we just derived only opens the wrap of the key
            // that actually responded, so try it against every enrolled entry: at most one opens.
            // Without this, a profile with a single enrolled key fails with "not enrolled" even
            // though the key is correct (recovery code still works because it never touches HW).
            foreach (var k in sc.Keys)
            {
                if (k.CredentialId == credB64) continue; // already tried above
                var pms = TryOpen(k.WrappedPms, k.Nonce, wrapKey);
                if (pms is not null) { SetSession(pms); return pms; }
            }

            // The key responded (so the hardware is fine) but its hmac-secret opened none of the
            // enrolled wraps — it's a different key than the one(s) enrolled for this profile, or
            // the sidecar was overwritten by an enrollment on another machine. Distinct from the
            // "no enrollment at all" null above so the login UI can tell the user which it is.
            throw new Fido2Exception(Fido2ErrorKind.NoCredentials,
                "This security key is not enrolled for this profile. Use a key you enrolled here, or your recovery code.");
        }
        finally
        {
            CryptoService.ZeroOut(wrapKey);
            CryptoService.ZeroOut(res.HmacSecret);
        }
    }

    /// <summary>
    /// Unlock via the recovery code. Returns the PMS (also pushed into the ProfileStore) or null if
    /// no recovery is configured or the code is wrong.
    /// </summary>
    public byte[]? UnlockWithRecovery(string hashHex, string recoveryCode)
    {
        var rec = Fido2SidecarStore.Load(hashHex)?.Recovery;
        if (rec is null) return null;

        var codeBytes = Encoding.UTF8.GetBytes(Base32.Canonicalize(recoveryCode));
        byte[]? wrapKey = null;
        try
        {
            var salt = Convert.FromBase64String(rec.ScryptSalt);
            wrapKey = ProfileEncryption.DeriveKey(codeBytes, salt, rec.ScryptN, 32);
            var pms = TryOpen(rec.WrappedPms, rec.Nonce, wrapKey);
            if (pms is null) return null;
            SetSession(pms);
            return pms;
        }
        finally
        {
            CryptoService.ZeroOut(codeBytes);
            CryptoService.ZeroOut(wrapKey);
        }
    }

    // --- Enrollment ---

    /// <summary>
    /// Enroll a new hardware key. On the FIRST key this generates the PMS and re-encrypts the
    /// profile + chat history to bind them to the new second factor; for additional keys it reuses
    /// the active session PMS (so the caller must already be unlocked via a key or recovery code).
    /// Prompts the user for PIN + touch via the authenticator. Throws <see cref="Fido2Exception"/>.
    /// </summary>
    public async Task<Fido2Credential> EnrollKeyAsync(Profile profile, byte[] passphrase, string keyName, string? pin)
    {
        EnsureBackend();
        if (!_auth.HasDevice())
            throw new Fido2Exception(Fido2ErrorKind.NoDevice, "No security key detected. Plug it in and try again.");
        if (!_auth.SupportsHmacSecret())
            throw new Fido2Exception(Fido2ErrorKind.HmacSecretUnsupported,
                "This security key does not support the hmac-secret extension required for profile unlock.");

        var hashHex = Fido2SidecarStore.HashForUserId(profile.UserId);
        var sc = Fido2SidecarStore.Load(hashHex) ?? new Fido2Sidecar();
        bool first = sc.Keys.Count == 0;

        byte[] pms;
        bool generated = false;
        if (first)
        {
            pms = _sessionPms ?? GenerateRandom(PmsLen);
            generated = _sessionPms is null;
            sc.RpId = "rede.local";
            sc.HmacSalt = Convert.ToBase64String(GenerateRandom(32));
        }
        else
        {
            if (_sessionPms is null)
                throw new Fido2Exception(Fido2ErrorKind.Other,
                    "Unlock with an existing key or your recovery code before adding another key.");
            pms = _sessionPms;
        }

        var userHandle = SHA256.HashData(Encoding.UTF8.GetBytes(profile.UserId));
        var cred = _auth.MakeCredential(sc.RpId, profile.DisplayName ?? profile.UserId, userHandle, pin);

        var salt = Convert.FromBase64String(sc.HmacSalt);
        var hmac = _auth.GetHmacSecret(sc.RpId, new[] { cred.CredentialId }, salt, pin);

        byte[]? wrapKey = null;
        byte[] wrapped;
        var nonce = GenerateRandom(24);
        try
        {
            wrapKey = Hkdf.DeriveKey(hmac.HmacSecret, Array.Empty<byte>(), WrapInfo, 32);
            wrapped = SecretBox.Create(pms, nonce, wrapKey);
        }
        finally
        {
            CryptoService.ZeroOut(wrapKey);
            CryptoService.ZeroOut(hmac.HmacSecret);
        }

        sc.Keys.Add(new Fido2KeyEntry
        {
            CredentialId = Convert.ToBase64String(cred.CredentialId),
            Nonce = Convert.ToBase64String(nonce),
            WrappedPms = Convert.ToBase64String(wrapped),
            Name = string.IsNullOrWhiteSpace(keyName) ? "Security key" : keyName.Trim(),
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        if (first)
        {
            SetSession(pms);
            // Write the sidecar FIRST, then re-encrypt the profile with the new factor. If we did it
            // the other way and crashed in between, the profile would be PMS-encrypted with NO sidecar
            // to recover the PMS — an unrecoverable lockout. This order means a crash leaves the profile
            // still passphrase-only + a sidecar whose PMS simply doesn't apply yet; the login self-heal
            // (passphrase-only fallback) then recovers cleanly and drops the stale sidecar.
            Fido2SidecarStore.Save(hashHex, sc);
            await _store.SaveProfileAsync(profile, passphrase);
            await _store.SaveChatHistoryAsync(profile, passphrase);
        }
        else
        {
            Fido2SidecarStore.Save(hashHex, sc);
        }
        if (generated) CryptoService.ZeroOut(pms); // SetSession kept its own clone
        return cred; // caller may register the public key with the server (Phase B)
    }

    /// <summary>
    /// Generate (or regenerate) the recovery code and store its wrapped PMS. Requires an active
    /// session PMS (caller must be unlocked). Returns the human-readable grouped code — show it ONCE.
    /// </summary>
    public string GenerateRecovery(string hashHex)
    {
        if (_sessionPms is null)
            throw new Fido2Exception(Fido2ErrorKind.Other, "Unlock with a security key before generating a recovery code.");
        var sc = Fido2SidecarStore.Load(hashHex)
            ?? throw new Fido2Exception(Fido2ErrorKind.Other, "Enroll a security key first.");

        var raw = GenerateRandom(16);
        var canonical = Base32.Encode(raw);
        CryptoService.ZeroOut(raw);

        var salt = GenerateRandom(16);
        var nonce = GenerateRandom(24);
        var codeBytes = Encoding.UTF8.GetBytes(canonical);
        byte[]? wrapKey = null;
        byte[] wrapped;
        try
        {
            wrapKey = ProfileEncryption.DeriveKey(codeBytes, salt, ProfileEncryption.ScryptNCurrent, 32);
            wrapped = SecretBox.Create(_sessionPms, nonce, wrapKey);
        }
        finally
        {
            CryptoService.ZeroOut(wrapKey);
            CryptoService.ZeroOut(codeBytes);
        }

        sc.Recovery = new Fido2RecoveryEntry
        {
            ScryptSalt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            WrappedPms = Convert.ToBase64String(wrapped),
            ScryptN = ProfileEncryption.ScryptNCurrent,
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        Fido2SidecarStore.Save(hashHex, sc);
        return Base32.Group(canonical);
    }

    // --- Removal ---

    /// <summary>
    /// Remove one enrolled key. If it was the last hardware key, FIDO is disabled entirely
    /// (profile re-encrypted passphrase-only, sidecar deleted) so the user is never locked out.
    /// </summary>
    public async Task RemoveKeyAsync(Profile profile, byte[] passphrase, string credentialIdB64)
    {
        var hashHex = Fido2SidecarStore.HashForUserId(profile.UserId);
        var sc = Fido2SidecarStore.Load(hashHex);
        if (sc is null) return;
        sc.Keys.RemoveAll(k => k.CredentialId == credentialIdB64);
        if (sc.Keys.Count == 0)
        {
            await DisableAsync(profile, passphrase);
            return;
        }
        Fido2SidecarStore.Save(hashHex, sc);
    }

    /// <summary>
    /// Turn FIDO2 off completely: re-encrypt profile + history with the passphrase alone and delete
    /// the sidecar. Caller must already be logged in (profile fully decrypted in memory).
    /// </summary>
    public async Task DisableAsync(Profile profile, byte[] passphrase)
    {
        var hashHex = Fido2SidecarStore.HashForUserId(profile.UserId);
        _store.SetActivePms(null);
        await _store.SaveProfileAsync(profile, passphrase);
        await _store.SaveChatHistoryAsync(profile, passphrase);
        Fido2SidecarStore.Delete(hashHex);
        ClearSession();
    }

    // --- Session lifecycle ---

    private void SetSession(byte[] pms)
    {
        if (_sessionPms is not null) CryptoService.ZeroOut(_sessionPms);
        _sessionPms = (byte[])pms.Clone();
        _store.SetActivePms(_sessionPms);
    }

    /// <summary>Zero the session PMS and clear it from the ProfileStore. Call on logout / exit.</summary>
    public void ClearSession()
    {
        if (_sessionPms is not null) { CryptoService.ZeroOut(_sessionPms); _sessionPms = null; }
        _store.ClearActivePms();
    }

    // --- Helpers ---

    private void EnsureBackend()
    {
        if (!_auth.IsAvailable)
            throw new Fido2Exception(Fido2ErrorKind.NotAvailable,
                "Security-key support is not installed. Install it from Settings first.");
    }

    private static byte[] GenerateRandom(int n) => SodiumCore.GetRandomBytes(n);

    private static byte[]? TryOpen(string cipherB64, string nonceB64, byte[] key)
    {
        try
        {
            return SecretBox.Open(
                Convert.FromBase64String(cipherB64),
                Convert.FromBase64String(nonceB64),
                key);
        }
        catch { return null; }
    }
}
