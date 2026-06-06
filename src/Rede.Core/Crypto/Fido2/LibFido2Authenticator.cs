using System.Runtime.InteropServices;

namespace Rede.Core.Crypto.Fido2;

/// <summary>
/// <see cref="IFido2Authenticator"/> backed by Yubico's libfido2 via P/Invoke. libfido2 talks to
/// the key directly over hidraw on Linux and delegates to the Windows WebAuthn API on Windows, so
/// a single code path covers both. The native library is distributed on demand into
/// <c>~/.rede/libs/</c> (Ed25519 + SHA256-verified), mirroring the RNNoise installer; until then
/// <see cref="IsAvailable"/> is false and the UI offers to install it.
/// </summary>
public sealed class LibFido2Authenticator : IFido2Authenticator
{
    private const string Lib = "fido2";

    // COSE / extension / option constants from fido.h
    private const int COSE_ES256 = -7;
    private const int FIDO_EXT_HMAC_SECRET = 0x01;
    private const int FIDO_OPT_OMIT = 0;
    private const int FIDO_OPT_FALSE = 1;
    private const int FIDO_OPT_TRUE = 2;

    // Selected CTAP/libfido2 error codes from fido/err.h
    private const int FIDO_OK = 0x00;
    private const int FIDO_ERR_NO_CREDENTIALS = 0x2e;
    private const int FIDO_ERR_PIN_INVALID = 0x31;
    private const int FIDO_ERR_PIN_BLOCKED = 0x32;
    private const int FIDO_ERR_PIN_AUTH_BLOCKED = 0x34;
    private const int FIDO_ERR_PIN_REQUIRED = 0x36;
    private const int FIDO_ERR_ACTION_TIMEOUT = 0x3a;
    private const int FIDO_ERR_UP_REQUIRED = 0x3b;

    public static string LibsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rede", "libs");

