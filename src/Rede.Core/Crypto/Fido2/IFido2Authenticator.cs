namespace Rede.Core.Crypto.Fido2;

/// <summary>Result of creating a credential on the authenticator.</summary>
/// <param name="CredentialId">Raw credential id bytes returned by the authenticator.</param>
/// <param name="CredentialPublicKeyCose">COSE-encoded credential public key (used by the
/// server-side 2FA enrollment in Phase B; may be empty for local-unlock-only enrollment).</param>
public sealed record Fido2Credential(byte[] CredentialId, byte[] CredentialPublicKeyCose);

/// <summary>Result of an hmac-secret assertion: which credential the connected device used,
/// and the 32-byte HMAC output for the requested salt.</summary>
public sealed record Fido2HmacResult(byte[] CredentialId, byte[] HmacSecret);

/// <summary>Result of a server-2FA assertion: the matched credential, the raw authenticator
/// data, and the DER ECDSA signature over (authData ‖ clientDataHash).</summary>
public sealed record Fido2ServerAssertion(byte[] CredentialId, byte[] AuthData, byte[] Signature);

/// <summary>Raised for user-actionable authenticator errors (no device, wrong PIN, no touch,
/// missing hmac-secret support). The message is safe to surface in the UI.</summary>
public sealed class Fido2Exception : Exception
{
    public Fido2ErrorKind Kind { get; }
    public Fido2Exception(Fido2ErrorKind kind, string message) : base(message) { Kind = kind; }
}

public enum Fido2ErrorKind
{
    NotAvailable,      // libfido2 not installed / no usable backend
    NoDevice,          // no authenticator plugged in
    HmacSecretUnsupported,
    NoCredentials,     // connected device holds none of the allowed credentials (try another key)
    PinRequired,
    PinInvalid,
    PinBlocked,
    NoUserPresence,    // touch timed out / cancelled
    Cancelled,
    Other,
}

/// <summary>
/// Abstraction over the physical FIDO2 authenticator. Implemented by <c>LibFido2Authenticator</c>
/// (native, P/Invoke over libfido2) in production and by an in-memory fake in tests, so the PMS
/// wrap/unwrap and sidecar logic in <see cref="Fido2UnlockService"/> can be tested without hardware.
/// </summary>
public interface IFido2Authenticator
{
    /// <summary>True if a usable backend (libfido2 / platform WebAuthn) is loaded.</summary>
    bool IsAvailable { get; }

    /// <summary>Short human-readable backend state for diagnostics (shown in Settings).</summary>
    string DescribeBackend();

    /// <summary>True if at least one authenticator is currently connected.</summary>
    bool HasDevice();

    /// <summary>True if the connected authenticator advertises the CTAP2 hmac-secret extension.</summary>
    bool SupportsHmacSecret();

    /// <summary>
    /// Create a discoverable credential with the hmac-secret extension enabled.
    /// Throws <see cref="Fido2Exception"/> on user-actionable failures.
    /// </summary>
    Fido2Credential MakeCredential(string rpId, string userName, byte[] userHandle, string? pin);

    /// <summary>
    /// Perform a single get-assertion over <paramref name="allowCredentialIds"/> requesting
    /// hmac-secret for <paramref name="salt"/>. The connected device picks whichever credential
    /// it holds and returns that credential id plus its 32-byte HMAC output for the salt
    /// (deterministic per credential+salt) — one user touch even when several keys are enrolled.
    /// Throws <see cref="Fido2Exception"/> (Kind=NoCredentials when the device holds none of them).
    /// </summary>
    Fido2HmacResult GetHmacSecret(string rpId, IReadOnlyList<byte[]> allowCredentialIds, byte[] salt, string? pin);

    /// <summary>
    /// Perform a standard assertion over <paramref name="allowCredentialIds"/> with
    /// <paramref name="clientDataHash"/> (the 32-byte server challenge) for server-side 2FA.
    /// Returns the matched credential, raw authenticator data, and DER ECDSA signature.
    /// Throws <see cref="Fido2Exception"/> on failure.
    /// </summary>
    Fido2ServerAssertion GetServerAssertion(string rpId, IReadOnlyList<byte[]> allowCredentialIds, byte[] clientDataHash, string? pin);
}
