using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rede.Core.Storage;
using Sodium;

namespace Rede.Core.Crypto;

/// <summary>
/// Profile encryption at rest using scrypt + nacl.secretbox + HMAC integrity.
/// Mirrors: encryptProfile, decryptProfile, deriveKey in crypto.js
/// </summary>
public static class ProfileEncryption
{
    public const int ScryptNCurrent = 1048576; // 2^20
    public const int ScryptNLegacy = 16384;    // 2^14

    public record EncryptedEnvelope(string Salt, string Nonce, string Data, string Hmac, int ScryptN);

    // P/Invoke to libsodium's low-level scrypt with direct N/r/p parameters.
    // Sodium.Core's PasswordHash.ScryptHashBinary uses opsLimit/memLimit which
    // don't map directly to N/r/p — we need the _ll variant for v1 compat.
    [DllImport("libsodium", CallingConvention = CallingConvention.Cdecl)]
    private static extern int crypto_pwhash_scryptsalsa208sha256_ll(
        byte[] password, nuint passwordLen,
        byte[] salt, nuint saltLen,
        ulong N, uint r, uint p,
        byte[] buf, nuint bufLen);

    /// <summary>
    /// Derive a 32-byte key using scrypt. Mirrors: deriveKey(passphrase, salt, scryptN)
    /// Uses N/r=8/p=1 parameters matching the JS client's crypto.scryptSync call.
    /// Caller owns <paramref name="passphrase"/> (bytes are not zeroed here).
    /// </summary>
    public static byte[] DeriveKey(byte[] passphrase, byte[] salt, int scryptN = 0, int outputLen = 32)
    {
        int N = scryptN > 0 ? scryptN : ScryptNCurrent;
        var output = new byte[outputLen];

        var result = crypto_pwhash_scryptsalsa208sha256_ll(
            passphrase, (nuint)passphrase.Length,
            salt, (nuint)salt.Length,
            (ulong)N, 8, 1,  // N, r=8, p=1
            output, (nuint)output.Length);

        if (result != 0)
            throw new CryptographicException("scrypt key derivation failed");

        return output;
    }

    /// <summary>
    /// Encrypt profile data to an envelope. Mirrors: encryptProfile(data, passphrase)
    /// </summary>
    public static EncryptedEnvelope Encrypt(object data, byte[] passphrase)
    {
        var salt = SodiumCore.GetRandomBytes(16);
        var combined = DeriveKey(passphrase, salt, ScryptNCurrent, 64);
        var envelope = EncryptWithDerivedKey(data, combined, salt);
        CryptoService.ZeroOut(combined);
        return envelope;
    }

    /// <summary>
    /// Encrypt using a pre-derived 64-byte key (32 enc + 32 hmac), skipping scrypt.
    /// The salt must be the same salt used to derive the key, OR a fresh random salt
    /// if the caller is providing a cached key (in which case pass cachedSalt=null and
    /// a fresh salt will be embedded in the envelope for decryption bookkeeping).
    /// </summary>
    public static EncryptedEnvelope EncryptWithDerivedKey(object data, byte[] cachedKey64, byte[]? salt = null)
    {
        salt ??= SodiumCore.GetRandomBytes(16);
        var key = new byte[32];
        var hmacKey = new byte[32];
        Buffer.BlockCopy(cachedKey64, 0, key, 0, 32);
        Buffer.BlockCopy(cachedKey64, 32, hmacKey, 0, 32);

        var nonce = SodiumCore.GetRandomBytes(24);
        // Use source-generated serializer + direct UTF-8 bytes (no intermediate string)
        var plaintext = data is Profile profile
            ? JsonSerializer.SerializeToUtf8Bytes(profile, ProfileJsonContext.Default.Profile)
            : JsonSerializer.SerializeToUtf8Bytes(data);
        var encrypted = SecretBox.Create(plaintext, nonce, key);
        CryptoService.ZeroOut(key);
        CryptoService.ZeroOut(plaintext);

        // HMAC for integrity check
        using var hmacAlg = new HMACSHA256(hmacKey);
        var hmacValue = hmacAlg.ComputeHash(encrypted);
        CryptoService.ZeroOut(hmacKey);
        var hmacHex = Convert.ToHexString(hmacValue).ToLowerInvariant();
        CryptoService.ZeroOut(hmacValue);

        return new EncryptedEnvelope(
            Convert.ToBase64String(salt),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(encrypted),
            hmacHex,
            ScryptNCurrent
        );
    }

