using System.Text;

namespace Rede.Core.Crypto.Fido2;

/// <summary>
/// Minimal RFC 4648 base32 (no padding) used for human-typeable recovery codes.
/// Crockford-style input cleanup so users can ignore case and separators.
/// </summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Encode bytes to an unpadded uppercase base32 string (the canonical form).</summary>
    public static string Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 0x1f]);
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1f]);
        return sb.ToString();
    }

    /// <summary>Normalize user input back to the canonical form: uppercase, alphabet chars only.</summary>
    public static string Canonicalize(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input.ToUpperInvariant())
        {
            // Forgive common look-alikes for digits the alphabet doesn't use (0→O, 1→I).
            var c = ch switch { '0' => 'O', '1' => 'I', _ => ch };
            if (Alphabet.IndexOf(c) >= 0) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Group a canonical code into dash-separated blocks of 4 for display.</summary>
    public static string Group(string canonical)
    {
        var sb = new StringBuilder(canonical.Length + canonical.Length / 4);
        for (int i = 0; i < canonical.Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(canonical[i]);
        }
        return sb.ToString();
    }
}
