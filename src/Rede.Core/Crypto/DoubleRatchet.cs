using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Sodium;

namespace Rede.Core.Crypto;

/// <summary>
/// Double Ratchet — Per-message PFS for 1:1 conversations.
/// Mirrors: ratchetInit*, ratchetEncrypt, ratchetDecrypt in crypto.js
/// Key material is stored as byte[] in state to allow zeroing.
/// </summary>
public class DoubleRatchet
{
    public const int MaxSkip = 256;
    public const int MaxMkSkipped = 1000;
    // C3: Prevent int32 counter wraparound — force ratchet reset before overflow
    public const int MaxMessageNumber = 1_000_000_000;

    /// <summary>
    /// Ratchet state — serializable for profile persistence.
    /// Wire format preserves the JS client's base64-string layout via Base64BytesJsonConverter.
    /// </summary>
    public class RatchetState
    {
        [JsonPropertyName("DHs")]
        public KeyPairBytes? DHs { get; set; }

        [JsonPropertyName("DHr")]
        [JsonConverter(typeof(Base64BytesJsonConverter))]
        public byte[]? DHr { get; set; }

        [JsonPropertyName("RK")]
        [JsonConverter(typeof(Base64BytesJsonConverter))]
        public byte[]? RK { get; set; }

        [JsonPropertyName("CKs")]
        [JsonConverter(typeof(Base64BytesJsonConverter))]
        public byte[]? CKs { get; set; }

        [JsonPropertyName("CKr")]
        [JsonConverter(typeof(Base64BytesJsonConverter))]
        public byte[]? CKr { get; set; }

        [JsonPropertyName("Ns")] public int Ns { get; set; }
        [JsonPropertyName("Nr")] public int Nr { get; set; }
        [JsonPropertyName("PN")] public int PN { get; set; }

        /// <summary>
        /// Skipped message keys. Key is "{dhPubB64}:{n}" (string for JSON dict compat),
        /// value is the raw 32-byte message key (stored as base64 via converter).
        /// </summary>
        [JsonPropertyName("MKSKIPPED")]
        [JsonConverter(typeof(MkSkippedConverter))]
        public Dictionary<string, byte[]> MKSKIPPED { get; set; } = new();

        /// <summary>Deep clone for backup/restore on failed decrypt.</summary>
        public RatchetState DeepClone()
        {
            var clonedSkipped = new Dictionary<string, byte[]>(MKSKIPPED.Count);
            foreach (var kv in MKSKIPPED) clonedSkipped[kv.Key] = (byte[])kv.Value.Clone();
            return new RatchetState
            {
                DHs = DHs is null ? null : new KeyPairBytes((byte[])DHs.PublicKey.Clone(), (byte[])DHs.SecretKey.Clone()),
                DHr = DHr is null ? null : (byte[])DHr.Clone(),
                RK = RK is null ? null : (byte[])RK.Clone(),
                CKs = CKs is null ? null : (byte[])CKs.Clone(),
                CKr = CKr is null ? null : (byte[])CKr.Clone(),
                Ns = Ns,
                Nr = Nr,
                PN = PN,
                MKSKIPPED = clonedSkipped,
            };
        }
    }

    /// <summary>
    /// Public/secret key pair for the DH ratchet. Stored as byte[] for zeroability.
    /// Serializes as JSON object with base64-encoded publicKey/secretKey for wire compat.
    /// </summary>
    public class KeyPairBytes
    {
        [JsonPropertyName("publicKey")]
        [JsonConverter(typeof(Base64BytesJsonConverter))]
        public byte[] PublicKey { get; set; }

        [JsonPropertyName("secretKey")]
        [JsonConverter(typeof(Base64BytesJsonConverter))]
        public byte[] SecretKey { get; set; }

        public KeyPairBytes() { PublicKey = Array.Empty<byte>(); SecretKey = Array.Empty<byte>(); }
        public KeyPairBytes(byte[] publicKey, byte[] secretKey) { PublicKey = publicKey; SecretKey = secretKey; }
    }

    /// <summary>RatchetHeader is wire format — Dh stays base64 string.</summary>
    public record RatchetHeader(string Dh, int Pn, int N);

