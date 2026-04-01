using Sodium;

namespace Rede.Core.Crypto;

/// <summary>
/// X3DH — Extended Triple Diffie-Hellman Key Agreement.
/// Mirrors: x3dhInitiate, x3dhRespond, generatePreKeyBundle in crypto.js
/// </summary>
public static class X3dh
{
    public record PreKeyBundle(
        PreKeyBundlePublic PublicBundle,
        PreKeyBundlePrivate PrivateKeys
    );

    public record PreKeyBundlePublic(
        string SignedPreKey,
        string SignedPreKeySig,
        List<OneTimePreKeyPublic> OneTimePreKeys
    );

    public record OneTimePreKeyPublic(int Id, string Key);

    public record PreKeyBundlePrivate(
        KeyPairB64 SignedPreKey,
        List<OneTimePreKeyPrivate> OneTimePreKeys
    );

    public record OneTimePreKeyPrivate(int Id, string PublicKey, string SecretKey);
    public record KeyPairB64(string PublicKey, string SecretKey);

    public record RecipientBundle(
        string IdentityKey,
        string SignedPreKey,
        string SignedPreKeySig,
        string SigningKey,
        OneTimePreKeyPublic? OneTimePreKey
    );

    public record X3dhInitiateResult(byte[] SharedSecret, string EphemeralPublic, int? UsedOtpkId);
    public record X3dhRespondResult(byte[] SharedSecret);

    /// <summary>
    /// Generate pre-key bundle for server upload.
    /// Mirrors: generatePreKeyBundle(signingSecretKeyB64) in crypto.js
    /// </summary>
    public static PreKeyBundle GeneratePreKeyBundle(string signingSecretKeyB64)
    {
        // Signed pre-key (ephemeral X25519, signed by Ed25519 identity)
        var spk = PublicKeyBox.GenerateKeyPair();
        var spkPub = Convert.ToBase64String(spk.PublicKey);
        var spkSec = Convert.ToBase64String(spk.PrivateKey);
        var spkSig = CryptoService.SignBytes(spk.PublicKey, signingSecretKeyB64);
        CryptoService.ZeroOut(spk.PrivateKey);

        // One-time pre-keys (20 X25519 keypairs)
        var otpksPublic = new List<OneTimePreKeyPublic>();
        var otpksPrivate = new List<OneTimePreKeyPrivate>();
        for (int i = 0; i < 20; i++)
        {
            var kp = PublicKeyBox.GenerateKeyPair();
            otpksPublic.Add(new OneTimePreKeyPublic(i, Convert.ToBase64String(kp.PublicKey)));
            otpksPrivate.Add(new OneTimePreKeyPrivate(i, Convert.ToBase64String(kp.PublicKey), Convert.ToBase64String(kp.PrivateKey)));
            CryptoService.ZeroOut(kp.PrivateKey);
        }

        return new PreKeyBundle(
            new PreKeyBundlePublic(spkPub, spkSig, otpksPublic),
            new PreKeyBundlePrivate(
                new KeyPairB64(spkPub, spkSec),
                otpksPrivate
            )
        );
    }

    /// <summary>
    /// Initiator side of X3DH (Alice sending first message to Bob).
    /// Mirrors: x3dhInitiate(senderIdentitySecretB64, recipientBundle) in crypto.js
    /// </summary>
    public static X3dhInitiateResult? Initiate(string senderIdentitySecretB64, RecipientBundle recipientBundle)
    {
        // M1: Validate key lengths before use
        try
        {
            if (Convert.FromBase64String(recipientBundle.IdentityKey).Length != 32) return null;
            if (Convert.FromBase64String(recipientBundle.SignedPreKey).Length != 32) return null;
            if (Convert.FromBase64String(recipientBundle.SigningKey).Length != 32) return null;
            if (recipientBundle.OneTimePreKey is not null &&
                Convert.FromBase64String(recipientBundle.OneTimePreKey.Key).Length != 32) return null;
        }
        catch { return null; }

        // Verify signed pre-key signature
        var spkBytes = Convert.FromBase64String(recipientBundle.SignedPreKey);
        if (!CryptoService.VerifyBytes(spkBytes, recipientBundle.SignedPreKeySig, recipientBundle.SigningKey))
            return null; // Invalid signature — abort

        // M1: try-finally ensures all secrets are zeroed even on exception
        byte[]? ikA = null, dh1 = null, dh2 = null, dh3 = null, dh4 = null, dhConcat = null;
        KeyPair? ek = null;
        try
        {
            ikA = Convert.FromBase64String(senderIdentitySecretB64);
            var ikB = Convert.FromBase64String(recipientBundle.IdentityKey);
            var spkB = Convert.FromBase64String(recipientBundle.SignedPreKey);
            var ikAPub = CryptoService.PublicKeyFromSecret(ikA);

            ek = PublicKeyBox.GenerateKeyPair();

            dh1 = CryptoService.Dh(ikA, spkB);
            dh2 = CryptoService.Dh(ek.PrivateKey, ikB);
            dh3 = CryptoService.Dh(ek.PrivateKey, spkB);

            int? usedOtpkId = null;

            if (recipientBundle.OneTimePreKey is not null)
            {
                var opkB = Convert.FromBase64String(recipientBundle.OneTimePreKey.Key);
                dh4 = CryptoService.Dh(ek.PrivateKey, opkB);
                dhConcat = new byte[128];
                Buffer.BlockCopy(dh1, 0, dhConcat, 0, 32);
                Buffer.BlockCopy(dh2, 0, dhConcat, 32, 32);
                Buffer.BlockCopy(dh3, 0, dhConcat, 64, 32);
                Buffer.BlockCopy(dh4, 0, dhConcat, 96, 32);
                usedOtpkId = recipientBundle.OneTimePreKey.Id;
            }
            else
            {
                dhConcat = new byte[96];
                Buffer.BlockCopy(dh1, 0, dhConcat, 0, 32);
                Buffer.BlockCopy(dh2, 0, dhConcat, 32, 32);
                Buffer.BlockCopy(dh3, 0, dhConcat, 64, 32);
            }

            var x3dhSalt = Hkdf.X3dhIdentitySalt(ikAPub, ikB);
            var sharedSecret = Hkdf.DeriveKey(dhConcat, x3dhSalt, "RedeX3DH", 32);
            var ephemeralPublic = Convert.ToBase64String(ek.PublicKey);

            return new X3dhInitiateResult(sharedSecret, ephemeralPublic, usedOtpkId);
        }
        finally
        {
            if (dh1 is not null) CryptoService.ZeroOut(dh1);
            if (dh2 is not null) CryptoService.ZeroOut(dh2);
            if (dh3 is not null) CryptoService.ZeroOut(dh3);
            if (dh4 is not null) CryptoService.ZeroOut(dh4);
            if (dhConcat is not null) CryptoService.ZeroOut(dhConcat);
            if (ikA is not null) CryptoService.ZeroOut(ikA);
            if (ek is not null) CryptoService.ZeroOut(ek.PrivateKey);
        }
    }

