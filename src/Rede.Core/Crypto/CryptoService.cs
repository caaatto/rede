using System.Security.Cryptography;
using System.Text;
using Sodium;

namespace Rede.Core.Crypto;

/// <summary>
/// Core cryptographic operations: key generation, signatures, fingerprints.
/// Key material is passed as byte[] so it can be zeroed after use.
/// Wire artefacts (ciphertext, nonces, signatures on the wire) remain base64 strings —
/// they don't need zeroing.
/// </summary>
public static class CryptoService
{
    public record KeyPairBytes(byte[] PublicKey, byte[] SecretKey);
    public record SigningKeyPairBytes(byte[] SigningKey, byte[] SigningSecretKey);

    // --- Memory safety ---

    public static void ZeroOut(byte[]? arr)
    {
        if (arr is not null) CryptographicOperations.ZeroMemory(arr);
    }

    // --- Key Generation ---

    /// <summary>Generate X25519 keypair for encryption.</summary>
    public static KeyPairBytes GenerateKeyPair()
    {
        var kp = PublicKeyBox.GenerateKeyPair();
        // Copy to arrays we own (libsodium wrapper disposes its buffers)
        var pk = (byte[])kp.PublicKey.Clone();
        var sk = (byte[])kp.PrivateKey.Clone();
        ZeroOut(kp.PrivateKey);
        return new KeyPairBytes(pk, sk);
    }

    /// <summary>Generate Ed25519 keypair for signing.</summary>
    public static SigningKeyPairBytes GenerateSigningKeyPair()
    {
        var kp = PublicKeyAuth.GenerateKeyPair();
        var pk = (byte[])kp.PublicKey.Clone();
        var sk = (byte[])kp.PrivateKey.Clone();
        ZeroOut(kp.PrivateKey);
        return new SigningKeyPairBytes(pk, sk);
    }

    /// <summary>Generate random 32-byte symmetric key (e.g. group/metadata key).</summary>
    public static byte[] GenerateSymmetricKey()
    {
        return SodiumCore.GetRandomBytes(32);
    }

    // --- Signatures (Ed25519) ---

    /// <summary>Sign raw bytes with Ed25519.</summary>
    public static byte[] Sign(byte[] data, byte[] signingSecretKey)
    {
        return PublicKeyAuth.SignDetached(data, signingSecretKey);
    }

    /// <summary>Sign UTF-8 string with Ed25519.</summary>
    public static byte[] SignString(string text, byte[] signingSecretKey)
    {
        return PublicKeyAuth.SignDetached(Encoding.UTF8.GetBytes(text), signingSecretKey);
    }

    /// <summary>Verify Ed25519 signature over raw bytes.</summary>
    public static bool Verify(byte[] data, byte[] signature, byte[] signingKey)
    {
        try { return PublicKeyAuth.VerifyDetached(signature, data, signingKey); }
        catch { return false; }
    }

    /// <summary>Verify Ed25519 signature over UTF-8 string.</summary>
    public static bool VerifyString(string text, byte[] signature, byte[] signingKey)
    {
        try { return PublicKeyAuth.VerifyDetached(signature, Encoding.UTF8.GetBytes(text), signingKey); }
        catch { return false; }
    }

    // --- Base64-string bridge overloads (for wire-format callers) ---

    /// <summary>Verify where data/sig/key are all base64 strings from the wire.</summary>
    public static bool VerifyB64(string dataB64, string signatureB64, byte[] signingKey)
    {
        try
        {
            var data = Convert.FromBase64String(dataB64);
            var sig = Convert.FromBase64String(signatureB64);
            return PublicKeyAuth.VerifyDetached(sig, data, signingKey);
        }
        catch { return false; }
    }

    /// <summary>Verify raw bytes against a base64 signature (wire format).</summary>
    public static bool VerifyBytes(byte[] data, string signatureB64, byte[] signingKey)
    {
        try
        {
            var sig = Convert.FromBase64String(signatureB64);
            return PublicKeyAuth.VerifyDetached(sig, data, signingKey);
        }
        catch { return false; }
    }

    /// <summary>Sign raw bytes and return signature as base64 (wire format).</summary>
    public static string SignBytesB64(byte[] data, byte[] signingSecretKey)
    {
        return Convert.ToBase64String(PublicKeyAuth.SignDetached(data, signingSecretKey));
    }

