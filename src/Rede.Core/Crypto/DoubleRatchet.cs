using System.Security.Cryptography;
using Sodium;

namespace Rede.Core.Crypto;

/// <summary>
/// Double Ratchet — Per-message PFS for 1:1 conversations.
/// Mirrors: ratchetInit*, ratchetEncrypt, ratchetDecrypt in crypto.js
/// </summary>
public class DoubleRatchet
{
    public const int MaxSkip = 256;
    public const int MaxMkSkipped = 1000;

    /// <summary>
    /// Ratchet state — serializable for profile persistence.
    /// Field names match the JS version exactly for wire compat.
    /// </summary>
    public class RatchetState
    {
        public KeyPairB64? DHs { get; set; }  // Our DH keypair
        public string? DHr { get; set; }       // Their DH public key (base64)
        public string? RK { get; set; }        // Root key (base64)
        public string? CKs { get; set; }       // Sending chain key (base64)
        public string? CKr { get; set; }       // Receiving chain key (base64)
        public int Ns { get; set; }            // Sending message number
        public int Nr { get; set; }            // Receiving message number
        public int PN { get; set; }            // Previous sending chain length
        public Dictionary<string, string> MKSKIPPED { get; set; } = new(); // Skipped message keys

        /// <summary>Deep clone for backup/restore on failed decrypt (K2 fix).</summary>
        public RatchetState DeepClone()
        {
            return new RatchetState
            {
                DHs = DHs is null ? null : new KeyPairB64(DHs.PublicKey, DHs.SecretKey),
                DHr = DHr,
                RK = RK,
                CKs = CKs,
                CKr = CKr,
                Ns = Ns,
                Nr = Nr,
                PN = PN,
                MKSKIPPED = new Dictionary<string, string>(MKSKIPPED),
            };
        }
    }

    public record KeyPairB64(string PublicKey, string SecretKey);

    public record RatchetHeader(string Dh, int Pn, int N);

    public record EncryptResult(RatchetHeader Header, string Ciphertext, string Nonce);

    // --- KDF functions ---

    /// <summary>
    /// KDF for root chain: produces new root key + chain key.
    /// Mirrors: kdfRK(rk, dhOut) in crypto.js
    /// </summary>
    private static (byte[] NewRK, byte[] ChainKey) KdfRK(byte[] rk, byte[] dhOut)
    {
        var derived = Hkdf.DeriveKey(dhOut, rk, "RedeRatchet", 64);
        var newRK = derived[..32];
        var chainKey = derived[32..64];
        return (newRK, chainKey);
    }

    /// <summary>
    /// KDF for chain key: produces new chain key + message key.
    /// Mirrors: kdfCK(ck) in crypto.js
    /// </summary>
    public static (byte[] NewCK, byte[] MsgKey) KdfCK(byte[] ck)
    {
        using var hmac1 = new HMACSHA256(ck);
        var newCK = hmac1.ComputeHash(new byte[] { 0x02 });
        using var hmac2 = new HMACSHA256(ck);
        var msgKey = hmac2.ComputeHash(new byte[] { 0x01 });
        return (newCK, msgKey);
    }

    // --- Init ---

    /// <summary>
    /// Initialize ratchet as sender (after X3DH initiator).
    /// Mirrors: ratchetInitSender(sharedSecret, recipientDHPubB64) in crypto.js
    /// </summary>
    public static RatchetState InitSender(byte[] sharedSecret, string recipientDHPubB64)
    {
        var dhKP = PublicKeyBox.GenerateKeyPair();
        var rkB64 = Convert.ToBase64String(sharedSecret);
        CryptoService.ZeroOut(sharedSecret);

        return new RatchetState
        {
            DHs = new KeyPairB64(Convert.ToBase64String(dhKP.PublicKey), Convert.ToBase64String(dhKP.PrivateKey)),
            DHr = recipientDHPubB64,
            RK = rkB64,
            CKs = null,
            CKr = null,
            Ns = 0,
            Nr = 0,
            PN = 0,
            MKSKIPPED = new(),
        };
    }

