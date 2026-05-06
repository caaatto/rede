using Sodium;

namespace Rede.Core.Crypto;

/// <summary>
/// X3DH / PQXDH — Extended Triple Diffie-Hellman with optional post-quantum hybrid layer.
///
/// Classical X3DH (X25519 only): backward-compatible path for peers without ML-KEM keys.
/// PQXDH (X25519 + ML-KEM-768): used when both peers have post-quantum pre-keys.
/// Spec: https://signal.org/docs/specifications/pqxdh/
///
/// HKDF input order:
///   classical: F || DH1 || DH2 || DH3 [|| DH4]            info = "RedeX3DH"
///   PQXDH:     F || DH1 || DH2 || DH3 [|| DH4] || SS_PQ   info = "RedePQXDH"
/// </summary>
public static class X3dh
{
    public record PreKeyBundle(
        PreKeyBundlePublic PublicBundle,
        PreKeyBundlePrivate PrivateKeys
    );

    /// <summary>Public wire format — base64 strings for JSON server transport.</summary>
    public record PreKeyBundlePublic(
        string SignedPreKey,
        string SignedPreKeySig,
        List<OneTimePreKeyPublic> OneTimePreKeys,
        // PQXDH fields — null on legacy bundles
        string? PqSignedPreKey,
        string? PqSignedPreKeySig,
        List<OneTimePreKeyPublic>? PqOneTimePreKeys
    );

    public record OneTimePreKeyPublic(int Id, string Key);

    /// <summary>Private parts — byte[] for storage in Profile, zeroable.</summary>
    public record PreKeyBundlePrivate(
        DoubleRatchet.KeyPairBytes SignedPreKey,
        List<OneTimePreKeyPrivate> OneTimePreKeys,
        DoubleRatchet.KeyPairBytes? PqSignedPreKey,
        List<OneTimePreKeyPrivate>? PqOneTimePreKeys
    );

    public record OneTimePreKeyPrivate(int Id, byte[] PublicKey, byte[] SecretKey);

    /// <summary>Recipient bundle with byte[] key material (decoded once at protocol boundary).</summary>
    public record RecipientBundle(
        byte[] IdentityKey,
        byte[] SignedPreKey,
        byte[] SignedPreKeySig,
        byte[] SigningKey,
        OneTimePreKeyBytes? OneTimePreKey,
        // PQXDH fields — null on legacy peers
        byte[]? PqSignedPreKey,
        byte[]? PqSignedPreKeySig,
        OneTimePreKeyBytes? PqOneTimePreKey
    );

    public record OneTimePreKeyBytes(int Id, byte[] Key);

    public record X3dhInitiateResult(
        byte[] SharedSecret,
        byte[] EphemeralPublic,
        int? UsedOtpkId,
        // PQXDH outputs — null when classical fallback path was taken
        byte[]? PqCiphertext,
        int? UsedPqOtpkId,
        bool PqUsed);

    public record X3dhRespondResult(byte[] SharedSecret);

    private const int PqOneTimePreKeyCount = 20;

    /// <summary>Generate pre-key bundle for server upload (always includes PQ keys).</summary>
    public static PreKeyBundle GeneratePreKeyBundle(byte[] signingSecretKey)
    {
        // Classical signed pre-key (X25519)
        var spk = PublicKeyBox.GenerateKeyPair();
        var spkPub = (byte[])spk.PublicKey.Clone();
        var spkSec = (byte[])spk.PrivateKey.Clone();
        CryptoService.ZeroOut(spk.PrivateKey);

        var spkSigB64 = CryptoService.SignBytesB64(spkPub, signingSecretKey);

        // Classical one-time pre-keys (X25519)
        var otpksPublic = new List<OneTimePreKeyPublic>();
        var otpksPrivate = new List<OneTimePreKeyPrivate>();
        for (int i = 0; i < 20; i++)
        {
            var kp = PublicKeyBox.GenerateKeyPair();
            var pk = (byte[])kp.PublicKey.Clone();
            var sk = (byte[])kp.PrivateKey.Clone();
            CryptoService.ZeroOut(kp.PrivateKey);
            otpksPublic.Add(new OneTimePreKeyPublic(i, Convert.ToBase64String(pk)));
            otpksPrivate.Add(new OneTimePreKeyPrivate(i, pk, sk));
        }

        // PQ signed pre-key (ML-KEM-768) — signed with the same Ed25519 signing key
        var (pqSpkPub, pqSpkPriv) = PQKem.GenerateKeyPair();
        var pqSpkSigB64 = CryptoService.SignBytesB64(pqSpkPub, signingSecretKey);

        // PQ one-time pre-keys (ML-KEM-768)
        var pqOtpksPublic = new List<OneTimePreKeyPublic>();
        var pqOtpksPrivate = new List<OneTimePreKeyPrivate>();
        for (int i = 0; i < PqOneTimePreKeyCount; i++)
        {
            var (pub, priv) = PQKem.GenerateKeyPair();
            pqOtpksPublic.Add(new OneTimePreKeyPublic(i, Convert.ToBase64String(pub)));
            pqOtpksPrivate.Add(new OneTimePreKeyPrivate(i, pub, priv));
        }

        return new PreKeyBundle(
            new PreKeyBundlePublic(
                Convert.ToBase64String(spkPub), spkSigB64, otpksPublic,
                Convert.ToBase64String(pqSpkPub), pqSpkSigB64, pqOtpksPublic),
            new PreKeyBundlePrivate(
                new DoubleRatchet.KeyPairBytes(spkPub, spkSec),
                otpksPrivate,
                new DoubleRatchet.KeyPairBytes(pqSpkPub, pqSpkPriv),
                pqOtpksPrivate
            )
        );
    }

