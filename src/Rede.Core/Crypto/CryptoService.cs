using System.Security.Cryptography;
using System.Text;
using Sodium;

namespace Rede.Core.Crypto;

/// <summary>
/// Core cryptographic operations: key generation, signatures, fingerprints, base64.
/// Mirrors key generation and signature functions from crypto.js using libsodium.
/// </summary>
public static class CryptoService
{
    // --- Memory safety ---

    public static void ZeroOut(byte[] arr)
    {
        CryptographicOperations.ZeroMemory(arr);
    }

    // --- Key Generation ---

    /// <summary>
    /// Generate X25519 keypair for encryption. Mirrors: generateKeyPair() in crypto.js
    /// nacl.box.keyPair() -> Curve25519 key pair
    /// </summary>
    public static (string PublicKey, string SecretKey) GenerateKeyPair()
    {
        var kp = PublicKeyBox.GenerateKeyPair();
        var result = (
            PublicKey: Convert.ToBase64String(kp.PublicKey),
            SecretKey: Convert.ToBase64String(kp.PrivateKey)
        );
        ZeroOut(kp.PrivateKey);
        return result;
    }

    /// <summary>
    /// Generate Ed25519 keypair for signing. Mirrors: generateSigningKeyPair() in crypto.js
    /// nacl.sign.keyPair() -> Ed25519 key pair
    /// </summary>
    public static (string SigningKey, string SigningSecretKey) GenerateSigningKeyPair()
    {
        var kp = PublicKeyAuth.GenerateKeyPair();
        var result = (
            SigningKey: Convert.ToBase64String(kp.PublicKey),
            SigningSecretKey: Convert.ToBase64String(kp.PrivateKey)
        );
        ZeroOut(kp.PrivateKey);
        return result;
    }

    /// <summary>
    /// Generate random group key (32 bytes). Mirrors: generateGroupKey() in crypto.js
    /// </summary>
    public static string GenerateGroupKey()
    {
        var key = SodiumCore.GetRandomBytes(32);
        var result = Convert.ToBase64String(key);
        ZeroOut(key);
        return result;
    }

    // --- Signatures (Ed25519) ---

    /// <summary>
    /// Sign base64 data with Ed25519. Mirrors: sign(dataB64, signingSecretKeyB64)
    /// </summary>
    public static string Sign(string dataB64, string signingSecretKeyB64)
    {
        var data = Convert.FromBase64String(dataB64);
        var sk = Convert.FromBase64String(signingSecretKeyB64);
        var sig = PublicKeyAuth.SignDetached(data, sk);
        ZeroOut(sk);
        return Convert.ToBase64String(sig);
    }