    /// <summary>
    /// Initialize ratchet as receiver (after X3DH responder).
    /// Mirrors: ratchetInitReceiver(sharedSecret, ourDHKeyPair) in crypto.js
    /// </summary>
    public static RatchetState InitReceiver(byte[] sharedSecret, KeyPairB64 ourDHKeyPair)
    {
        return new RatchetState
        {
            DHs = ourDHKeyPair,
            DHr = null,
            RK = Convert.ToBase64String(sharedSecret),
            CKs = null,
            CKr = null,
            Ns = 0,
            Nr = 0,
            PN = 0,
            MKSKIPPED = new(),
        };
    }

    // --- Encrypt ---

    /// <summary>
    /// Encrypt a message with the Double Ratchet.
    /// Mirrors: ratchetEncrypt(state, plaintext) in crypto.js
    /// </summary>
    public static EncryptResult Encrypt(RatchetState state, string plaintext)
    {
        // If first message and we have DHr, perform DH ratchet step
        if (state.CKs is null && state.DHr is not null)
        {
            var dhSec = Convert.FromBase64String(state.DHs!.SecretKey);
            var dhPub = Convert.FromBase64String(state.DHr);
            var rk = Convert.FromBase64String(state.RK!);

            var dhOut = CryptoService.Dh(dhSec, dhPub);
            var (newRK, cks) = KdfRK(rk, dhOut);

            CryptoService.ZeroOut(dhOut);
            CryptoService.ZeroOut(rk);

            state.RK = Convert.ToBase64String(newRK);
            state.CKs = Convert.ToBase64String(cks);

            CryptoService.ZeroOut(newRK);
            CryptoService.ZeroOut(cks);
            CryptoService.ZeroOut(dhSec);
        }

        if (state.CKs is null)
            throw new InvalidOperationException("Sending chain not initialized — wait for first incoming message");

        var ck = Convert.FromBase64String(state.CKs);
        var (newCK, msgKey) = KdfCK(ck);
        CryptoService.ZeroOut(ck);
        state.CKs = Convert.ToBase64String(newCK);
        CryptoService.ZeroOut(newCK);

        var nonce = SodiumCore.GetRandomBytes(24);
        var paddedBytes = MessagePadding.Pad(plaintext);
        var ciphertext = SecretBox.Create(paddedBytes, nonce, msgKey);
        CryptoService.ZeroOut(msgKey);
        CryptoService.ZeroOut(paddedBytes);

        var header = new RatchetHeader(state.DHs!.PublicKey, state.PN, state.Ns);
        state.Ns++;

        return new EncryptResult(header, Convert.ToBase64String(ciphertext), Convert.ToBase64String(nonce));
    }

    // --- Decrypt ---

    /// <summary>
    /// Decrypt a message with the Double Ratchet.
    /// Mirrors: ratchetDecrypt(state, header, ciphertextB64, nonceB64) in crypto.js
    /// </summary>
    public static string? Decrypt(RatchetState state, RatchetHeader header, string ciphertextB64, string nonceB64)
    {
        // C1: Backup state before any mutation — restore on failure
        var backup = state.DeepClone();
        try
        {
            var ciphertext = Convert.FromBase64String(ciphertextB64);
            var nonce = Convert.FromBase64String(nonceB64);

            // Check skipped message keys first
            var skippedKey = $"{header.Dh}:{header.N}";
            if (state.MKSKIPPED.TryGetValue(skippedKey, out var skippedMkB64))
            {
                state.MKSKIPPED.Remove(skippedKey);
                var skippedMk = Convert.FromBase64String(skippedMkB64);
                try
                {
                    var decryptedSkipped = SecretBox.Open(ciphertext, nonce, skippedMk);
                    CryptoService.ZeroOut(skippedMk);
                    var result = MessagePadding.Unpad(decryptedSkipped);
                    return result ?? System.Text.Encoding.UTF8.GetString(decryptedSkipped);
                }
                catch
                {
                    // Restore skipped key on failed decrypt
                    CryptoService.ZeroOut(skippedMk);
                    RestoreState(state, backup);
                    return null;
                }
            }

            // DH ratchet step if new DH key
            if (header.Dh != state.DHr)
            {
                if (state.CKr is not null)
                    SkipMessageKeys(state, header.Pn);
                DhRatchetStep(state, header.Dh);
            }

            // Skip any missed messages in current chain
            SkipMessageKeys(state, header.N);

            // Derive message key
            var ck = Convert.FromBase64String(state.CKr!);
            var (newCK, msgKey) = KdfCK(ck);
            CryptoService.ZeroOut(ck);
            state.CKr = Convert.ToBase64String(newCK);
            CryptoService.ZeroOut(newCK);
            state.Nr++;

            var decrypted = SecretBox.Open(ciphertext, nonce, msgKey);
            CryptoService.ZeroOut(msgKey);

            var text = MessagePadding.Unpad(decrypted);
            return text ?? System.Text.Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            // C1: Rollback all state mutations on any failure
            RestoreState(state, backup);
            return null;
        }
    }

