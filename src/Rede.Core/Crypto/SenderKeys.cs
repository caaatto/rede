using System.Buffers.Binary;
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
        public string ChainKey { get; set; } = "";  // base64
        public int MessageNumber { get; set; }

        /// <summary>Deep clone for backup/restore on failed decrypt (C2 fix).</summary>
        public SenderKeyState DeepClone() => new()
        {
            ChainKey = ChainKey,
            MessageNumber = MessageNumber,
        };
    }

    public record EncryptResult(string Ciphertext, string Nonce, int MessageNumber, string Signature);

    /// <summary>
    /// Generate a new sender key. Mirrors: generateSenderKey() in crypto.js
    /// </summary>
    public static SenderKeyState Generate()
    {
        var chainKey = SodiumCore.GetRandomBytes(32);
        var result = new SenderKeyState
        {
            ChainKey = Convert.ToBase64String(chainKey),
            MessageNumber = 0,
        };
        CryptoService.ZeroOut(chainKey);
        return result;
    }

    /// <summary>
    /// Encrypt with sender key. Mirrors: senderKeyEncrypt(state, plaintext, signingSecretKeyB64)
    /// </summary>
    public static EncryptResult Encrypt(SenderKeyState state, string plaintext, string signingSecretKeyB64)
    {
        var ck = Convert.FromBase64String(state.ChainKey);
        var (newCK, msgKey) = DoubleRatchet.KdfCK(ck);
        CryptoService.ZeroOut(ck);
        state.ChainKey = Convert.ToBase64String(newCK);
        CryptoService.ZeroOut(newCK);

        var nonce = SodiumCore.GetRandomBytes(24);
        var paddedBytes = MessagePadding.Pad(plaintext);
        var ciphertext = SecretBox.Create(paddedBytes, nonce, msgKey);
        CryptoService.ZeroOut(msgKey);
        CryptoService.ZeroOut(paddedBytes);

        // Sign ciphertext + messageNumber for authentication
        var sigData = new byte[ciphertext.Length + 4];
        Buffer.BlockCopy(ciphertext, 0, sigData, 0, ciphertext.Length);
        BinaryPrimitives.WriteUInt32BigEndian(sigData.AsSpan(ciphertext.Length), (uint)state.MessageNumber);
        var signature = CryptoService.SignBytes(sigData, signingSecretKeyB64);

        var messageNumber = state.MessageNumber;
        // M5: Bounds check — trigger rekey before receivers reject
        if (messageNumber >= MaxMessageNumber)
            throw new InvalidOperationException("Sender key message limit reached — rekey required.");
        state.MessageNumber++;

        return new EncryptResult(
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(nonce),
            messageNumber,
            signature
        );
    }

    /// <summary>
    /// Decrypt with sender key. Mirrors: senderKeyDecrypt(state, ...) in crypto.js
    /// </summary>
    public static string? Decrypt(
        SenderKeyState state,
        string ciphertextB64,
        string nonceB64,
        int messageNumber,
        string signature,
        string signingKeyB64)
    {
        // C2: Backup state before mutation — restore on failure
        var backup = state.DeepClone();
        try
        {
            // Validate messageNumber range
            if (messageNumber < 0 || messageNumber > MaxMessageNumber)
                return null;

            var ciphertext = Convert.FromBase64String(ciphertextB64);
            var nonce = Convert.FromBase64String(nonceB64);

            // Verify signature
            var sigData = new byte[ciphertext.Length + 4];
            Buffer.BlockCopy(ciphertext, 0, sigData, 0, ciphertext.Length);
            BinaryPrimitives.WriteUInt32BigEndian(sigData.AsSpan(ciphertext.Length), (uint)messageNumber);
            if (!CryptoService.VerifyBytes(sigData, signature, signingKeyB64))
                return null; // Signature verification failed

            // Old message check
            if (messageNumber < state.MessageNumber)
                return null;

            // Prevent DoS via massive forward skip
            if (messageNumber - state.MessageNumber > MaxSkip)
                return null;

            var ck = Convert.FromBase64String(state.ChainKey);
            byte[]? msgKey = null;

            // Skip forward to the right message number
            for (int i = state.MessageNumber; i <= messageNumber; i++)
            {
                var (newCK, mk) = DoubleRatchet.KdfCK(ck);
                CryptoService.ZeroOut(ck);
                ck = newCK;
                if (i == messageNumber)
                    msgKey = mk;
                else
                    CryptoService.ZeroOut(mk);
            }

            state.ChainKey = Convert.ToBase64String(ck);
            CryptoService.ZeroOut(ck);
            state.MessageNumber = messageNumber + 1;

            var decrypted = SecretBox.Open(ciphertext, nonce, msgKey!);
            CryptoService.ZeroOut(msgKey!);

            var result = MessagePadding.Unpad(decrypted);
            return result ?? System.Text.Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            // C2: Rollback state on any failure
            state.ChainKey = backup.ChainKey;
            state.MessageNumber = backup.MessageNumber;
            return null;
        }
    }
}
