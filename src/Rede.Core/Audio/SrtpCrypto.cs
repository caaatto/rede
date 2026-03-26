using System.Security.Cryptography;

namespace Rede.Core.Audio;

/// <summary>
/// RFC 3711 SRTP encrypt/decrypt — AES-128-CM + HMAC-SHA1-80.
/// Keys exchanged via Double Ratchet session (server never sees them).
/// </summary>
public static class SrtpCrypto
{
    public const int MasterKeyLength = 16;    // AES-128
    public const int MasterSaltLength = 14;
    public const int AuthTagLength = 10;      // HMAC-SHA1 truncated to 80 bits
    private const int RtpHeaderMinLength = 12;

    // KDF labels per RFC 3711 §4.3.1
    private const byte LabelCipherKey = 0x00;
    private const byte LabelAuthKey = 0x01;
    private const byte LabelSaltKey = 0x02;

    /// <summary>
    /// Derive session keys from master key + master salt using SRTP KDF (AES-CM PRF).
    /// </summary>
    public static (byte[] cipherKey, byte[] authKey, byte[] sessionSalt) DeriveSessionKeys(
        byte[] masterKey, byte[] masterSalt)
    {
        if (masterKey.Length != MasterKeyLength)
            throw new ArgumentException($"Master key must be {MasterKeyLength} bytes");
        if (masterSalt.Length != MasterSaltLength)
            throw new ArgumentException($"Master salt must be {MasterSaltLength} bytes");

        var cipherKey = KdfDerive(masterKey, masterSalt, LabelCipherKey, 16);
        var authKey = KdfDerive(masterKey, masterSalt, LabelAuthKey, 20);
        var sessionSalt = KdfDerive(masterKey, masterSalt, LabelSaltKey, 14);
        return (cipherKey, authKey, sessionSalt);
    }

    /// <summary>
    /// Encrypt an RTP packet payload in-place (AES-128-CM) and append HMAC-SHA1-80 auth tag.
    /// Input: complete RTP packet (header + payload).
    /// Output: RTP header + encrypted payload + 10-byte auth tag.
    /// </summary>
    public static byte[] Protect(byte[] rtpPacket, byte[] cipherKey, byte[] authKey, byte[] sessionSalt, uint roc)
    {
        if (rtpPacket.Length < RtpHeaderMinLength)
            throw new ArgumentException("RTP packet too short");

        int headerLen = GetRtpHeaderLength(rtpPacket);
        int payloadLen = rtpPacket.Length - headerLen;

        // Extract SSRC and sequence number for IV
        uint ssrc = (uint)((rtpPacket[8] << 24) | (rtpPacket[9] << 16) | (rtpPacket[10] << 8) | rtpPacket[11]);
        ushort seq = (ushort)((rtpPacket[2] << 8) | rtpPacket[3]);

        // Build IV per RFC 3711 §4.1.1
        var iv = BuildIv(sessionSalt, ssrc, (ulong)((long)roc << 16 | seq));

        // Encrypt payload with AES-128-CM (CTR mode)
        var encrypted = AesCm(cipherKey, iv, rtpPacket, headerLen, payloadLen);

        // Build SRTP packet: header + encrypted payload + auth tag
        var srtpPacket = new byte[rtpPacket.Length + AuthTagLength];
        Buffer.BlockCopy(rtpPacket, 0, srtpPacket, 0, headerLen);
        Buffer.BlockCopy(encrypted, 0, srtpPacket, headerLen, payloadLen);

        // Compute auth tag over header + encrypted payload
        var tag = ComputeAuthTag(authKey, srtpPacket, 0, rtpPacket.Length);
        Buffer.BlockCopy(tag, 0, srtpPacket, rtpPacket.Length, AuthTagLength);

        return srtpPacket;
    }

    /// <summary>
    /// Decrypt an SRTP packet. Verifies auth tag, decrypts payload.
    /// Input: SRTP packet (header + encrypted payload + 10-byte auth tag).
    /// Output: RTP packet (header + plaintext payload), or null if auth fails.
    /// </summary>
    public static byte[]? Unprotect(byte[] srtpPacket, byte[] cipherKey, byte[] authKey, byte[] sessionSalt, uint roc)
    {
        if (srtpPacket.Length < RtpHeaderMinLength + AuthTagLength)
            return null;

        int authStart = srtpPacket.Length - AuthTagLength;

        // Verify auth tag
        var expectedTag = ComputeAuthTag(authKey, srtpPacket, 0, authStart);
        if (!CryptographicOperations.FixedTimeEquals(
            new ReadOnlySpan<byte>(srtpPacket, authStart, AuthTagLength),
            new ReadOnlySpan<byte>(expectedTag, 0, AuthTagLength)))
        {
            return null; // Authentication failed
        }

        int headerLen = GetRtpHeaderLength(srtpPacket);
        int payloadLen = authStart - headerLen;
        if (payloadLen < 0) return null;

        uint ssrc = (uint)((srtpPacket[8] << 24) | (srtpPacket[9] << 16) | (srtpPacket[10] << 8) | srtpPacket[11]);
        ushort seq = (ushort)((srtpPacket[2] << 8) | srtpPacket[3]);

        var iv = BuildIv(sessionSalt, ssrc, (ulong)((long)roc << 16 | seq));
        var decrypted = AesCm(cipherKey, iv, srtpPacket, headerLen, payloadLen);

        var rtpPacket = new byte[authStart];
        Buffer.BlockCopy(srtpPacket, 0, rtpPacket, 0, headerLen);
        Buffer.BlockCopy(decrypted, 0, rtpPacket, headerLen, payloadLen);

        return rtpPacket;
    }

