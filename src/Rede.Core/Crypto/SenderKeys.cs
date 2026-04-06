using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Serialization;
using Sodium;

namespace Rede.Core.Crypto;

/// <summary>
/// Sender Keys — Per-sender symmetric ratchet for group PFS.
/// Mirrors: generateSenderKey, senderKeyEncrypt, senderKeyDecrypt in crypto.js
/// </summary>
public static class SenderKeys
{
    public const int MaxSkip = 1000;
    public const int MaxMessageNumber = 10000;

    public class SenderKeyState
    {
        [JsonPropertyName("chainKey")]
        [JsonConverter(typeof(Base64BytesJsonConverter))]
        public byte[] ChainKey { get; set; } = Array.Empty<byte>();

        [JsonPropertyName("messageNumber")]
        public int MessageNumber { get; set; }

        public SenderKeyState DeepClone() => new()
        {
            ChainKey = (byte[])ChainKey.Clone(),
            MessageNumber = MessageNumber,
        };
    }

    public record EncryptResult(string Ciphertext, string Nonce, int MessageNumber, string Signature);

    /// <summary>Generate a new sender key.</summary>
    public static SenderKeyState Generate()
    {
        return new SenderKeyState
        {
            ChainKey = SodiumCore.GetRandomBytes(32),
            MessageNumber = 0,
        };
    }

    /// <summary>
    /// Build the signature payload: ciphertext || uint32(messageNumber) || utf8(contextId).
    /// The contextId binds the signature to a specific group or place channel, preventing
    /// cross-group replay attacks.
    /// </summary>
    private static byte[] BuildSigData(byte[] ciphertext, int messageNumber, string? contextId)
    {
        var ctxBytes = contextId is not null ? Encoding.UTF8.GetBytes(contextId) : Array.Empty<byte>();
        var sigData = new byte[ciphertext.Length + 4 + ctxBytes.Length];
        Buffer.BlockCopy(ciphertext, 0, sigData, 0, ciphertext.Length);
        BinaryPrimitives.WriteUInt32BigEndian(sigData.AsSpan(ciphertext.Length), (uint)messageNumber);
        if (ctxBytes.Length > 0)
            Buffer.BlockCopy(ctxBytes, 0, sigData, ciphertext.Length + 4, ctxBytes.Length);
        return sigData;
    }

    /// <summary>Encrypt with sender key. contextId binds the signature to a group/channel.</summary>
    public static EncryptResult Encrypt(SenderKeyState state, string plaintext, byte[] signingSecretKey, string contextId)
    {
        if (state.MessageNumber >= MaxMessageNumber)
            throw new InvalidOperationException("Sender key message limit reached — rekey required.");

        var (newCK, msgKey) = DoubleRatchet.KdfCK(state.ChainKey);
        CryptoService.ZeroOut(state.ChainKey);
        state.ChainKey = newCK;

        var nonce = SodiumCore.GetRandomBytes(24);
        var paddedBytes = MessagePadding.Pad(plaintext);
        var ciphertext = SecretBox.Create(paddedBytes, nonce, msgKey);
        CryptoService.ZeroOut(msgKey);
        CryptoService.ZeroOut(paddedBytes);

        var sigData = BuildSigData(ciphertext, state.MessageNumber, contextId);
        var signature = CryptoService.SignBytesB64(sigData, signingSecretKey);

        var messageNumber = state.MessageNumber;
        state.MessageNumber++;

        return new EncryptResult(
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(nonce),
            messageNumber,
            signature
        );
    }

    /// <summary>Decrypt with sender key. contextId must match what was used during encryption.</summary>
    public static string? Decrypt(
        SenderKeyState state,
        string ciphertextB64,
        string nonceB64,
        int messageNumber,
        string signatureB64,
        byte[] signingKey,
        string contextId)
    {
        var backup = state.DeepClone();
        try
        {
            if (messageNumber < 0 || messageNumber >= MaxMessageNumber)
                return null;

            var ciphertext = Convert.FromBase64String(ciphertextB64);
            var nonce = Convert.FromBase64String(nonceB64);

            if (nonce.Length != 24) return null;
            if (ciphertext.Length < 16) return null;

            // Verify signature with contextId binding (new format).
            // Fall back to legacy (no contextId) for messages from pre-v2.17.3 clients.
            var sigData = BuildSigData(ciphertext, messageNumber, contextId);
            if (!CryptoService.VerifyBytes(sigData, signatureB64, signingKey))
            {
                var legacySigData = BuildSigData(ciphertext, messageNumber, null);
                if (!CryptoService.VerifyBytes(legacySigData, signatureB64, signingKey))
                    return null;
            }

            if (messageNumber < state.MessageNumber)
                return null;
            if (messageNumber - state.MessageNumber > MaxSkip)
                return null;

            var ck = (byte[])state.ChainKey.Clone();
            byte[]? msgKey = null;

            for (int i = state.MessageNumber; i <= messageNumber; i++)
            {
                var (newCK, mk) = DoubleRatchet.KdfCK(ck);
                CryptoService.ZeroOut(ck);
                ck = newCK;
                if (i == messageNumber) msgKey = mk;
                else CryptoService.ZeroOut(mk);
            }

            CryptoService.ZeroOut(state.ChainKey);
            state.ChainKey = ck;
            state.MessageNumber = messageNumber + 1;

            var decrypted = SecretBox.Open(ciphertext, nonce, msgKey!);
            CryptoService.ZeroOut(msgKey!);

            var result = MessagePadding.Unpad(decrypted);
            return result ?? System.Text.Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            // Rollback on failure
            CryptoService.ZeroOut(state.ChainKey);
            state.ChainKey = backup.ChainKey;
            state.MessageNumber = backup.MessageNumber;
            return null;
        }
    }
}