    /// <summary>Sign UTF-8 string and return signature as base64 (wire format).</summary>
    public static string SignStringB64(string text, byte[] signingSecretKey)
    {
        return Convert.ToBase64String(PublicKeyAuth.SignDetached(Encoding.UTF8.GetBytes(text), signingSecretKey));
    }

    // --- Fingerprint ---

    /// <summary>
    /// Fingerprint a public key for display.
    /// Hashes the base64 representation to stay wire-compatible with the JS client.
    /// Returns 32-hex-char string in 4-char chunks separated by spaces.
    /// </summary>
    public static string Fingerprint(byte[] publicKey)
    {
        var b64 = Convert.ToBase64String(publicKey);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(b64));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        var chunks = new string[8];
        for (int i = 0; i < 8; i++) chunks[i] = hex.Substring(i * 4, 4);
        return string.Join(' ', chunks);
    }

    // --- NaCl box (public-key encryption) ---

    /// <summary>Encrypt for recipient using X25519 + XSalsa20-Poly1305 (nacl.box).</summary>
    public static (string Encrypted, string Nonce) EncryptFor(string plaintext, byte[] recipientPub, byte[] senderSecret)
    {
        var nonce = SodiumCore.GetRandomBytes(24);
        var messageBytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = PublicKeyBox.Create(messageBytes, nonce, senderSecret, recipientPub);
        return (Convert.ToBase64String(encrypted), Convert.ToBase64String(nonce));
    }

    /// <summary>Decrypt from sender using X25519 + XSalsa20-Poly1305.</summary>
    public static string? DecryptFrom(string encryptedB64, string nonceB64, byte[] senderPub, byte[] recipientSecret)
    {
        try
        {
            var encrypted = Convert.FromBase64String(encryptedB64);
            var nonce = Convert.FromBase64String(nonceB64);
            var decrypted = PublicKeyBox.Open(encrypted, nonce, recipientSecret, senderPub);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch { return null; }
    }

    // --- NaCl secretbox (symmetric encryption) ---

    /// <summary>Encrypt with symmetric key (XSalsa20-Poly1305).</summary>
    public static (string Encrypted, string Nonce) EncryptSecretBox(string plaintext, byte[] key)
    {
        var nonce = SodiumCore.GetRandomBytes(24);
        var messageBytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = SecretBox.Create(messageBytes, nonce, key);
        return (Convert.ToBase64String(encrypted), Convert.ToBase64String(nonce));
    }

    /// <summary>Decrypt with symmetric key.</summary>
    public static string? DecryptSecretBox(string encryptedB64, string nonceB64, byte[] key)
    {
        try
        {
            var encrypted = Convert.FromBase64String(encryptedB64);
            var nonce = Convert.FromBase64String(nonceB64);
            var decrypted = SecretBox.Open(encrypted, nonce, key);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch { return null; }
    }

    // --- Raw X25519 DH ---

    /// <summary>Known Curve25519 low-order points that produce all-zeros shared secrets.</summary>
    private static readonly byte[][] LowOrderPoints = {
        new byte[32],
        new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new byte[] { 224, 235, 122, 124, 59, 65, 184, 174, 22, 86, 227, 250, 241, 159, 196, 128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new byte[] { 95, 156, 149, 188, 163, 80, 140, 36, 177, 208, 177, 85, 156, 131, 239, 91, 4, 68, 92, 196, 88, 28, 142, 134, 216, 34, 78, 221, 208, 159, 17, 87 },
        new byte[] { 236, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 127 },
        new byte[] { 237, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 127 },
        new byte[] { 238, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 127 },
    };

    public static bool IsValidDhPublicKey(byte[] publicKey)
    {
        if (publicKey.Length != 32) return false;
        foreach (var lop in LowOrderPoints)
            if (CryptographicOperations.FixedTimeEquals(publicKey, lop)) return false;
        return true;
    }

    /// <summary>Raw X25519 scalar multiplication. Rejects low-order and zero outputs.</summary>
    public static byte[] Dh(byte[] secretKey, byte[] publicKey)
    {
        var result = Sodium.ScalarMult.Mult(secretKey, publicKey);
        if (result.All(b => b == 0))
            throw new CryptographicException("DH produced all-zeros output — invalid public key.");
        return result;
    }

    /// <summary>Get X25519 public key from secret key.</summary>
    public static byte[] PublicKeyFromSecret(byte[] secretKey)
    {
        return Sodium.ScalarMult.Base(secretKey);
    }

    // --- Server signature verification ---

    public static bool VerifyServerSignature(string rawJson, byte[]? serverSigningKey)
    {
        if (serverSigningKey is null || serverSigningKey.Length == 0) return false;
        try
        {
            var msg = System.Text.Json.Nodes.JsonNode.Parse(rawJson)?.AsObject();
            if (msg is null) return false;
            var sigNode = msg["serverSig"];
            if (sigNode is null) return false;
            var sigB64 = sigNode.GetValue<string>();
            var body = StripJsonField(rawJson, "serverSig");
            var data = Encoding.UTF8.GetBytes(body);
            var sig = Convert.FromBase64String(sigB64);
            return PublicKeyAuth.VerifyDetached(sig, data, serverSigningKey);
        }
        catch { return false; }
    }

    private static string StripJsonField(string json, string fieldName)
    {
        var pattern = $@",\s*""{fieldName}""\s*:\s*""[^""\\]*(?:\\.[^""\\]*)*""";
        var result = System.Text.RegularExpressions.Regex.Replace(json, pattern, "");
        if (result == json)
        {
            pattern = $@"""{fieldName}""\s*:\s*""[^""\\]*(?:\\.[^""\\]*)*""\s*,";
            result = System.Text.RegularExpressions.Regex.Replace(json, pattern, "");
        }
        return result;
    }

    // --- Group key signing (legacy protocol — groupKey serialized as base64 in payload) ---

    public static byte[] SignGroupKey(string groupId, string groupName, byte[] groupKey, byte[] signingSecretKey)
    {
        var payload = $"GROUPKEY:{groupId}:{groupName}:{Convert.ToBase64String(groupKey)}";
        return SignString(payload, signingSecretKey);
    }

    public static bool VerifyGroupKey(string groupId, string groupName, byte[] groupKey, byte[] signature, byte[] signingKey)
    {
        var payload = $"GROUPKEY:{groupId}:{groupName}:{Convert.ToBase64String(groupKey)}";
        return VerifyString(payload, signature, signingKey);
    }

    // --- Passphrase strength ---

    public static int EstimatePassphraseStrength(string passphrase)
    {
        int score = 0;
        int len = passphrase.Length;

        if (len >= 20) score += 50;
        else if (len >= 16) score += 40;
        else if (len >= 12) score += 30;
        else score += len * 2;

        bool hasLower = passphrase.Any(c => c >= 'a' && c <= 'z');
        bool hasUpper = passphrase.Any(c => c >= 'A' && c <= 'Z');
        bool hasDigit = passphrase.Any(c => c >= '0' && c <= '9');
        bool hasSpecial = passphrase.Any(c => !char.IsLetterOrDigit(c));
        int classCount = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSpecial ? 1 : 0);
        score += classCount * 10;

        int unique = new HashSet<char>(passphrase.ToLower()).Count;
        if (unique >= 10) score += 15;
        else if (unique >= 6) score += 5;
        if (unique < len * 0.4) score -= 15;

        var lower = passphrase.ToLower();
        string[] commonWords = { "password", "passphrase", "letmein", "welcome", "admin", "master", "dragon", "monkey", "shadow", "sunshine" };
        foreach (var w in commonWords)
            if (lower.Contains(w)) { score -= 25; break; }

        if (System.Text.RegularExpressions.Regex.IsMatch(passphrase, @"(.)\1{3,}")) score -= 20;
        if (System.Text.RegularExpressions.Regex.IsMatch(passphrase, @"1234|2345|3456|abcd|bcde|qwer|asdf", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) score -= 20;
        if (System.Text.RegularExpressions.Regex.IsMatch(passphrase, @"^[a-zA-Z]+$")) score -= 15;
        if (System.Text.RegularExpressions.Regex.IsMatch(passphrase, @"^[0-9]+$")) score -= 30;
        if (System.Text.RegularExpressions.Regex.IsMatch(passphrase, @"qwerty|asdfgh|zxcvbn", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) score -= 25;

        return Math.Clamp(score, 0, 100);
    }
}