    public record EncryptResult(RatchetHeader Header, string Ciphertext, string Nonce);

    // --- KDF functions ---

    private static (byte[] NewRK, byte[] ChainKey) KdfRK(byte[] rk, byte[] dhOut)
    {
        var derived = Hkdf.DeriveKey(dhOut, rk, "RedeRatchet", 64);
        var newRK = derived[..32];
        var chainKey = derived[32..64];
        CryptoService.ZeroOut(derived);
        return (newRK, chainKey);
    }

    public static (byte[] NewCK, byte[] MsgKey) KdfCK(byte[] ck)
    {
        using var hmac1 = new HMACSHA256(ck);
        var newCK = hmac1.ComputeHash(new byte[] { 0x02 });
        using var hmac2 = new HMACSHA256(ck);
        var msgKey = hmac2.ComputeHash(new byte[] { 0x01 });
        return (newCK, msgKey);
    }

    // --- Init ---

    /// <summary>Initialize ratchet as sender (after X3DH initiator). sharedSecret is zeroed.</summary>
    public static RatchetState InitSender(byte[] sharedSecret, byte[] recipientDHPub)
    {
        var dhKP = PublicKeyBox.GenerateKeyPair();
        var rkCopy = (byte[])sharedSecret.Clone();
        CryptoService.ZeroOut(sharedSecret);

        var pk = (byte[])dhKP.PublicKey.Clone();
        var sk = (byte[])dhKP.PrivateKey.Clone();
        CryptoService.ZeroOut(dhKP.PrivateKey);

        return new RatchetState
        {
            DHs = new KeyPairBytes(pk, sk),
            DHr = (byte[])recipientDHPub.Clone(),
            RK = rkCopy,
            CKs = null,
            CKr = null,
            Ns = 0,
            Nr = 0,
            PN = 0,
            MKSKIPPED = new(),
        };
    }

    /// <summary>Initialize ratchet as receiver (after X3DH responder). sharedSecret is consumed.</summary>
    public static RatchetState InitReceiver(byte[] sharedSecret, KeyPairBytes ourDHKeyPair)
    {
        var rkCopy = (byte[])sharedSecret.Clone();
        CryptoService.ZeroOut(sharedSecret);
        return new RatchetState
        {
            DHs = ourDHKeyPair,
            DHr = null,
            RK = rkCopy,
            CKs = null,
            CKr = null,
            Ns = 0,
            Nr = 0,
            PN = 0,
            MKSKIPPED = new(),
        };
    }

    // --- Encrypt ---

    public static EncryptResult Encrypt(RatchetState state, string plaintext)
    {
        // First send after DH ratchet step: derive sending chain
        if (state.CKs is null && state.DHr is not null)
        {
            byte[]? dhOut = null, newRK = null, cks = null;
            try
            {
                dhOut = CryptoService.Dh(state.DHs!.SecretKey, state.DHr);
                (newRK, cks) = KdfRK(state.RK!, dhOut);
                CryptoService.ZeroOut(state.RK);
                state.RK = newRK;
                state.CKs = cks;
                // Ownership transferred — prevent finally from zeroing these
                newRK = null;
                cks = null;
            }
            finally
            {
                if (dhOut is not null) CryptoService.ZeroOut(dhOut);
                if (newRK is not null) CryptoService.ZeroOut(newRK);
                if (cks is not null) CryptoService.ZeroOut(cks);
            }
        }

        if (state.CKs is null)
            throw new InvalidOperationException("Sending chain not initialized - wait for first incoming message");

        if (state.Ns >= MaxMessageNumber)
            throw new InvalidOperationException("Message counter limit reached - session must be re-established.");

        var (newCK, msgKey) = KdfCK(state.CKs);
        CryptoService.ZeroOut(state.CKs);
        state.CKs = newCK;

        var nonce = SodiumCore.GetRandomBytes(24);
        var paddedBytes = MessagePadding.Pad(plaintext);
        var ciphertext = SecretBox.Create(paddedBytes, nonce, msgKey);
        CryptoService.ZeroOut(msgKey);
        CryptoService.ZeroOut(paddedBytes);

        var header = new RatchetHeader(Convert.ToBase64String(state.DHs!.PublicKey), state.PN, state.Ns);
        state.Ns++;

        return new EncryptResult(header, Convert.ToBase64String(ciphertext), Convert.ToBase64String(nonce));
    }