    /// <summary>Restore state from backup (C1: rollback on failed decrypt).</summary>
    private static void RestoreState(RatchetState target, RatchetState source)
    {
        target.DHs = source.DHs;
        target.DHr = source.DHr;
        target.RK = source.RK;
        target.CKs = source.CKs;
        target.CKr = source.CKr;
        target.Ns = source.Ns;
        target.Nr = source.Nr;
        target.PN = source.PN;
        target.MKSKIPPED = source.MKSKIPPED;
    }

    /// <summary>
    /// Skip missed messages in a chain, caching their keys.
    /// Mirrors: _skipMessageKeys(state, until) in crypto.js
    /// </summary>
    private static void SkipMessageKeys(RatchetState state, int until)
    {
        if (state.CKr is null) return;
        if (until - state.Nr > MaxSkip)
            throw new InvalidOperationException("Too many skipped messages");

        var dhKey = state.DHr ?? "";
        while (state.Nr < until)
        {
            var ck = Convert.FromBase64String(state.CKr);
            var (newCK, msgKey) = KdfCK(ck);
            CryptoService.ZeroOut(ck);
            state.CKr = Convert.ToBase64String(newCK);
            CryptoService.ZeroOut(newCK);
            state.MKSKIPPED[$"{dhKey}:{state.Nr}"] = Convert.ToBase64String(msgKey);
            CryptoService.ZeroOut(msgKey);
            state.Nr++;
        }

        // Evict oldest skipped keys if over limit
        if (state.MKSKIPPED.Count > MaxMkSkipped)
        {
            var toRemove = state.MKSKIPPED.Keys.Take(state.MKSKIPPED.Count - MaxMkSkipped).ToList();
            foreach (var k in toRemove)
                state.MKSKIPPED.Remove(k);
        }
    }

    /// <summary>
    /// Perform DH ratchet step (when receiving a new DH key).
    /// Mirrors: _dhRatchetStep(state, headerDH) in crypto.js
    /// </summary>
    private static void DhRatchetStep(RatchetState state, string headerDH)
    {
        state.PN = state.Ns;
        state.Ns = 0;
        state.Nr = 0;
        state.DHr = headerDH;

        var dhSec = Convert.FromBase64String(state.DHs!.SecretKey);
        var dhPub = Convert.FromBase64String(headerDH);
        var rk = Convert.FromBase64String(state.RK!);

        // Receiving chain
        var dhOut1 = CryptoService.Dh(dhSec, dhPub);
        var (rk1, ckr) = KdfRK(rk, dhOut1);
        CryptoService.ZeroOut(dhOut1);
        state.RK = Convert.ToBase64String(rk1);
        state.CKr = Convert.ToBase64String(ckr);
        CryptoService.ZeroOut(rk1);
        CryptoService.ZeroOut(ckr);

        // New DH keypair for sending
        var newDH = PublicKeyBox.GenerateKeyPair();
        state.DHs = new KeyPairB64(Convert.ToBase64String(newDH.PublicKey), Convert.ToBase64String(newDH.PrivateKey));

        // Sending chain
        var rk2raw = Convert.FromBase64String(state.RK);
        var dhOut2 = CryptoService.Dh(newDH.PrivateKey, dhPub);
        var (rk2, cks) = KdfRK(rk2raw, dhOut2);
        CryptoService.ZeroOut(dhOut2);
        CryptoService.ZeroOut(newDH.PrivateKey);
        state.RK = Convert.ToBase64String(rk2);
        state.CKs = Convert.ToBase64String(cks);
        CryptoService.ZeroOut(rk2);
        CryptoService.ZeroOut(cks);
        CryptoService.ZeroOut(dhSec);
        CryptoService.ZeroOut(rk);
    }
}