    /// <summary>
    /// Generate random SRTP master key + salt.
    /// </summary>
    public static (byte[] masterKey, byte[] masterSalt) GenerateKeyMaterial()
    {
        var masterKey = RandomNumberGenerator.GetBytes(MasterKeyLength);
        var masterSalt = RandomNumberGenerator.GetBytes(MasterSaltLength);
        return (masterKey, masterSalt);
    }

    // --- Internal helpers ---

    private static byte[] BuildIv(byte[] sessionSalt, uint ssrc, ulong index)
    {
        var iv = new byte[16];
        // IV = (SSRC XOR salt) || packetIndex, left-padded to 128 bits
        // Per RFC 3711 §4.1.1: IV = (label || r) XOR (k_s || 0..)
        // Simplified: IV[4..7] = SSRC, IV[8..13] = index, then XOR with salt
        iv[4] = (byte)(ssrc >> 24);
        iv[5] = (byte)(ssrc >> 16);
        iv[6] = (byte)(ssrc >> 8);
        iv[7] = (byte)(ssrc);
        iv[8] = (byte)(index >> 40);
        iv[9] = (byte)(index >> 32);
        iv[10] = (byte)(index >> 24);
        iv[11] = (byte)(index >> 16);
        iv[12] = (byte)(index >> 8);
        iv[13] = (byte)(index);

        for (int i = 0; i < sessionSalt.Length; i++)
            iv[i + 2] ^= sessionSalt[i]; // Salt is 14 bytes, aligned to iv[2..15]

        return iv;
    }

    private static byte[] AesCm(byte[] key, byte[] iv, byte[] data, int offset, int length)
    {
        // AES-CM = AES in counter mode
        var result = new byte[length];
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        var counter = new byte[16];
        Buffer.BlockCopy(iv, 0, counter, 0, 16);
        var block = new byte[16];

        for (int i = 0; i < length; i += 16)
        {
            aes.EncryptEcb(counter, block, PaddingMode.None);
            int chunkLen = Math.Min(16, length - i);
            for (int j = 0; j < chunkLen; j++)
                result[i + j] = (byte)(data[offset + i + j] ^ block[j]);

            // Increment counter (big-endian)
            for (int k = 15; k >= 0; k--)
            {
                if (++counter[k] != 0) break;
            }
        }

        return result;
    }

    private static byte[] ComputeAuthTag(byte[] authKey, byte[] data, int offset, int length)
    {
        using var hmac = new HMACSHA1(authKey);
        var hash = hmac.ComputeHash(data, offset, length);
        var tag = new byte[AuthTagLength];
        Buffer.BlockCopy(hash, 0, tag, 0, AuthTagLength);
        return tag;
    }

    private static byte[] KdfDerive(byte[] masterKey, byte[] masterSalt, byte label, int derivedLength)
    {
        // SRTP KDF: key_i = AES_CM(masterKey, (label XOR salt) || 0...)
        var x = new byte[14];
        Buffer.BlockCopy(masterSalt, 0, x, 0, 14);
        x[7] ^= label;

        var iv = new byte[16];
        Buffer.BlockCopy(x, 0, iv, 2, 14);

        var zeroes = new byte[derivedLength];
        return AesCm(masterKey, iv, zeroes, 0, derivedLength);
    }

    private static int GetRtpHeaderLength(byte[] packet)
    {
        if (packet.Length < RtpHeaderMinLength) return RtpHeaderMinLength;
        int cc = packet[0] & 0x0F; // CSRC count
        int headerLen = RtpHeaderMinLength + cc * 4;

        // Check for extension header
        if ((packet[0] & 0x10) != 0 && packet.Length >= headerLen + 4)
        {
            int extLen = (packet[headerLen + 2] << 8) | packet[headerLen + 3];
            headerLen += 4 + extLen * 4;
        }

        return Math.Min(headerLen, packet.Length);
    }
}