    // --- Decrypt ---

    public static string? Decrypt(RatchetState state, RatchetHeader header, string ciphertextB64, string nonceB64)
    {
        var backup = state.DeepClone();
        try
        {
            var ciphertext = Convert.FromBase64String(ciphertextB64);
            var nonce = Convert.FromBase64String(nonceB64);

            if (nonce.Length != 24) return null;
            if (ciphertext.Length < 16) return null;

            // Check skipped message keys first
            var skippedKey = $"{header.Dh}:{header.N}";
            if (state.MKSKIPPED.TryGetValue(skippedKey, out var skippedMk))
            {
                state.MKSKIPPED.Remove(skippedKey);
                try
                {
                    var decryptedSkipped = SecretBox.Open(ciphertext, nonce, skippedMk);
                    CryptoService.ZeroOut(skippedMk);
                    var result = MessagePadding.Unpad(decryptedSkipped);
                    return result ?? System.Text.Encoding.UTF8.GetString(decryptedSkipped);
                }
                catch
                {
                    CryptoService.ZeroOut(skippedMk);
                    RestoreState(state, backup);
                    return null;
                }
            }

            // DH ratchet step if new DH key
            var headerDhBytes = Convert.FromBase64String(header.Dh);
            bool isNewDh = state.DHr is null || !CryptographicOperations.FixedTimeEquals(state.DHr, headerDhBytes);
            if (isNewDh)
            {
                if (state.CKr is not null)
                    SkipMessageKeys(state, header.Pn);
                DhRatchetStep(state, headerDhBytes);
            }

            SkipMessageKeys(state, header.N);

            var (newCK, msgKey) = KdfCK(state.CKr!);
            CryptoService.ZeroOut(state.CKr);
            state.CKr = newCK;

            if (state.Nr >= MaxMessageNumber)
                throw new InvalidOperationException("Receive counter limit reached.");
            state.Nr++;

            var decrypted = SecretBox.Open(ciphertext, nonce, msgKey);
            CryptoService.ZeroOut(msgKey);

            var text = MessagePadding.Unpad(decrypted);
            ZeroBackup(backup);
            return text ?? System.Text.Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            RestoreState(state, backup);
            return null;
        }
    }

    private static void ZeroBackup(RatchetState backup)
    {
        if (backup.RK is not null) CryptoService.ZeroOut(backup.RK);
        if (backup.CKs is not null) CryptoService.ZeroOut(backup.CKs);
        if (backup.CKr is not null) CryptoService.ZeroOut(backup.CKr);
        if (backup.DHs?.SecretKey is not null) CryptoService.ZeroOut(backup.DHs.SecretKey);
        foreach (var mk in backup.MKSKIPPED.Values)
            CryptoService.ZeroOut(mk);
        backup.MKSKIPPED.Clear();
    }

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

    private static void SkipMessageKeys(RatchetState state, int until)
    {
        if (state.CKr is null) return;
        if (until < 0 || until > MaxMessageNumber)
            throw new InvalidOperationException("Invalid message number");
        if (until - state.Nr > MaxSkip)
            throw new InvalidOperationException("Too many skipped messages");

        var dhKeyStr = state.DHr is null ? "" : Convert.ToBase64String(state.DHr);
        while (state.Nr < until)
        {
            var (newCK, msgKey) = KdfCK(state.CKr);
            CryptoService.ZeroOut(state.CKr);
            state.CKr = newCK;
            state.MKSKIPPED[$"{dhKeyStr}:{state.Nr}"] = msgKey;
            state.Nr++;
        }

        if (state.MKSKIPPED.Count > MaxMkSkipped)
        {
            var toRemove = state.MKSKIPPED.Keys.Take(state.MKSKIPPED.Count - MaxMkSkipped).ToList();
            foreach (var k in toRemove)
            {
                if (state.MKSKIPPED.TryGetValue(k, out var mk))
                    CryptoService.ZeroOut(mk);
                state.MKSKIPPED.Remove(k);
            }
        }
    }