    /// <summary>Initiator side of X3DH / PQXDH (auto-detects PQ availability).</summary>
    public static X3dhInitiateResult? Initiate(byte[] senderIdentitySecret, RecipientBundle recipientBundle)
    {
        // Validate classical key lengths
        if (recipientBundle.IdentityKey.Length != 32) return null;
        if (recipientBundle.SignedPreKey.Length != 32) return null;
        if (recipientBundle.SigningKey.Length != 32) return null;
        if (recipientBundle.OneTimePreKey is not null && recipientBundle.OneTimePreKey.Key.Length != 32) return null;

        // Reject low-order / small-subgroup public keys (defense-in-depth)
        if (!CryptoService.IsValidDhPublicKey(recipientBundle.IdentityKey)) return null;
        if (!CryptoService.IsValidDhPublicKey(recipientBundle.SignedPreKey)) return null;
        if (recipientBundle.OneTimePreKey is not null && !CryptoService.IsValidDhPublicKey(recipientBundle.OneTimePreKey.Key)) return null;

        // Verify classical signed pre-key signature
        if (!CryptoService.Verify(recipientBundle.SignedPreKey, recipientBundle.SignedPreKeySig, recipientBundle.SigningKey))
            return null;

        // Determine PQ availability and validate PQ keys if present
        bool pqAvailable = recipientBundle.PqSignedPreKey is not null && recipientBundle.PqSignedPreKeySig is not null;
        if (pqAvailable)
        {
            if (recipientBundle.PqSignedPreKey!.Length != PQKem.PublicKeySize) return null;
            if (recipientBundle.PqOneTimePreKey is not null && recipientBundle.PqOneTimePreKey.Key.Length != PQKem.PublicKeySize) return null;
            if (!CryptoService.Verify(recipientBundle.PqSignedPreKey, recipientBundle.PqSignedPreKeySig!, recipientBundle.SigningKey))
                return null;
        }

        byte[]? dh1 = null, dh2 = null, dh3 = null, dh4 = null, dhConcat = null;
        byte[]? pqSs = null, pqCiphertext = null;
        KeyPair? ek = null;
        byte[]? ekSecretCopy = null;
        try
        {
            var ikAPub = CryptoService.PublicKeyFromSecret(senderIdentitySecret);

            ek = PublicKeyBox.GenerateKeyPair();
            ekSecretCopy = (byte[])ek.PrivateKey.Clone();

            dh1 = CryptoService.Dh(senderIdentitySecret, recipientBundle.SignedPreKey);
            dh2 = CryptoService.Dh(ekSecretCopy, recipientBundle.IdentityKey);
            dh3 = CryptoService.Dh(ekSecretCopy, recipientBundle.SignedPreKey);

            int? usedOtpkId = null;
            int? usedPqOtpkId = null;

            if (recipientBundle.OneTimePreKey is not null)
            {
                dh4 = CryptoService.Dh(ekSecretCopy, recipientBundle.OneTimePreKey.Key);
                usedOtpkId = recipientBundle.OneTimePreKey.Id;
            }

            // PQ encapsulation: prefer PQ-OPK, fall back to PQ-SPK.
            if (pqAvailable)
            {
                byte[] pqTarget;
                if (recipientBundle.PqOneTimePreKey is not null)
                {
                    pqTarget = recipientBundle.PqOneTimePreKey.Key;
                    usedPqOtpkId = recipientBundle.PqOneTimePreKey.Id;
                }
                else
                {
                    pqTarget = recipientBundle.PqSignedPreKey!;
                }
                (pqCiphertext, pqSs) = PQKem.Encapsulate(pqTarget);
            }

            dhConcat = ConcatDhParts(dh1, dh2, dh3, dh4, pqSs);

            var x3dhSalt = Hkdf.X3dhIdentitySalt(ikAPub, recipientBundle.IdentityKey);
            var info = pqAvailable ? "RedePQXDH" : "RedeX3DH";
            var sharedSecret = Hkdf.DeriveKey(dhConcat, x3dhSalt, info, 32);
            var ephemeralPublic = (byte[])ek.PublicKey.Clone();

            return new X3dhInitiateResult(
                sharedSecret, ephemeralPublic, usedOtpkId,
                pqCiphertext, usedPqOtpkId, pqAvailable);
        }
        finally
        {
            if (dh1 is not null) CryptoService.ZeroOut(dh1);
            if (dh2 is not null) CryptoService.ZeroOut(dh2);
            if (dh3 is not null) CryptoService.ZeroOut(dh3);
            if (dh4 is not null) CryptoService.ZeroOut(dh4);
            if (dhConcat is not null) CryptoService.ZeroOut(dhConcat);
            if (pqSs is not null) CryptoService.ZeroOut(pqSs);
            if (ekSecretCopy is not null) CryptoService.ZeroOut(ekSecretCopy);
            if (ek is not null) CryptoService.ZeroOut(ek.PrivateKey);
        }
    }