    public static string LibFileName { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "fido2.dll" : "libfido2.so";

    public static bool IsAvailableStatic { get; private set; }

    public bool IsAvailable => IsAvailableStatic;

    public string DescribeBackend()
        => IsAvailableStatic ? "libfido2 loaded" : "libfido2 not found (install the libfido2 system package)";

    static LibFido2Authenticator()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibFido2Authenticator).Assembly, (name, asm, searchPath) =>
        {
            if (name != Lib) return IntPtr.Zero;
            IntPtr handle;
            // User-installed / bundled copy first.
            foreach (var path in new[]
                     {
                         Path.Combine(LibsDirectory, LibFileName),
                         Path.Combine(AppContext.BaseDirectory, LibFileName),
                     })
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle))
                    return handle;
            }
            // System library. The default name resolves to the unversioned soname
            // (libfido2.so / fido2.dll), which only ships with the -dev package on Linux;
            // the runtime package installs versioned sonames, so try those explicitly.
            string[] candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { "fido2.dll", "fido2" }
                : new[] { "libfido2.so", "libfido2.so.1", "libfido2.so.1.12.0" };
            foreach (var n in candidates)
                if (NativeLibrary.TryLoad(n, asm, searchPath, out handle))
                    return handle;
            if (NativeLibrary.TryLoad(name, asm, searchPath, out handle))
                return handle;
            return IntPtr.Zero;
        });

        Probe();
    }

    /// <summary>Re-probe after installing the native library at runtime.</summary>
    public static void TryReload()
    {
        if (IsAvailableStatic) return;
        Probe();
    }

    private static void Probe()
    {
        try
        {
            fido_init(0);
            IsAvailableStatic = true;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch { }
    }

    // --- IFido2Authenticator ---

    public bool HasDevice() => FirstDevicePath() is not null;

    public bool SupportsHmacSecret()
    {
        var path = FirstDevicePath();
        if (path is null) return false;
        IntPtr dev = IntPtr.Zero, ci = IntPtr.Zero;
        try
        {
            dev = OpenDevice(path);
            ci = fido_cbor_info_new();
            if (fido_dev_get_cbor_info(dev, ci) != FIDO_OK) return false;
            return ExtensionList(ci).Contains("hmac-secret");
        }
        catch { return false; }
        finally
        {
            if (ci != IntPtr.Zero) fido_cbor_info_free(ref ci);
            CloseDevice(ref dev);
        }
    }

    public Fido2Credential MakeCredential(string rpId, string userName, byte[] userHandle, string? pin)
    {
        var path = FirstDevicePath() ?? throw Err(Fido2ErrorKind.NoDevice, "No security key detected.");
        IntPtr dev = IntPtr.Zero, cred = IntPtr.Zero;
        try
        {
            dev = OpenDevice(path);
            cred = fido_cred_new();

            Check(fido_cred_set_type(cred, COSE_ES256), "set_type");
            Check(fido_cred_set_clientdata_hash(cred, ZeroHash, (nuint)ZeroHash.Length), "set_clientdata_hash");
            Check(fido_cred_set_rp(cred, rpId, "Rede"), "set_rp");
            Check(fido_cred_set_user(cred, userHandle, (nuint)userHandle.Length, userName, userName, null), "set_user");
            Check(fido_cred_set_extensions(cred, FIDO_EXT_HMAC_SECRET), "set_extensions");
            Check(fido_cred_set_rk(cred, FIDO_OPT_TRUE), "set_rk");

            var rc = fido_dev_make_cred(dev, cred, pin);
            if (rc != FIDO_OK) throw MapErr(rc, "make_cred");

            var credId = ReadPtr(fido_cred_id_ptr(cred), fido_cred_id_len(cred));
            var pubKey = ReadPtr(fido_cred_pubkey_ptr(cred), fido_cred_pubkey_len(cred));
            return new Fido2Credential(credId, pubKey);
        }
        finally
        {
            if (cred != IntPtr.Zero) fido_cred_free(ref cred);
            CloseDevice(ref dev);
        }
    }

    public Fido2HmacResult GetHmacSecret(string rpId, IReadOnlyList<byte[]> allowCredentialIds, byte[] salt, string? pin)
    {
        var path = FirstDevicePath() ?? throw Err(Fido2ErrorKind.NoDevice, "No security key detected.");
        IntPtr dev = IntPtr.Zero, assert = IntPtr.Zero;
        try
        {
            dev = OpenDevice(path);
            assert = fido_assert_new();

            Check(fido_assert_set_rp(assert, rpId), "assert_set_rp");
            Check(fido_assert_set_clientdata_hash(assert, ZeroHash, (nuint)ZeroHash.Length), "assert_set_clientdata_hash");
            foreach (var cid in allowCredentialIds)
                Check(fido_assert_allow_cred(assert, cid, (nuint)cid.Length), "assert_allow_cred");
            Check(fido_assert_set_extensions(assert, FIDO_EXT_HMAC_SECRET), "assert_set_extensions");
            Check(fido_assert_set_hmac_salt(assert, salt, (nuint)salt.Length), "assert_set_hmac_salt");

            var rc = fido_dev_get_assert(dev, assert, pin);
            if (rc != FIDO_OK) throw MapErr(rc, "get_assert");
            if (fido_assert_count(assert) == 0)
                throw Err(Fido2ErrorKind.NoCredentials, "Security key holds none of the enrolled credentials.");

            var hmac = ReadPtr(fido_assert_hmac_secret_ptr(assert, 0), fido_assert_hmac_secret_len(assert, 0));
            if (hmac.Length == 0)
                throw Err(Fido2ErrorKind.HmacSecretUnsupported, "Security key did not return an hmac-secret.");
            var credId = ReadPtr(fido_assert_id_ptr(assert, 0), fido_assert_id_len(assert, 0));
            return new Fido2HmacResult(credId, hmac);
        }
        finally
        {
            if (assert != IntPtr.Zero) fido_assert_free(ref assert);
            CloseDevice(ref dev);
        }
    }

    public Fido2ServerAssertion GetServerAssertion(string rpId, IReadOnlyList<byte[]> allowCredentialIds, byte[] clientDataHash, string? pin)
    {
        var path = FirstDevicePath() ?? throw Err(Fido2ErrorKind.NoDevice, "No security key detected.");
        IntPtr dev = IntPtr.Zero, assert = IntPtr.Zero;
        try
        {
            dev = OpenDevice(path);
            assert = fido_assert_new();

            // Unified with the Windows backend (which signs SHA256(clientDataJSON)): the signed
            // client-data hash is SHA256(serverChallenge). The server verifies over the same.
            var cdh = System.Security.Cryptography.SHA256.HashData(clientDataHash);
            Check(fido_assert_set_rp(assert, rpId), "assert_set_rp");
            Check(fido_assert_set_clientdata_hash(assert, cdh, (nuint)cdh.Length), "assert_set_clientdata_hash");
            foreach (var cid in allowCredentialIds)
                Check(fido_assert_allow_cred(assert, cid, (nuint)cid.Length), "assert_allow_cred");

            var rc = fido_dev_get_assert(dev, assert, pin);
            if (rc != FIDO_OK) throw MapErr(rc, "get_assert");
            if (fido_assert_count(assert) == 0)
                throw Err(Fido2ErrorKind.NoCredentials, "Security key holds none of the enrolled credentials.");

            var authData = ReadPtr(fido_assert_authdata_raw_ptr(assert, 0), fido_assert_authdata_raw_len(assert, 0));
            var sig = ReadPtr(fido_assert_sig_ptr(assert, 0), fido_assert_sig_len(assert, 0));
            var credId = ReadPtr(fido_assert_id_ptr(assert, 0), fido_assert_id_len(assert, 0));
            if (authData.Length == 0 || sig.Length == 0)
                throw Err(Fido2ErrorKind.Other, "Security key returned an empty assertion.");
            return new Fido2ServerAssertion(credId, authData, sig);
        }
        finally
        {
            if (assert != IntPtr.Zero) fido_assert_free(ref assert);
            CloseDevice(ref dev);
        }
    }

    // --- device helpers ---

    private static readonly byte[] ZeroHash = new byte[32];

    private static string? FirstDevicePath()
    {
        const int max = 8;
        var list = fido_dev_info_new((nuint)max);
        try
        {
            if (fido_dev_info_manifest(list, (nuint)max, out var olen) != FIDO_OK || olen == 0)
                return null;
            var di = fido_dev_info_ptr(list, 0);
            if (di == IntPtr.Zero) return null;
            var pathPtr = fido_dev_info_path(di);
            return pathPtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pathPtr);
        }
        finally
        {
            fido_dev_info_free(ref list, (nuint)max);
        }
    }

    private static IntPtr OpenDevice(string path)
    {
        var dev = fido_dev_new();
        var rc = fido_dev_open(dev, path);
        if (rc != FIDO_OK)
        {
            fido_dev_free(ref dev);
            throw MapErr(rc, "dev_open");
        }
        return dev;
    }

    private static void CloseDevice(ref IntPtr dev)
    {
        if (dev == IntPtr.Zero) return;
        try { fido_dev_close(dev); } catch { }
        fido_dev_free(ref dev);
    }

    private static List<string> ExtensionList(IntPtr ci)
    {
        var result = new List<string>();
        var arr = fido_cbor_info_extensions_ptr(ci);
        var len = (int)fido_cbor_info_extensions_len(ci);
        if (arr == IntPtr.Zero || len <= 0) return result;
        for (int i = 0; i < len; i++)
        {
            var strPtr = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
            var s = Marshal.PtrToStringUTF8(strPtr);
            if (s is not null) result.Add(s);
        }
        return result;
    }

    private static byte[] ReadPtr(IntPtr ptr, nuint len)
    {
        var n = (int)len;
        if (ptr == IntPtr.Zero || n <= 0) return Array.Empty<byte>();
        var buf = new byte[n];
        Marshal.Copy(ptr, buf, 0, n);
        return buf;
    }

    private static void Check(int rc, string op)
    {
        if (rc != FIDO_OK) throw MapErr(rc, op);
    }

    private static Fido2Exception Err(Fido2ErrorKind kind, string msg) => new(kind, msg);

    private static Fido2Exception MapErr(int rc, string op)
    {
        var kind = rc switch
        {
            FIDO_ERR_NO_CREDENTIALS => Fido2ErrorKind.NoCredentials,
            FIDO_ERR_PIN_REQUIRED => Fido2ErrorKind.PinRequired,
            FIDO_ERR_PIN_INVALID => Fido2ErrorKind.PinInvalid,
            FIDO_ERR_PIN_BLOCKED or FIDO_ERR_PIN_AUTH_BLOCKED => Fido2ErrorKind.PinBlocked,
            FIDO_ERR_ACTION_TIMEOUT or FIDO_ERR_UP_REQUIRED => Fido2ErrorKind.NoUserPresence,
            _ => Fido2ErrorKind.Other,
        };
        var detail = StrErr(rc);
        var msg = kind switch
        {
            Fido2ErrorKind.NoCredentials => "Security key holds none of the enrolled credentials.",
            Fido2ErrorKind.PinRequired => "This security key requires a PIN.",
            Fido2ErrorKind.PinInvalid => "Wrong PIN.",
            Fido2ErrorKind.PinBlocked => "Too many wrong PIN attempts. The key is locked; remove and reinsert it.",
            Fido2ErrorKind.NoUserPresence => "Timed out waiting for you to touch the security key.",
            _ => $"Security key error during {op}: {detail} (0x{rc:x2}).",
        };
        return new Fido2Exception(kind, msg);
    }

    private static string StrErr(int rc)
    {
        try
        {
            var p = fido_strerr(rc);
            return p == IntPtr.Zero ? "unknown" : Marshal.PtrToStringUTF8(p) ?? "unknown";
        }
        catch { return "unknown"; }
    }

    // --- P/Invoke (libfido2) ---

    [DllImport(Lib)] private static extern void fido_init(int flags);
    [DllImport(Lib)] private static extern IntPtr fido_strerr(int rc);

    [DllImport(Lib)] private static extern IntPtr fido_dev_info_new(nuint n);
    [DllImport(Lib)] private static extern int fido_dev_info_manifest(IntPtr list, nuint ilen, out nuint olen);
    [DllImport(Lib)] private static extern IntPtr fido_dev_info_ptr(IntPtr list, nuint i);
    [DllImport(Lib)] private static extern IntPtr fido_dev_info_path(IntPtr di);
    [DllImport(Lib)] private static extern void fido_dev_info_free(ref IntPtr list, nuint n);

    [DllImport(Lib)] private static extern IntPtr fido_dev_new();
    [DllImport(Lib, CharSet = CharSet.Ansi)] private static extern int fido_dev_open(IntPtr dev, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Lib)] private static extern int fido_dev_close(IntPtr dev);
    [DllImport(Lib)] private static extern void fido_dev_free(ref IntPtr dev);
    [DllImport(Lib)] private static extern int fido_dev_get_cbor_info(IntPtr dev, IntPtr ci);

    [DllImport(Lib)] private static extern IntPtr fido_cbor_info_new();
    [DllImport(Lib)] private static extern void fido_cbor_info_free(ref IntPtr ci);
    [DllImport(Lib)] private static extern IntPtr fido_cbor_info_extensions_ptr(IntPtr ci);
    [DllImport(Lib)] private static extern nuint fido_cbor_info_extensions_len(IntPtr ci);

    [DllImport(Lib)] private static extern IntPtr fido_cred_new();
    [DllImport(Lib)] private static extern void fido_cred_free(ref IntPtr cred);
    [DllImport(Lib)] private static extern int fido_cred_set_type(IntPtr cred, int coseAlg);
    [DllImport(Lib)] private static extern int fido_cred_set_clientdata_hash(IntPtr cred, byte[] ptr, nuint len);
    [DllImport(Lib)] private static extern int fido_cred_set_rp(IntPtr cred, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] private static extern int fido_cred_set_user(IntPtr cred, byte[] userId, nuint userIdLen,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string displayName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? icon);
    [DllImport(Lib)] private static extern int fido_cred_set_extensions(IntPtr cred, int flags);
    [DllImport(Lib)] private static extern int fido_cred_set_rk(IntPtr cred, int opt);
    [DllImport(Lib, CharSet = CharSet.Ansi)] private static extern int fido_dev_make_cred(IntPtr dev, IntPtr cred, [MarshalAs(UnmanagedType.LPUTF8Str)] string? pin);
    [DllImport(Lib)] private static extern IntPtr fido_cred_id_ptr(IntPtr cred);
    [DllImport(Lib)] private static extern nuint fido_cred_id_len(IntPtr cred);
    [DllImport(Lib)] private static extern IntPtr fido_cred_pubkey_ptr(IntPtr cred);
    [DllImport(Lib)] private static extern nuint fido_cred_pubkey_len(IntPtr cred);

    [DllImport(Lib)] private static extern IntPtr fido_assert_new();
    [DllImport(Lib)] private static extern void fido_assert_free(ref IntPtr assert);
    [DllImport(Lib)] private static extern int fido_assert_set_rp(IntPtr assert, [MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport(Lib)] private static extern int fido_assert_set_clientdata_hash(IntPtr assert, byte[] ptr, nuint len);
    [DllImport(Lib)] private static extern int fido_assert_allow_cred(IntPtr assert, byte[] ptr, nuint len);
    [DllImport(Lib)] private static extern int fido_assert_set_extensions(IntPtr assert, int flags);
    [DllImport(Lib)] private static extern int fido_assert_set_hmac_salt(IntPtr assert, byte[] salt, nuint len);
    [DllImport(Lib, CharSet = CharSet.Ansi)] private static extern int fido_dev_get_assert(IntPtr dev, IntPtr assert, [MarshalAs(UnmanagedType.LPUTF8Str)] string? pin);
    [DllImport(Lib)] private static extern nuint fido_assert_count(IntPtr assert);
    [DllImport(Lib)] private static extern IntPtr fido_assert_hmac_secret_ptr(IntPtr assert, nuint idx);
    [DllImport(Lib)] private static extern nuint fido_assert_hmac_secret_len(IntPtr assert, nuint idx);
    [DllImport(Lib)] private static extern IntPtr fido_assert_id_ptr(IntPtr assert, nuint idx);
    [DllImport(Lib)] private static extern nuint fido_assert_id_len(IntPtr assert, nuint idx);
    [DllImport(Lib)] private static extern IntPtr fido_assert_authdata_raw_ptr(IntPtr assert, nuint idx);
    [DllImport(Lib)] private static extern nuint fido_assert_authdata_raw_len(IntPtr assert, nuint idx);
    [DllImport(Lib)] private static extern IntPtr fido_assert_sig_ptr(IntPtr assert, nuint idx);
    [DllImport(Lib)] private static extern nuint fido_assert_sig_len(IntPtr assert, nuint idx);
}
