using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Rede.Core.Crypto;

/// <summary>
/// Fixed-size bucket message padding to prevent traffic analysis.
/// Mirrors: padMessage, unpadMessage in crypto.js
/// Buckets: 256, 1024, 4096, 16384 bytes
/// Format: 2-byte big-endian length prefix + content + random fill
/// </summary>
public static class MessagePadding
{
    private static readonly int[] PadBuckets = { 256, 1024, 4096, 16384 };

    public static byte[] Pad(string plaintext)
    {
        var msgBytes = Encoding.UTF8.GetBytes(plaintext);
        return Pad(msgBytes);
    }

    public static byte[] Pad(byte[] msgBytes)
    {
        // M5: Validate message size — ushort length prefix caps at 65535,
        // but largest bucket is 16384 so max content is 16382 (16384 - 2 byte prefix)
        if (msgBytes.Length > 16382)
            throw new ArgumentException($"Message too large ({msgBytes.Length} bytes, max 16382).");

        int needed = 2 + msgBytes.Length; // 2-byte length prefix + content
        int bucket = PadBuckets[^1];
        foreach (var b in PadBuckets)
        {
            if (needed <= b) { bucket = b; break; }
        }

        var padded = new byte[bucket];
        BinaryPrimitives.WriteUInt16BigEndian(padded.AsSpan(0, 2), (ushort)msgBytes.Length);
        Buffer.BlockCopy(msgBytes, 0, padded, 2, msgBytes.Length);

        // Fill remainder with random bytes
        if (bucket - needed > 0)
        {
            RandomNumberGenerator.Fill(padded.AsSpan(needed));
        }

        return padded;
    }

    public static string? Unpad(byte[] paddedBytes)
    {
        if (paddedBytes.Length < 2) return null;
        int len = BinaryPrimitives.ReadUInt16BigEndian(paddedBytes.AsSpan(0, 2));
        if (len > paddedBytes.Length - 2) return null;
        return Encoding.UTF8.GetString(paddedBytes, 2, len);
    }
}