    /// <summary>
    /// Decrypt profile from envelope. Mirrors: decryptProfile(envelope, passphrase)
    /// </summary>
    public static T? Decrypt<T>(EncryptedEnvelope envelope, byte[] passphrase) where T : class
    {
        try
        {
            var salt = Convert.FromBase64String(envelope.Salt);
            var encrypted = Convert.FromBase64String(envelope.Data);
            var scryptN = envelope.ScryptN > 0 ? envelope.ScryptN : ScryptNLegacy;

            // L7: Try single 64-byte derivation first (new format), fall back to separate salt (legacy)
            var combined = DeriveKey(passphrase, salt, scryptN, 64);
            var key = new byte[32];
            var hmacKey = new byte[32];
            Buffer.BlockCopy(combined, 0, key, 0, 32);
            Buffer.BlockCopy(combined, 32, hmacKey, 0, 32);
            CryptoService.ZeroOut(combined);

            // HMAC verification — MANDATORY. A missing or empty HMAC field is a hard
            // decrypt failure: an attacker must not be able to downgrade a profile to
            // the unauthenticated legacy format. Profiles written by older client
            // versions that never populated the HMAC field cannot be opened by this
            // build; users on such profiles need to re-save from an older Rede once
            // before upgrading (or restore from backup).
            if (string.IsNullOrEmpty(envelope.Hmac))
            {
                CryptoService.ZeroOut(key);
                CryptoService.ZeroOut(hmacKey);
                return null;
            }
            {
                using var hmacAlg = new HMACSHA256(hmacKey);
                var expected = Convert.ToHexString(hmacAlg.ComputeHash(encrypted)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(envelope.Hmac),
                    Encoding.UTF8.GetBytes(expected)))
                {
                    // Legacy HMAC fallback: separate-salt "hmac" derivation. Kept only
                    // as a single extra try — no second legacy layer. If this also
                    // fails, the envelope is rejected.
                    CryptoService.ZeroOut(hmacKey);
                    var hmacSalt = new byte[salt.Length + 4];
                    Buffer.BlockCopy(salt, 0, hmacSalt, 0, salt.Length);
                    Encoding.UTF8.GetBytes("hmac").CopyTo(hmacSalt, salt.Length);
                    hmacKey = DeriveKey(passphrase, hmacSalt, scryptN);
                    using var hmacAlg2 = new HMACSHA256(hmacKey);
                    expected = Convert.ToHexString(hmacAlg2.ComputeHash(encrypted)).ToLowerInvariant();
                    CryptoService.ZeroOut(hmacKey);
                    if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(envelope.Hmac),
                        Encoding.UTF8.GetBytes(expected)))
                    {
                        CryptoService.ZeroOut(key);
                        return null;
                    }
                    // Legacy format uses separate 32-byte key derivation
                    CryptoService.ZeroOut(key);
                    key = DeriveKey(passphrase, salt, scryptN);
                }
            }
            CryptoService.ZeroOut(hmacKey);

            var nonce = Convert.FromBase64String(envelope.Nonce);
            var decrypted = SecretBox.Open(encrypted, nonce, key);
            CryptoService.ZeroOut(key);
            var json = Encoding.UTF8.GetString(decrypted);
            CryptoService.ZeroOut(decrypted);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }
}