    /// <summary>Responder side of X3DH / PQXDH.</summary>
    /// <remarks>
    /// pqCiphertext + pqKemSecret are both non-null when the initiator went the PQXDH path.
    /// pqKemSecret is the private key that matches whichever PQ key the initiator targeted
    /// (OPK if available, else SPK) — caller selects from Profile based on usedPqOtpkId.
    /// </remarks>
    public static X3dhRespondResult? Respond(
        byte[] recipientIdentitySecret,
        byte[] signedPreKeySecret,
        byte[]? oneTimePreKeySecret,
        byte[] senderIdentityKey,
        byte[] senderEphemeralKey,
        byte[]? pqCiphertext,
        byte[]? pqKemSecret)
    {
        if (senderIdentityKey.Length != 32) return null;
        if (senderEphemeralKey.Length != 32) return null;

        bool pqUsed = pqCiphertext is not null && pqKemSecret is not null;
        if (pqCiphertext is not null && pqCiphertext.Length != PQKem.CiphertextSize) return null;
        if (pqKemSecret is not null && pqKemSecret.Length != PQKem.PrivateKeySize) return null;

        byte[]? dh1 = null, dh2 = null, dh3 = null, dh4 = null, dhConcat = null;
        byte[]? pqSs = null;
        try
        {
            var ikBPub = CryptoService.PublicKeyFromSecret(recipientIdentitySecret);

            dh1 = CryptoService.Dh(signedPreKeySecret, senderIdentityKey);
            dh2 = CryptoService.Dh(recipientIdentitySecret, senderEphemeralKey);
            dh3 = CryptoService.Dh(signedPreKeySecret, senderEphemeralKey);

            if (oneTimePreKeySecret is not null)
                dh4 = CryptoService.Dh(oneTimePreKeySecret, senderEphemeralKey);

            if (pqUsed)
                pqSs = PQKem.Decapsulate(pqKemSecret!, pqCiphertext!);

            dhConcat = ConcatDhParts(dh1, dh2, dh3, dh4, pqSs);

            var x3dhSalt = Hkdf.X3dhIdentitySalt(senderIdentityKey, ikBPub);
            var info = pqUsed ? "RedePQXDH" : "RedeX3DH";
            var sharedSecret = Hkdf.DeriveKey(dhConcat, x3dhSalt, info, 32);
            return new X3dhRespondResult(sharedSecret);
        }
        finally
        {
            if (dh1 is not null) CryptoService.ZeroOut(dh1);
            if (dh2 is not null) CryptoService.ZeroOut(dh2);
            if (dh3 is not null) CryptoService.ZeroOut(dh3);
            if (dh4 is not null) CryptoService.ZeroOut(dh4);
            if (dhConcat is not null) CryptoService.ZeroOut(dhConcat);
            if (pqSs is not null) CryptoService.ZeroOut(pqSs);
        }
    }

    /// <summary>HKDF input concatenation: DH1 || DH2 || DH3 [|| DH4] [|| SS_PQ].</summary>
    private static byte[] ConcatDhParts(byte[] dh1, byte[] dh2, byte[] dh3, byte[]? dh4, byte[]? pqSs)
    {
        int len = 96 + (dh4 is not null ? 32 : 0) + (pqSs is not null ? 32 : 0);
        var buf = new byte[len];
        int off = 0;
        Buffer.BlockCopy(dh1, 0, buf, off, 32); off += 32;
        Buffer.BlockCopy(dh2, 0, buf, off, 32); off += 32;
        Buffer.BlockCopy(dh3, 0, buf, off, 32); off += 32;
        if (dh4 is not null) { Buffer.BlockCopy(dh4, 0, buf, off, 32); off += 32; }
        if (pqSs is not null) { Buffer.BlockCopy(pqSs, 0, buf, off, 32); }
        return buf;
    }
}
