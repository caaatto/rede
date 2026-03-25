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

    /// <summary>
    /// Encrypt an inner payload so only the recipient can unseal it.
    /// Mirrors: sealMessage(innerPayloadJson, recipientIdentityPubKeyB64) in crypto.js
    /// </summary>
    public static SealedEnvelope Seal(string innerPayloadJson, string recipientIdentityPubKeyB64)
    {
        var ephKP = PublicKeyBox.GenerateKeyPair();
        var recipPub = Convert.FromBase64String(recipientIdentityPubKeyB64);
        var nonce = SodiumCore.GetRandomBytes(24);
        var plaintext = Encoding.UTF8.GetBytes(innerPayloadJson);
        var ciphertext = PublicKeyBox.Create(plaintext, nonce, ephKP.PrivateKey, recipPub);
        CryptoService.ZeroOut(ephKP.PrivateKey);

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
            var json = Encoding.UTF8.GetString(decrypted);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return null;
        }
    }
}
