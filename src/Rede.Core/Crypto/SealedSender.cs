using System.Text;
using System.Text.Json;
using Sodium;

namespace Rede.Core.Crypto;

/// <summary>
/// Sealed Sender — Hide sender identity from server.
/// Mirrors: sealMessage, unsealMessage in crypto.js
/// Uses a one-time ephemeral key for the outer nacl.box envelope.
/// </summary>
public static class SealedSender
{
    public record SealedEnvelope(string EphemeralKey, string Nonce, string Ciphertext);

    // Domain separation tag to prevent cross-protocol reuse of ephemeral box payloads
    private static readonly byte[] DomainTag = "SEALED_SENDER_V1:"u8.ToArray();

    /// <summary>
    /// Encrypt an inner payload so only the recipient can unseal it.
    /// Mirrors: sealMessage(innerPayloadJson, recipientIdentityPubKeyB64) in crypto.js
    /// </summary>
    public static SealedEnvelope Seal(string innerPayloadJson, string recipientIdentityPubKeyB64)
    {
        var ephKP = PublicKeyBox.GenerateKeyPair();
        var recipPub = Convert.FromBase64String(recipientIdentityPubKeyB64);
        var nonce = SodiumCore.GetRandomBytes(24);
        var jsonBytes = Encoding.UTF8.GetBytes(innerPayloadJson);
        // Prepend domain tag for domain separation
        var plaintext = new byte[DomainTag.Length + jsonBytes.Length];
        Buffer.BlockCopy(DomainTag, 0, plaintext, 0, DomainTag.Length);
        Buffer.BlockCopy(jsonBytes, 0, plaintext, DomainTag.Length, jsonBytes.Length);
        var ciphertext = PublicKeyBox.Create(plaintext, nonce, ephKP.PrivateKey, recipPub);
        CryptoService.ZeroOut(ephKP.PrivateKey);
        CryptoService.ZeroOut(plaintext);

        return new SealedEnvelope(
            Convert.ToBase64String(ephKP.PublicKey),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext)
        );
    }

    /// <summary>
    /// Decrypt a sealed envelope using our identity secret key.
    /// Mirrors: unsealMessage(sealedEnvelope, recipientIdentitySecretKeyB64) in crypto.js
    /// </summary>
    public static JsonElement? Unseal(SealedEnvelope envelope, string recipientIdentitySecretKeyB64)
    {
        try
        {
            var ephPub = Convert.FromBase64String(envelope.EphemeralKey);
            // M6: Validate ephemeral key length
            if (ephPub.Length != 32) return null;
            var nonce = Convert.FromBase64String(envelope.Nonce);
            if (nonce.Length != 24) return null;
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            if (ciphertext.Length == 0) return null;
            var secretKey = Convert.FromBase64String(recipientIdentitySecretKeyB64);
            var decrypted = PublicKeyBox.Open(ciphertext, nonce, secretKey, ephPub);
            CryptoService.ZeroOut(secretKey); // M7: Zero secret key after use
            // Verify and strip domain separation tag
            if (decrypted.Length < DomainTag.Length) return null;
            for (int i = 0; i < DomainTag.Length; i++)
                if (decrypted[i] != DomainTag[i]) return null;
            var json = Encoding.UTF8.GetString(decrypted, DomainTag.Length, decrypted.Length - DomainTag.Length);
            CryptoService.ZeroOut(decrypted);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return null;
        }
    }
}