    /// <summary>
    /// Responder side of X3DH (Bob receiving first message from Alice).
    /// Mirrors: x3dhRespond(...) in crypto.js
    /// </summary>
    public static X3dhRespondResult? Respond(
        string recipientIdentitySecretB64,
        string signedPreKeySecretB64,
        string? oneTimePreKeySecretB64,
        string senderIdentityKeyB64,
        string senderEphemeralKeyB64)
    {
        // H5: Validate sender key lengths before use
        try
        {
            if (Convert.FromBase64String(senderIdentityKeyB64).Length != 32) return null;
            if (Convert.FromBase64String(senderEphemeralKeyB64).Length != 32) return null;
        }
        catch { return null; }

        // M1: try-finally ensures all secrets are zeroed even on exception
        byte[]? ikB = null, spkB = null, dh1 = null, dh2 = null, dh3 = null, dh4 = null;
        byte[]? dhConcat = null, opkB = null;
        try
        {
            ikB = Convert.FromBase64String(recipientIdentitySecretB64);
            spkB = Convert.FromBase64String(signedPreKeySecretB64);
            var ikA = Convert.FromBase64String(senderIdentityKeyB64);
            var ekA = Convert.FromBase64String(senderEphemeralKeyB64);
            var ikBPub = CryptoService.PublicKeyFromSecret(ikB);

            dh1 = CryptoService.Dh(spkB, ikA);
            dh2 = CryptoService.Dh(ikB, ekA);
            dh3 = CryptoService.Dh(spkB, ekA);

            if (oneTimePreKeySecretB64 is not null)
            {
                opkB = Convert.FromBase64String(oneTimePreKeySecretB64);
                dh4 = CryptoService.Dh(opkB, ekA);
                dhConcat = new byte[128];
                Buffer.BlockCopy(dh1, 0, dhConcat, 0, 32);
                Buffer.BlockCopy(dh2, 0, dhConcat, 32, 32);
                Buffer.BlockCopy(dh3, 0, dhConcat, 64, 32);
                Buffer.BlockCopy(dh4, 0, dhConcat, 96, 32);
            }
            else
            {
                dhConcat = new byte[96];
                Buffer.BlockCopy(dh1, 0, dhConcat, 0, 32);
                Buffer.BlockCopy(dh2, 0, dhConcat, 32, 32);
                Buffer.BlockCopy(dh3, 0, dhConcat, 64, 32);
            }

            var x3dhSalt = Hkdf.X3dhIdentitySalt(ikA, ikBPub);
            var sharedSecret = Hkdf.DeriveKey(dhConcat, x3dhSalt, "RedeX3DH", 32);
            return new X3dhRespondResult(sharedSecret);
        }
        finally
        {
            if (dh1 is not null) CryptoService.ZeroOut(dh1);
            if (dh2 is not null) CryptoService.ZeroOut(dh2);
            if (dh3 is not null) CryptoService.ZeroOut(dh3);
            if (dh4 is not null) CryptoService.ZeroOut(dh4);
            if (dhConcat is not null) CryptoService.ZeroOut(dhConcat);
            if (ikB is not null) CryptoService.ZeroOut(ikB);
            if (spkB is not null) CryptoService.ZeroOut(spkB);
            if (opkB is not null) CryptoService.ZeroOut(opkB);
        }
    }
}