    /// <summary>
    /// Sign UTF-8 string with Ed25519. Mirrors: signString(text, signingSecretKeyB64)
    /// </summary>
    public static string SignString(string text, string signingSecretKeyB64)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var sk = Convert.FromBase64String(signingSecretKeyB64);
        var sig = PublicKeyAuth.SignDetached(data, sk);
        ZeroOut(sk);
        return Convert.ToBase64String(sig);
    }

    /// <summary>
    /// Sign raw bytes with Ed25519. Mirrors: signBytes(data, signingSecretKeyB64)
    /// </summary>
    public static string SignBytes(byte[] data, string signingSecretKeyB64)
    {
        var sk = Convert.FromBase64String(signingSecretKeyB64);
        var sig = PublicKeyAuth.SignDetached(data, sk);
        ZeroOut(sk);
        return Convert.ToBase64String(sig);
    }

    /// <summary>
    /// Verify base64 data signature. Mirrors: verify(dataB64, signatureB64, signingKeyB64)
    /// </summary>
    public static bool Verify(string dataB64, string signatureB64, string signingKeyB64)
    {
        try
        {
            var data = Convert.FromBase64String(dataB64);
            var sig = Convert.FromBase64String(signatureB64);
            var pk = Convert.FromBase64String(signingKeyB64);
            return PublicKeyAuth.VerifyDetached(sig, data, pk);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verify raw bytes signature. Mirrors: verifyBytes(data, signatureB64, signingKeyB64)
    /// </summary>
    public static bool VerifyBytes(byte[] data, string signatureB64, string signingKeyB64)
    {
        try
        {
            var sig = Convert.FromBase64String(signatureB64);
            var pk = Convert.FromBase64String(signingKeyB64);
            return PublicKeyAuth.VerifyDetached(sig, data, pk);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generate fingerprint from public key. Mirrors: fingerprint(publicKeyB64)
    /// Returns 32-char hex in 4-char chunks separated by spaces (8 chunks = 32 hex chars).
    /// </summary>
    public static string Fingerprint(string publicKeyB64)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(publicKeyB64));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        // Take first 32 hex chars (16 bytes = 128 bits), split into 4-char chunks
        var chunks = new string[8];
        for (int i = 0; i < 8; i++)
        {
            chunks[i] = hex.Substring(i * 4, 4);
        }
        return string.Join(' ', chunks);
    }

    // --- Base64 helpers ---

    public static byte[] DecodeBase64(string b64) => Convert.FromBase64String(b64);
    public static string EncodeBase64(byte[] bytes) => Convert.ToBase64String(bytes);

    // --- NaCl box (public-key encryption) ---

    /// <summary>
    /// Encrypt for recipient. Mirrors: encryptFor(plaintext, recipientPubKeyB64, senderSecretKeyB64)
    /// </summary>
    public static (string Encrypted, string Nonce)? EncryptFor(string plaintext, string recipientPubKeyB64, string senderSecretKeyB64)
    {
        var nonce = SodiumCore.GetRandomBytes(24);
        var messageBytes = Encoding.UTF8.GetBytes(plaintext);
        var recipientPub = Convert.FromBase64String(recipientPubKeyB64);
        var senderSecret = Convert.FromBase64String(senderSecretKeyB64);
        var encrypted = PublicKeyBox.Create(messageBytes, nonce, senderSecret, recipientPub);
        ZeroOut(senderSecret);
        return (
            Encrypted: Convert.ToBase64String(encrypted),
            Nonce: Convert.ToBase64String(nonce)
        );
    }

    /// <summary>
    /// Decrypt from sender. Mirrors: decryptFrom(encryptedB64, nonceB64, senderPubKeyB64, recipientSecretKeyB64)
    /// </summary>
    public static string? DecryptFrom(string encryptedB64, string nonceB64, string senderPubKeyB64, string recipientSecretKeyB64)
    {
        try
        {
            var encrypted = Convert.FromBase64String(encryptedB64);
            var nonce = Convert.FromBase64String(nonceB64);
            var senderPub = Convert.FromBase64String(senderPubKeyB64);
            var recipientSecret = Convert.FromBase64String(recipientSecretKeyB64);
            var decrypted = PublicKeyBox.Open(encrypted, nonce, recipientSecret, senderPub);
            ZeroOut(recipientSecret);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    // --- NaCl secretbox (symmetric encryption) ---

    /// <summary>
    /// Encrypt with symmetric key. Mirrors: encryptGroup(plaintext, groupKeyB64)
    /// </summary>
    public static (string Encrypted, string Nonce)? EncryptSecretBox(string plaintext, string keyB64)
    {
        var nonce = SodiumCore.GetRandomBytes(24);
        var messageBytes = Encoding.UTF8.GetBytes(plaintext);
        var key = Convert.FromBase64String(keyB64);
        var encrypted = SecretBox.Create(messageBytes, nonce, key);
        ZeroOut(key);
        return (
            Encrypted: Convert.ToBase64String(encrypted),
            Nonce: Convert.ToBase64String(nonce)
        );
    }

    /// <summary>
    /// Decrypt with symmetric key. Mirrors: decryptGroup(encryptedB64, nonceB64, groupKeyB64)
    /// </summary>
    public static string? DecryptSecretBox(string encryptedB64, string nonceB64, string keyB64)
    {
        try
        {
            var encrypted = Convert.FromBase64String(encryptedB64);
            var nonce = Convert.FromBase64String(nonceB64);
            var key = Convert.FromBase64String(keyB64);
            var decrypted = SecretBox.Open(encrypted, nonce, key);
            ZeroOut(key);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    // --- Raw X25519 DH ---

    /// <summary>
    /// Known Curve25519 low-order points that produce all-zeros shared secrets.
    /// </summary>
    /// All 7 known Curve25519 small-subgroup / low-order points (M8).
    private static readonly byte[][] LowOrderPoints = {
        new byte[32], // 0 (order 1)
        new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, // 1 (order 1)
        new byte[] { 224, 235, 122, 124, 59, 65, 184, 174, 22, 86, 227, 250, 241, 159, 196, 128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, // order 8
        new byte[] { 95, 156, 149, 188, 163, 80, 140, 36, 177, 208, 177, 85, 156, 131, 239, 91, 4, 68, 92, 196, 88, 28, 142, 134, 216, 34, 78, 221, 208, 159, 17, 87 }, // order 8
        new byte[] { 236, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 127 }, // p-1 (order 2)
        new byte[] { 237, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 127 }, // p (equiv 0)
        new byte[] { 238, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 127 }, // p+1 (order 4)
    };

    /// <summary>
    /// H1: Validate that a DH public key is not a known low-order point.
    /// </summary>
    public static bool IsValidDhPublicKey(byte[] publicKey)
    {
        if (publicKey.Length != 32) return false;
        foreach (var lop in LowOrderPoints)
        {
            if (CryptographicOperations.FixedTimeEquals(publicKey, lop))
                return false;
        }
        // Also reject if DH output would be all zeros
        return true;
    }

    /// <summary>
    /// Raw X25519 scalar multiplication. Mirrors: dh(secretKey, publicKey) using nacl.scalarMult
    /// H1: Validates public key and checks for all-zeros output.
    /// </summary>
    public static byte[] Dh(byte[] secretKey, byte[] publicKey)
    {
        var result = Sodium.ScalarMult.Mult(secretKey, publicKey);
        // H1: Reject if DH output is all zeros (low-order point attack)
        if (result.All(b => b == 0))
            throw new System.Security.Cryptography.CryptographicException("DH produced all-zeros output — invalid public key.");
        return result;
    }

    // --- Derive public key from secret key ---

    /// <summary>
    /// Get X25519 public key from secret key. Mirrors: nacl.box.keyPair.fromSecretKey(sk).publicKey
    /// </summary>
    public static byte[] PublicKeyFromSecret(byte[] secretKey)
    {
        return Sodium.ScalarMult.Base(secretKey);
    }

    // --- Server signature verification ---

    /// <summary>
    /// Verify server signature on a message. Mirrors: verifyServerSignature(msg, serverSigningKeyB64)
    /// The message JSON has a serverSig field that is removed before verifying.
    /// </summary>
    /// <summary>
    /// Verify server signature using the raw JSON string.
    /// The server signs JSON.stringify(msg) WITHOUT the serverSig field,
    /// then appends serverSig. We must strip serverSig from the raw JSON
    /// to get the exact bytes the server signed — re-serializing from a
    /// parsed object won't preserve JS key order / number formatting.
    /// </summary>
    public static bool VerifyServerSignature(string rawJson, string serverSigningKeyB64)
    {
        if (string.IsNullOrEmpty(serverSigningKeyB64)) return false;

        try
        {
            // Parse to extract the signature value
            var msg = System.Text.Json.Nodes.JsonNode.Parse(rawJson)?.AsObject();
            if (msg is null) return false;

            var sigNode = msg["serverSig"];
            if (sigNode is null) return false;
            var sigB64 = sigNode.GetValue<string>();

            // Strip the serverSig field from the raw JSON string
            // The server builds: {...fields} then adds "serverSig":"..." at the end
            // We need to reconstruct the JSON without serverSig
            var body = StripJsonField(rawJson, "serverSig");

            var data = Encoding.UTF8.GetBytes(body);
            var sig = Convert.FromBase64String(sigB64);
            var pk = Convert.FromBase64String(serverSigningKeyB64);
            return PublicKeyAuth.VerifyDetached(sig, data, pk);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Remove a top-level field from a JSON string, preserving the exact
    /// formatting of all other fields. Handles both middle and last position.
    /// </summary>
    private static string StripJsonField(string json, string fieldName)
    {
        // H4: Handle escaped quotes in field values — use [^"\\]*(?:\\.[^"\\]*)* pattern
        var pattern = $@",\s*""{fieldName}""\s*:\s*""[^""\\]*(?:\\.[^""\\]*)*""";
        var result = System.Text.RegularExpressions.Regex.Replace(json, pattern, "");
        if (result == json)
        {
            // Try: "fieldName":"value", (field at start/middle)
            pattern = $@"""{fieldName}""\s*:\s*""[^""\\]*(?:\\.[^""\\]*)*""\s*,";
            result = System.Text.RegularExpressions.Regex.Replace(json, pattern, "");
        }
        return result;
    }

    // --- Group key signing (legacy) ---

    public static string SignGroupKey(string groupId, string groupName, string groupKey, string signingSecretKeyB64)
    {
        var payload = $"GROUPKEY:{groupId}:{groupName}:{groupKey}";
        return SignString(payload, signingSecretKeyB64);
    }

    public static bool VerifyGroupKey(string groupId, string groupName, string groupKey, string signature, string signingKeyB64)
    {
        var payload = $"GROUPKEY:{groupId}:{groupName}:{groupKey}";
        try
        {
            var data = Encoding.UTF8.GetBytes(payload);
            var sig = Convert.FromBase64String(signature);
            var pk = Convert.FromBase64String(signingKeyB64);
            return PublicKeyAuth.VerifyDetached(sig, data, pk);
        }
        catch
        {
            return false;
        }
    }

    // --- Passphrase strength ---

    /// <summary>
    /// Estimate passphrase entropy/strength. Mirrors: estimatePassphraseStrength(passphrase)
    /// Returns 0-100 score.
    /// </summary>
    public static int EstimatePassphraseStrength(string passphrase)
    {
        int score = 0;
        int len = passphrase.Length;

        // Length scoring
        if (len >= 20) score += 50;
        else if (len >= 16) score += 40;
        else if (len >= 12) score += 30;
        else score += len * 2;

        // Character class diversity
        bool hasLower = passphrase.Any(c => c >= 'a' && c <= 'z');
        bool hasUpper = passphrase.Any(c => c >= 'A' && c <= 'Z');
        bool hasDigit = passphrase.Any(c => c >= '0' && c <= '9');
        bool hasSpecial = passphrase.Any(c => !char.IsLetterOrDigit(c));
        int classCount = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSpecial ? 1 : 0);
        score += classCount * 10;

        // Unique character ratio
        int unique = new HashSet<char>(passphrase.ToLower()).Count;
        if (unique >= 10) score += 15;
        else if (unique >= 6) score += 5;
        if (unique < len * 0.4) score -= 15;

        // Penalize common patterns
        var lower = passphrase.ToLower();
        string[] commonWords = { "password", "passphrase", "letmein", "welcome", "admin", "master", "dragon", "monkey", "shadow", "sunshine" };
        foreach (var w in commonWords)
        {
            if (lower.Contains(w)) { score -= 25; break; }
        }

        // Sequential patterns
        if (System.Text.RegularExpressions.Regex.IsMatch(passphrase, @"(.)\1{3,}")) score -= 20;
        if (System.Text.RegularExpressions.Regex.IsMatch(passphrase, @"1234|2345|3456|abcd|bcde|qwer|asdf", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) score -= 20;

        // All same class
        if (System.Text.RegularExpressions.Regex.IsMatch(passphrase, @"^[a-zA-Z]+$")) score -= 15;
        if (System.Text.RegularExpressions.Regex.IsMatch(passphrase, @"^[0-9]+$")) score -= 30;

        // Keyboard walk patterns
        if (System.Text.RegularExpressions.Regex.IsMatch(passphrase, @"qwerty|asdfgh|zxcvbn", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) score -= 25;

        return Math.Clamp(score, 0, 100);
    }
}
