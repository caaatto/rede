using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Rede.Core.Crypto.Fido2;

/// <summary>
/// <see cref="IFido2Authenticator"/> backed by the built-in Windows WebAuthn API (webauthn.dll,
/// Windows 10 1903+). No third-party native library is shipped — Windows itself drives the key
/// (and shows the system security-key dialog for PIN/touch). hmac-secret for local profile unlock
/// uses the salt API (WEBAUTHN_AUTHENTICATOR_GET_ASSERTION_OPTIONS v6 + ASSERTION pHmacSecret,
/// available on recent Windows builds).
///
/// All API calls need a foreground window handle, supplied lazily via the HWND provider so the
/// authenticator can be constructed before the window handle exists.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWebAuthnAuthenticator : IFido2Authenticator
{
    private const string RpName = "Rede";
    private const string HashSha256 = "SHA-256";
    private const string CredTypePublicKey = "public-key";
    private const int COSE_ES256 = -7;

    private const int ATTACHMENT_CROSS_PLATFORM = 2;
    private const int UV_REQUIRED = 1;
    private const int UV_PREFERRED = 2;
    private const int ATTESTATION_NONE = 1; // WEBAUTHN_ATTESTATION_CONVEYANCE_PREFERENCE_NONE

    private readonly Func<IntPtr> _hwndProvider;

    public WindowsWebAuthnAuthenticator(Func<IntPtr> hwndProvider) => _hwndProvider = hwndProvider;

    public bool IsAvailable
    {
        get { try { return WebAuthNGetApiVersionNumber() > 0; } catch { return false; } }
    }

    public string DescribeBackend()
    {
        try
        {
            int v = WebAuthNGetApiVersionNumber();
            return v > 0 ? $"Windows WebAuthn API v{v}" : "Windows WebAuthn API not available";
        }
        catch (Exception e) { return "webauthn.dll load error: " + e.GetType().Name; }
    }

    // The WebAuthn API does not expose device enumeration without a ceremony; the OS dialog itself
    // prompts to insert/tap a key. Treat "API present" as ready, and "hmac-secret supported" as a
    // function of API version (the salt API needs a recent build) — real failures surface per-call.
    public bool HasDevice() => IsAvailable;
    public bool SupportsHmacSecret()
    {
        try { return WebAuthNGetApiVersionNumber() >= 4; } catch { return false; }
    }

    // --- IFido2Authenticator ---

    public Fido2Credential MakeCredential(string rpId, string userName, byte[] userHandle, string? pin)
    {
        var rp = new RpInfo { dwVersion = 1, pwszId = rpId, pwszName = RpName, pwszIcon = null };
        var user = new UserInfo
        {
            dwVersion = 1,
            cbId = (uint)userHandle.Length, pbId = Pin(userHandle, out var hUser),
            pwszName = userName, pwszIcon = null, pwszDisplayName = userName,
        };
        var coseTypePtr = Marshal.StringToHGlobalUni(CredTypePublicKey);
        var coseParam = new CoseParam { dwVersion = 1, pwszCredentialType = coseTypePtr, lAlg = COSE_ES256 };
        var hCose = GCHandle.Alloc(coseParam, GCHandleType.Pinned);
        var coseParams = new CoseParams { cCredentialParameters = 1, pCredentialParameters = hCose.AddrOfPinnedObject() };

        var clientData = MakeClientData(userHandle, out var hClient); // content irrelevant for enroll

        // hmac-secret extension: pvExtension -> BOOL TRUE
        var trueBuf = Marshal.AllocHGlobal(4); Marshal.WriteInt32(trueBuf, 1);
        var extId = Marshal.StringToHGlobalUni("hmac-secret");
        var ext = new Extension { pwszExtensionIdentifier = extId, cbExtension = 4, pvExtension = trueBuf };
        var hExt = GCHandle.Alloc(ext, GCHandleType.Pinned);

        var opts = new MakeCredOptions
        {
            dwVersion = 3,
            dwTimeoutMilliseconds = 120000,
            cExtensions = 1, pExtensions = hExt.AddrOfPinnedObject(),
            dwAuthenticatorAttachment = ATTACHMENT_CROSS_PLATFORM,
            bRequireResidentKey = 1,
            dwUserVerificationRequirement = UV_PREFERRED,
            dwAttestationConveyancePreference = ATTESTATION_NONE,
        };

        IntPtr pAttestation = IntPtr.Zero;
        try
        {
            int hr = WebAuthNAuthenticatorMakeCredential(_hwndProvider(), ref rp, ref user, ref coseParams, ref clientData, ref opts, out pAttestation);
            if (hr != 0) throw MapHr(hr, "make_credential");

            // Read credentialId + authenticatorData from the (versioned) attestation struct by offset.
            // WEBAUTHN_CREDENTIAL_ATTESTATION layout:
            //  0  dwVersion
            //  8  pwszFormatType (ptr)
            // 16  cbAuthenticatorData / 24 pbAuthenticatorData
            // 32  cbAttestation / 40 pbAttestation
            // 48  dwAttestationDecodeType (+pad) / 56 pvAttestationDecode
            // 64  cbAttestationObject / 72 pbAttestationObject
            // 80  cbCredentialId / 88 pbCredentialId
            uint cbAuthData = (uint)Marshal.ReadInt32(pAttestation, 16);
            IntPtr pbAuthData = Marshal.ReadIntPtr(pAttestation, 24);
            uint cbCredId = (uint)Marshal.ReadInt32(pAttestation, 80);
            IntPtr pbCredId = Marshal.ReadIntPtr(pAttestation, 88);

            var credId = Copy(pbCredId, cbCredId);
            var authData = Copy(pbAuthData, cbAuthData);
            var pub = ExtractEs256PublicKey(authData); // raw x||y (64 B) for server-side 2FA
            return new Fido2Credential(credId, pub);
        }
        finally
        {
            if (pAttestation != IntPtr.Zero) WebAuthNFreeCredentialAttestation(pAttestation);
            hExt.Free(); Marshal.FreeHGlobal(extId); Marshal.FreeHGlobal(trueBuf);
            hCose.Free(); Marshal.FreeHGlobal(coseTypePtr);
            if (hUser.IsAllocated) hUser.Free();
            if (hClient.IsAllocated) hClient.Free();
        }
    }

    public Fido2HmacResult GetHmacSecret(string rpId, IReadOnlyList<byte[]> allowCredentialIds, byte[] salt, string? pin, bool requireUv)
    {
        // requireUv is ignored: the Windows WebAuthn API always performs user verification, so the
        // hmac-secret is always the CredRandomWithUV variant. (The try-both unlock derives this on
        // its UV pass; Windows-enrolled wraps therefore open on Windows and on Linux's UV retry.)
        // Local unlock: clientData is irrelevant; we only use the returned hmac-secret.
        var (credId, _, _, hmac) = Assert(rpId, allowCredentialIds, new byte[32], salt, requireHmac: true);
        if (hmac is null || hmac.Length == 0)
            throw new Fido2Exception(Fido2ErrorKind.HmacSecretUnsupported,
                "Windows did not return an hmac-secret (your Windows build may be too old).");
        return new Fido2HmacResult(credId, hmac);
    }

    public Fido2ServerAssertion GetServerAssertion(string rpId, IReadOnlyList<byte[]> allowCredentialIds, byte[] clientDataHash, string? pin)
    {
        // clientDataHash is the raw bytes the server bound; the API signs SHA256(clientDataJSON),
        // so we pass clientDataHash as the clientDataJSON content (server hashes the same input).
        var (credId, authData, sig, _) = Assert(rpId, allowCredentialIds, clientDataHash, null, requireHmac: false);
        return new Fido2ServerAssertion(credId, authData!, sig!);
    }

    // --- shared assertion path ---

    private (byte[] credId, byte[]? authData, byte[]? sig, byte[]? hmac) Assert(
        string rpId, IReadOnlyList<byte[]> allowCredentialIds, byte[] clientDataContent, byte[]? hmacSalt, bool requireHmac)
    {
        var clientData = new ClientData
        {
            dwVersion = 1,
            cbClientDataJSON = (uint)clientDataContent.Length, pbClientDataJSON = Pin(clientDataContent, out var hCd),
            pwszHashAlgId = HashSha256,
        };

        // Allow-credential list (WEBAUTHN_CREDENTIAL_EX[] -> WEBAUTHN_CREDENTIAL_LIST).
        var credHandles = new List<GCHandle>();
        IntPtr pCredList = BuildAllowList(allowCredentialIds, credHandles, out var credExArray, out var pCredExPtrs);

        // Optional hmac-secret salt values.
        IntPtr pSaltValues = IntPtr.Zero, pGlobalSalt = IntPtr.Zero, pSaltBytes = IntPtr.Zero;
        if (hmacSalt is not null)
        {
            pSaltBytes = Marshal.AllocHGlobal(hmacSalt.Length);
            Marshal.Copy(hmacSalt, 0, pSaltBytes, hmacSalt.Length);
            // WEBAUTHN_HMAC_SECRET_SALT { DWORD cbFirst; PBYTE pbFirst; DWORD cbSecond; PBYTE pbSecond; }
            // x64 layout: cbFirst@0, pbFirst@8, cbSecond@16, pbSecond@24 -> 32 bytes total.
            pGlobalSalt = Marshal.AllocHGlobal(16 + IntPtr.Size * 2);
            Marshal.WriteInt32(pGlobalSalt, 0, hmacSalt.Length);
            Marshal.WriteIntPtr(pGlobalSalt, 8, pSaltBytes);
            Marshal.WriteInt32(pGlobalSalt, 8 + IntPtr.Size, 0);
            Marshal.WriteIntPtr(pGlobalSalt, 8 + IntPtr.Size + 8, IntPtr.Zero);
            // WEBAUTHN_HMAC_SECRET_SALT_VALUES { pGlobalHmacSalt; cCred; pCredList; }
            pSaltValues = Marshal.AllocHGlobal(IntPtr.Size + 8 + IntPtr.Size);
            Marshal.WriteIntPtr(pSaltValues, 0, pGlobalSalt);
            Marshal.WriteInt32(pSaltValues, IntPtr.Size, 0);
            Marshal.WriteIntPtr(pSaltValues, IntPtr.Size + 8, IntPtr.Zero);
        }

        var opts = new GetAssertOptions
        {
            dwVersion = 6,
            dwTimeoutMilliseconds = 120000,
            dwUserVerificationRequirement = (uint)(requireHmac ? UV_REQUIRED : UV_PREFERRED),
            pAllowCredentialList = pCredList,
            pHmacSecretSaltValues = pSaltValues,
        };

        IntPtr pAssertion = IntPtr.Zero;
        try
        {
            int hr = WebAuthNAuthenticatorGetAssertion(_hwndProvider(), rpId, ref clientData, ref opts, out pAssertion);
            if (hr != 0) throw MapHr(hr, "get_assertion");

            // WEBAUTHN_ASSERTION layout (x64). NOTE: unlike the attestation struct, dwVersion is
            // followed directly by a DWORD (not a pointer), so fields are NOT all 8-aligned off 0.
            //   0  dwVersion (DWORD)
            //   4  cbAuthenticatorData / 8  pbAuthenticatorData
            //  16  cbSignature / 24 pbSignature
            //  32  Credential { dwVersion@32; cbId@36; pbId@40; pwszCredentialType@48 } -> ends@56
            //  56  cbUserId / 64 pbUserId
            //  v2: 72 Extensions(c@72,p@80) / 88 cbCredLargeBlob / 96 pbCredLargeBlob / 104 dwCredLargeBlobStatus
            //  v3: 112 pHmacSecret
            int ver = Marshal.ReadInt32(pAssertion, 0);
            uint cbAuth = (uint)Marshal.ReadInt32(pAssertion, 4);
            IntPtr pbAuth = Marshal.ReadIntPtr(pAssertion, 8);
            uint cbSig = (uint)Marshal.ReadInt32(pAssertion, 16);
            IntPtr pbSig = Marshal.ReadIntPtr(pAssertion, 24);
            uint cbId = (uint)Marshal.ReadInt32(pAssertion, 36);
            IntPtr pbId = Marshal.ReadIntPtr(pAssertion, 40);

            var authData = Copy(pbAuth, cbAuth);
            var sig = Copy(pbSig, cbSig);
            var credId = Copy(pbId, cbId);

            byte[]? hmac = null;
            if (requireHmac && ver >= 3)
            {
                IntPtr pHmac = Marshal.ReadIntPtr(pAssertion, 112); // WEBAUTHN_HMAC_SECRET_SALT*
                if (pHmac != IntPtr.Zero)
                {
                    uint cbFirst = (uint)Marshal.ReadInt32(pHmac, 0);
                    IntPtr pbFirst = Marshal.ReadIntPtr(pHmac, 8);
                    hmac = Copy(pbFirst, cbFirst);
                }
            }
            return (credId, authData, sig, hmac);
        }
        finally
        {
            if (pAssertion != IntPtr.Zero) WebAuthNFreeAssertion(pAssertion);
            if (hCd.IsAllocated) hCd.Free();
            FreeAllowList(pCredList, credExArray, pCredExPtrs, credHandles);
            if (pSaltValues != IntPtr.Zero) Marshal.FreeHGlobal(pSaltValues);
            if (pGlobalSalt != IntPtr.Zero) Marshal.FreeHGlobal(pGlobalSalt);
            if (pSaltBytes != IntPtr.Zero) Marshal.FreeHGlobal(pSaltBytes);
        }
    }

    // --- allow-list marshaling (WEBAUTHN_CREDENTIAL_EX / WEBAUTHN_CREDENTIAL_LIST) ---

    private static IntPtr BuildAllowList(IReadOnlyList<byte[]> ids, List<GCHandle> handles, out IntPtr credExArray, out IntPtr pPtrArray)
    {
        credExArray = IntPtr.Zero; pPtrArray = IntPtr.Zero;
        if (ids.Count == 0) return IntPtr.Zero;

        // WEBAUTHN_CREDENTIAL_EX { dwVersion; cbId; pbId; pwszCredentialType; dwTransports; } = 4+pad+4+8+8+4(+pad) -> 32 bytes
        const int credExSize = 32;
        credExArray = Marshal.AllocHGlobal(credExSize * ids.Count);
        var typePtr = Marshal.StringToHGlobalUni(CredTypePublicKey);
        handles.Add(GCHandle.Alloc(typePtr, GCHandleType.Normal)); // keep ref; freed via FreeAllowList

        pPtrArray = Marshal.AllocHGlobal(IntPtr.Size * ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            var idPtr = Marshal.AllocHGlobal(ids[i].Length);
            Marshal.Copy(ids[i], 0, idPtr, ids[i].Length);
            handles.Add(GCHandle.Alloc(idPtr, GCHandleType.Normal));
            IntPtr ex = credExArray + i * credExSize;
            Marshal.WriteInt32(ex, 0, 1);                 // dwVersion
            Marshal.WriteInt32(ex, 4, ids[i].Length);     // cbId
            Marshal.WriteIntPtr(ex, 8, idPtr);            // pbId
            Marshal.WriteIntPtr(ex, 16, typePtr);         // pwszCredentialType
            Marshal.WriteInt32(ex, 24, 0);                // dwTransports = 0 (any)
            Marshal.WriteIntPtr(pPtrArray, i * IntPtr.Size, ex);
        }
        // WEBAUTHN_CREDENTIAL_LIST { cCredentials; ppCredentials; }
        IntPtr list = Marshal.AllocHGlobal(8 + IntPtr.Size);
        Marshal.WriteInt32(list, 0, ids.Count);
        Marshal.WriteIntPtr(list, 8, pPtrArray);
        return list;
    }

    private static void FreeAllowList(IntPtr list, IntPtr credExArray, IntPtr pPtrArray, List<GCHandle> handles)
    {
        foreach (var h in handles) { if (h.IsAllocated) { Marshal.FreeHGlobal((IntPtr)h.Target!); h.Free(); } }
        if (pPtrArray != IntPtr.Zero) Marshal.FreeHGlobal(pPtrArray);
        if (credExArray != IntPtr.Zero) Marshal.FreeHGlobal(credExArray);
        if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
    }

    // --- helpers ---

    private static ClientData MakeClientData(byte[] content, out GCHandle handle)
        => new ClientData { dwVersion = 1, cbClientDataJSON = (uint)content.Length, pbClientDataJSON = Pin(content, out handle), pwszHashAlgId = HashSha256 };

    private static IntPtr Pin(byte[] data, out GCHandle handle)
    {
        handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        return handle.AddrOfPinnedObject();
    }

    private static byte[] Copy(IntPtr ptr, uint len)
    {
        if (ptr == IntPtr.Zero || len == 0) return Array.Empty<byte>();
        var b = new byte[len];
        Marshal.Copy(ptr, b, 0, (int)len);
        return b;
    }

    /// <summary>Extract raw P-256 x||y (64 B) from authenticatorData's attested COSE key (ES256).</summary>
    private static byte[] ExtractEs256PublicKey(byte[] authData)
    {
        try
        {
            // 32 rpIdHash + 1 flags + 4 signCount + 16 aaguid + 2 credIdLen
            int pos = 32 + 1 + 4 + 16;
            int credIdLen = (authData[pos] << 8) | authData[pos + 1];
            pos += 2 + credIdLen;
            // COSE_Key map follows; find labels -2 (x) and -3 (y), each a 32-byte bstr.
            var cose = authData.AsSpan(pos);
            byte[]? x = null, y = null;
            for (int i = 0; i < cose.Length - 1; i++)
            {
                // -2 encodes as 0x21, -3 as 0x22 (negative int minor); followed by bstr 0x58 0x20 (32).
                if (cose[i] == 0x21 && i + 2 < cose.Length && cose[i + 1] == 0x58 && cose[i + 2] == 0x20)
                    x = cose.Slice(i + 3, 32).ToArray();
                if (cose[i] == 0x22 && i + 2 < cose.Length && cose[i + 1] == 0x58 && cose[i + 2] == 0x20)
                    y = cose.Slice(i + 3, 32).ToArray();
            }
            if (x is not null && y is not null)
            {
                var pub = new byte[64];
                Buffer.BlockCopy(x, 0, pub, 0, 32);
                Buffer.BlockCopy(y, 0, pub, 32, 32);
                return pub;
            }
        }
        catch { }
        return Array.Empty<byte>(); // server-2FA enroll will skip if empty
    }

    private static Fido2Exception MapHr(int hr, string op)
    {
        // Common WebAuthn HRESULTs (NTE_*). 0x80090027 NTE_NOT_SUPPORTED, 0x80090029 NTE_DEVICE_NOT_READY,
        // 0x800704C7 user cancelled, 0x8007001F device not functioning.
        string detail;
        try { var p = WebAuthNGetErrorName(hr); detail = p == IntPtr.Zero ? $"0x{hr:x8}" : Marshal.PtrToStringUni(p) ?? $"0x{hr:x8}"; }
        catch { detail = $"0x{hr:x8}"; }
        var kind = (uint)hr switch
        {
            0x800704C7 => Fido2ErrorKind.Cancelled,
            0x80090027 => Fido2ErrorKind.HmacSecretUnsupported,
            _ => Fido2ErrorKind.Other,
        };
        var msg = kind == Fido2ErrorKind.Cancelled
            ? "Security-key prompt was cancelled."
            : $"Windows security-key error during {op}: {detail}.";
        return new Fido2Exception(kind, msg);
    }

    // --- structs ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RpInfo { public uint dwVersion; public string pwszId; public string pwszName; public string? pwszIcon; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo
    {
        public uint dwVersion; public uint cbId; public IntPtr pbId;
        public string pwszName; public string? pwszIcon; public string pwszDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CoseParam { public uint dwVersion; public IntPtr pwszCredentialType; public int lAlg; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CoseParams { public uint cCredentialParameters; public IntPtr pCredentialParameters; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ClientData { public uint dwVersion; public uint cbClientDataJSON; public IntPtr pbClientDataJSON; public string pwszHashAlgId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Extension { public IntPtr pwszExtensionIdentifier; public uint cbExtension; public IntPtr pvExtension; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MakeCredOptions
    {
        public uint dwVersion;
        public uint dwTimeoutMilliseconds;
        public uint cCredentials; public IntPtr pCredentials;          // CredentialList
        public uint cExtensions; public IntPtr pExtensions;            // Extensions
        public uint dwAuthenticatorAttachment;
        public int bRequireResidentKey;
        public uint dwUserVerificationRequirement;
        public uint dwAttestationConveyancePreference;
        public uint dwFlags;
        public IntPtr pCancellationId;        // v2
        public IntPtr pAllowCredentialList;   // v3 (unused for make)
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct GetAssertOptions
    {
        public uint dwVersion;
        public uint dwTimeoutMilliseconds;
        public uint cCredentials; public IntPtr pCredentials;          // CredentialList (legacy)
        public uint cExtensions; public IntPtr pExtensions;            // Extensions
        public uint dwAuthenticatorAttachment;
        public uint dwUserVerificationRequirement;
        public uint dwFlags;
        public IntPtr pwszU2fAppId;           // v2
        public IntPtr pbU2fAppId;             // v2
        public IntPtr pCancellationId;        // v3
        public IntPtr pAllowCredentialList;   // v4
        public uint dwCredLargeBlobOperation; // v5
        public uint cbCredLargeBlob; public IntPtr pbCredLargeBlob; // v5
        public IntPtr pHmacSecretSaltValues;  // v6
        public int bBrowserInPrivateMode;     // v6
    }

    [DllImport("webauthn.dll")] private static extern int WebAuthNGetApiVersionNumber();
    [DllImport("webauthn.dll")] private static extern IntPtr WebAuthNGetErrorName(int hr);
    [DllImport("webauthn.dll")] private static extern void WebAuthNFreeCredentialAttestation(IntPtr p);
    [DllImport("webauthn.dll")] private static extern void WebAuthNFreeAssertion(IntPtr p);

    [DllImport("webauthn.dll")]
    private static extern int WebAuthNAuthenticatorMakeCredential(
        IntPtr hWnd, ref RpInfo rp, ref UserInfo user, ref CoseParams coseParams,
        ref ClientData clientData, ref MakeCredOptions options, out IntPtr ppCredentialAttestation);

    [DllImport("webauthn.dll", CharSet = CharSet.Unicode)]
    private static extern int WebAuthNAuthenticatorGetAssertion(
        IntPtr hWnd, string rpId, ref ClientData clientData, ref GetAssertOptions options, out IntPtr ppAssertion);
}
