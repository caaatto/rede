using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    /// </summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt, int scryptN = 0)
    {
        int N = scryptN > 0 ? scryptN : ScryptNCurrent;
        var password = Encoding.UTF8.GetBytes(passphrase);
        var output = new byte[32];

        var result = crypto_pwhash_scryptsalsa208sha256_ll(
            password, (nuint)password.Length,
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
    public static EncryptedEnvelope Encrypt(object data, string passphrase)
    {
        var salt = SodiumCore.GetRandomBytes(16);
        var key = DeriveKey(passphrase, salt, ScryptNCurrent);
        var nonce = SodiumCore.GetRandomBytes(24);
        var jsonStr = JsonSerializer.Serialize(data);
        var plaintext = Encoding.UTF8.GetBytes(jsonStr);
        var encrypted = SecretBox.Create(plaintext, nonce, key);
        CryptoService.ZeroOut(key);
        CryptoService.ZeroOut(plaintext);

        // HMAC for integrity check
        var hmacSalt = new byte[salt.Length + 4];
        Buffer.BlockCopy(salt, 0, hmacSalt, 0, salt.Length);
        Encoding.UTF8.GetBytes("hmac").CopyTo(hmacSalt, salt.Length);
        var hmacKey = DeriveKey(passphrase, hmacSalt, ScryptNCurrent);
        using var hmacAlg = new HMACSHA256(hmacKey);
        var hmacValue = hmacAlg.ComputeHash(encrypted);
        CryptoService.ZeroOut(hmacKey);

        return new EncryptedEnvelope(
            Convert.ToBase64String(salt),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(encrypted),
            Convert.ToHexString(hmacValue).ToLowerInvariant(),
            ScryptNCurrent
        );
    }

    /// <summary>
    /// Decrypt profile from envelope. Mirrors: decryptProfile(envelope, passphrase)
    /// </summary>
    public static T? Decrypt<T>(EncryptedEnvelope envelope, string passphrase) where T : class
    {
        try
        {
            var salt = Convert.FromBase64String(envelope.Salt);
            var encrypted = Convert.FromBase64String(envelope.Data);
            var scryptN = envelope.ScryptN > 0 ? envelope.ScryptN : ScryptNLegacy;

            // HMAC verification
            if (!string.IsNullOrEmpty(envelope.Hmac))
            {
                var hmacSalt = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, hmacSalt, 0, salt.Length);
                Encoding.UTF8.GetBytes("hmac").CopyTo(hmacSalt, salt.Length);
                var hmacKey = DeriveKey(passphrase, hmacSalt, scryptN);
                using var hmacAlg = new HMACSHA256(hmacKey);
                var expected = Convert.ToHexString(hmacAlg.ComputeHash(encrypted)).ToLowerInvariant();
                CryptoService.ZeroOut(hmacKey);
                if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(envelope.Hmac),
                    Encoding.UTF8.GetBytes(expected)))
                {
                    return null;
                }
            }

            var key = DeriveKey(passphrase, salt, scryptN);
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var decrypted = SecretBox.Open(encrypted, nonce, key);
            CryptoService.ZeroOut(key);
            var json = Encoding.UTF8.GetString(decrypted);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }
}