    private static void DhRatchetStep(RatchetState state, byte[] headerDhBytes)
    {
        state.PN = state.Ns;
        state.Ns = 0;
        state.Nr = 0;

        if (headerDhBytes.Length != 32)
            throw new InvalidOperationException("Invalid DH public key length");
        if (!CryptoService.IsValidDhPublicKey(headerDhBytes))
            throw new InvalidOperationException("DH public key is a low-order point");

        state.DHr = headerDhBytes;

        byte[]? dhOut1 = null, rk1 = null, ckr = null;
        byte[]? dhOut2 = null, rk2 = null, cks = null;
        KeyPair? newDH = null;
        try
        {
            // Receiving chain
            dhOut1 = CryptoService.Dh(state.DHs!.SecretKey, headerDhBytes);
            (rk1, ckr) = KdfRK(state.RK!, dhOut1);
            CryptoService.ZeroOut(state.RK);
            state.RK = rk1;
            if (state.CKr is not null) CryptoService.ZeroOut(state.CKr);
            state.CKr = ckr;
            rk1 = null; ckr = null;

            // New DH keypair for sending
            newDH = PublicKeyBox.GenerateKeyPair();
            CryptoService.ZeroOut(state.DHs.SecretKey);
            state.DHs = new KeyPairBytes((byte[])newDH.PublicKey.Clone(), (byte[])newDH.PrivateKey.Clone());

            // Sending chain
            dhOut2 = CryptoService.Dh(state.DHs.SecretKey, headerDhBytes);
            (rk2, cks) = KdfRK(state.RK, dhOut2);
            CryptoService.ZeroOut(state.RK);
            state.RK = rk2;
            if (state.CKs is not null) CryptoService.ZeroOut(state.CKs);
            state.CKs = cks;
            rk2 = null; cks = null;
        }
        finally
        {
            if (dhOut1 is not null) CryptoService.ZeroOut(dhOut1);
            if (dhOut2 is not null) CryptoService.ZeroOut(dhOut2);
            if (rk1 is not null) CryptoService.ZeroOut(rk1);
            if (ckr is not null) CryptoService.ZeroOut(ckr);
            if (rk2 is not null) CryptoService.ZeroOut(rk2);
            if (cks is not null) CryptoService.ZeroOut(cks);
            if (newDH is not null) CryptoService.ZeroOut(newDH.PrivateKey);
        }
    }
}

/// <summary>
/// JSON converter for MKSKIPPED: Dictionary&lt;string, byte[]&gt; as { "key": "base64", ... }.
/// Wire-compatible with the JS client which stores these as base64 strings.
/// </summary>
public sealed class MkSkippedConverter : System.Text.Json.Serialization.JsonConverter<Dictionary<string, byte[]>>
{
    public override Dictionary<string, byte[]> Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        var result = new Dictionary<string, byte[]>();
        if (reader.TokenType == System.Text.Json.JsonTokenType.Null) return result;
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartObject)
            throw new System.Text.Json.JsonException("Expected object for MKSKIPPED");
        while (reader.Read())
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.EndObject) return result;
            if (reader.TokenType != System.Text.Json.JsonTokenType.PropertyName)
                throw new System.Text.Json.JsonException("Expected property name");
            var key = reader.GetString() ?? "";
            reader.Read();
            if (reader.TokenType == System.Text.Json.JsonTokenType.Null) { result[key] = Array.Empty<byte>(); continue; }
            if (reader.TokenType != System.Text.Json.JsonTokenType.String)
                throw new System.Text.Json.JsonException("Expected base64 string value");
            var s = reader.GetString() ?? "";
            try { result[key] = string.IsNullOrEmpty(s) ? Array.Empty<byte>() : Convert.FromBase64String(s); }
            catch (FormatException) { throw new System.Text.Json.JsonException("Invalid base64 in MKSKIPPED"); }
        }
        throw new System.Text.Json.JsonException("Unexpected end of MKSKIPPED object");
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, Dictionary<string, byte[]> value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var kv in value)
        {
            writer.WriteString(kv.Key, Convert.ToBase64String(kv.Value));
        }
        writer.WriteEndObject();
    }
}
