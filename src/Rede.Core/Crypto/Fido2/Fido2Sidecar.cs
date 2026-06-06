using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rede.Core.Crypto.Fido2;

/// <summary>
/// One enrolled FIDO2 hardware key. The wrapped Profile Master Secret (PMS) is
/// unwrapped by deriving a key from the authenticator's hmac-secret output for
/// (this credential, the sidecar's shared <see cref="Fido2Sidecar.HmacSalt"/>).
/// Nothing here is secret — the file can sit unencrypted next to the profile because
/// it only holds random salts, the (non-secret) credential id, and AEAD ciphertext.
/// </summary>
public sealed record Fido2KeyEntry
{
    public string CredentialId { get; init; } = "";   // base64, raw credential id from the authenticator
    public string Nonce { get; init; } = "";           // base64, 24 bytes — secretbox nonce
    public string WrappedPms { get; init; } = "";      // base64 — secretbox(PMS, nonce, wrapKey)
    public string Name { get; init; } = "Security key"; // user-facing label
    public long AddedAt { get; init; }
}

/// <summary>
/// Recovery-code fallback. Unwraps the same PMS via scrypt(recoveryCode, salt).
/// </summary>
public sealed record Fido2RecoveryEntry
{
    public string ScryptSalt { get; init; } = "";      // base64, 16 bytes
    public string Nonce { get; init; } = "";           // base64, 24 bytes
    public string WrappedPms { get; init; } = "";      // base64 — secretbox(PMS, nonce, wrapKey)
    public int ScryptN { get; init; } = ProfileEncryption.ScryptNCurrent;
    public long AddedAt { get; init; }
}

/// <summary>
/// Unencrypted unlock sidecar stored at <c>~/.rede/{sha256(userId)}.unlock.json</c>.
/// Present only when the user has enrolled at least one FIDO2 key — its existence is
/// what flips a profile into "hardware-key required" mode.
/// </summary>
public sealed class Fido2Sidecar
{
    public int Version { get; set; } = 1;
    /// <summary>Relying-party id used for all credentials in this sidecar (local, not a real domain).</summary>
    public string RpId { get; set; } = "rede.local";
    /// <summary>base64, 32 bytes — single hmac-secret salt shared by all keys so unlock is one tap.</summary>
    public string HmacSalt { get; set; } = "";
    public List<Fido2KeyEntry> Keys { get; set; } = new();
    public Fido2RecoveryEntry? Recovery { get; set; }
}

/// <summary>
/// File IO for the unlock sidecar. Keyed by the same sha256(userId) hex used for the
/// <c>.enc</c> profile filename so the login flow can probe it before decrypting.
/// </summary>
public static class Fido2SidecarStore
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rede");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string HashForUserId(string userId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant();

    private static bool IsHexLower(string s)
    {
        if (s.Length != 64) return false;
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    public static string PathForHash(string hashHex)
    {
        if (!IsHexLower(hashHex)) throw new ArgumentException("Invalid profile hash", nameof(hashHex));
        return Path.Combine(DataDir, $"{hashHex}.unlock.json");
    }

    /// <summary>True when a hardware key is enrolled for this profile (≥1 key entry).</summary>
    public static bool HasFidoEnrolled(string hashHex)
    {
        var sc = Load(hashHex);
        return sc is not null && sc.Keys.Count > 0;
    }

    public static Fido2Sidecar? Load(string hashHex)
    {
        try
        {
            var p = PathForHash(hashHex);
            if (!File.Exists(p)) return null;
            var json = File.ReadAllText(p);
            return JsonSerializer.Deserialize<Fido2Sidecar>(json, JsonOpts);
        }
        catch { return null; }
    }

    public static void Save(string hashHex, Fido2Sidecar sidecar)
    {
        if (!Directory.Exists(DataDir))
        {
            Directory.CreateDirectory(DataDir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(DataDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var p = PathForHash(hashHex);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sidecar, JsonOpts);
        var tmp = p + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, p, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { }
        }
    }

    public static void Delete(string hashHex)
    {
        try
        {
            var p = PathForHash(hashHex);
            if (File.Exists(p)) File.Delete(p);
        }
        catch { }
    }
}
