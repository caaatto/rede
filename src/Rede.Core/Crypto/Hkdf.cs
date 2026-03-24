using System.Security.Cryptography;

namespace Rede.Core.Crypto;

/// <summary>
/// HKDF-SHA256 (RFC 5869). Used by X3DH and Double Ratchet for key derivation.
/// Mirrors: hkdfExtract, hkdfExpand, hkdf in crypto.js
/// </summary>
public static class Hkdf
{
    public static byte[] Extract(byte[] salt, byte[] ikm)
    {
        using var hmac = new HMACSHA256(salt);
        return hmac.ComputeHash(ikm);
    }

    public static byte[] Expand(byte[] prk, byte[] info, int length)
    {
        var okm = new byte[length];
        var t = Array.Empty<byte>();
        int offset = 0;

        for (byte i = 1; offset < length; i++)
        {
            using var hmac = new HMACSHA256(prk);
            hmac.TransformBlock(t, 0, t.Length, null, 0);
            hmac.TransformBlock(info, 0, info.Length, null, 0);
            hmac.TransformFinalBlock(new[] { i }, 0, 1);
            t = hmac.Hash!;
            int toCopy = Math.Min(t.Length, length - offset);
            Buffer.BlockCopy(t, 0, okm, offset, toCopy);
            offset += toCopy;
        }

        return okm;
    }

    public static byte[] DeriveKey(byte[] ikm, byte[] salt, byte[] info, int length)
    {
        var prk = Extract(salt, ikm);
        return Expand(prk, info, length);
    }

    public static byte[] DeriveKey(byte[] ikm, byte[] salt, string info, int length)
    {
        return DeriveKey(ikm, salt, System.Text.Encoding.UTF8.GetBytes(info), length);
    }

    /// <summary>
    /// Build identity-bound HKDF salt for X3DH (sorted for deterministic ordering).
    /// Mirrors: x3dhIdentitySalt(pubA, pubB) in crypto.js
    /// </summary>
    public static byte[] X3dhIdentitySalt(byte[] pubA, byte[] pubB)
    {
        // Sort public keys lexicographically so both sides produce the same salt
        var cmp = pubA.AsSpan().SequenceCompareTo(pubB.AsSpan());
        byte[] sorted;
        if (cmp <= 0)
        {
            sorted = new byte[pubA.Length + pubB.Length];
            Buffer.BlockCopy(pubA, 0, sorted, 0, pubA.Length);
            Buffer.BlockCopy(pubB, 0, sorted, pubA.Length, pubB.Length);
        }
        else
        {
            sorted = new byte[pubB.Length + pubA.Length];
            Buffer.BlockCopy(pubB, 0, sorted, 0, pubB.Length);
            Buffer.BlockCopy(pubA, 0, sorted, pubB.Length, pubA.Length);
        }

        var prefix = System.Text.Encoding.UTF8.GetBytes("RedeX3DHSalt");
        var combined = new byte[prefix.Length + sorted.Length];
        Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
        Buffer.BlockCopy(sorted, 0, combined, prefix.Length, sorted.Length);

        return SHA256.HashData(combined);
    }
}
