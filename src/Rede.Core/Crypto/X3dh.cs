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

    /// <summary>Public wire format — base64 strings for JSON server transport.</summary>
    public record PreKeyBundlePublic(
        string SignedPreKey,
        string SignedPreKeySig,
        List<OneTimePreKeyPublic> OneTimePreKeys
    );

    public record OneTimePreKeyPublic(int Id, string Key);

    /// <summary>Private parts — byte[] for storage in Profile, zeroable.</summary>
    public record PreKeyBundlePrivate(
        DoubleRatchet.KeyPairBytes SignedPreKey,
        List<OneTimePreKeyPrivate> OneTimePreKeys
    );

    public record OneTimePreKeyPrivate(int Id, byte[] PublicKey, byte[] SecretKey);

    /// <summary>Recipient bundle with byte[] key material (decoded once at protocol boundary).</summary>
    public record RecipientBundle(
        byte[] IdentityKey,
        byte[] SignedPreKey,
        byte[] SignedPreKeySig,
        byte[] SigningKey,
        OneTimePreKeyBytes? OneTimePreKey
    );

    public record OneTimePreKeyBytes(int Id, byte[] Key);

    public record X3dhInitiateResult(byte[] SharedSecret, byte[] EphemeralPublic, int? UsedOtpkId);
    public record X3dhRespondResult(byte[] SharedSecret);

    /// <summary>Generate pre-key bundle for server upload.</summary>
    public static PreKeyBundle GeneratePreKeyBundle(byte[] signingSecretKey)
    {
        // Signed pre-key
        var spk = PublicKeyBox.GenerateKeyPair();
        var spkPub = (byte[])spk.PublicKey.Clone();
        var spkSec = (byte[])spk.PrivateKey.Clone();
        CryptoService.ZeroOut(spk.PrivateKey);

        var spkSigB64 = CryptoService.SignBytesB64(spkPub, signingSecretKey);

        // One-time pre-keys
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

        return new PreKeyBundle(
            new PreKeyBundlePublic(Convert.ToBase64String(spkPub), spkSigB64, otpksPublic),
            new PreKeyBundlePrivate(
                new DoubleRatchet.KeyPairBytes(spkPub, spkSec),
                otpksPrivate
            )
        );
    }

    /// <summary>Initiator side of X3DH.</summary>
    public static X3dhInitiateResult? Initiate(byte[] senderIdentitySecret, RecipientBundle recipientBundle)
    {
        // Validate key lengths
        if (recipientBundle.IdentityKey.Length != 32) return null;
        if (recipientBundle.SignedPreKey.Length != 32) return null;
        if (recipientBundle.SigningKey.Length != 32) return null;
        if (recipientBundle.OneTimePreKey is not null && recipientBundle.OneTimePreKey.Key.Length != 32) return null;

        // Verify signed pre-key signature
        if (!CryptoService.Verify(recipientBundle.SignedPreKey, recipientBundle.SignedPreKeySig, recipientBundle.SigningKey))
            return null;

        byte[]? dh1 = null, dh2 = null, dh3 = null, dh4 = null, dhConcat = null;
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

            if (recipientBundle.OneTimePreKey is not null)
            {
                dh4 = CryptoService.Dh(ekSecretCopy, recipientBundle.OneTimePreKey.Key);
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

            var x3dhSalt = Hkdf.X3dhIdentitySalt(ikAPub, recipientBundle.IdentityKey);
            var sharedSecret = Hkdf.DeriveKey(dhConcat, x3dhSalt, "RedeX3DH", 32);
            var ephemeralPublic = (byte[])ek.PublicKey.Clone();

            return new X3dhInitiateResult(sharedSecret, ephemeralPublic, usedOtpkId);
        }
        finally
        {
            if (dh1 is not null) CryptoService.ZeroOut(dh1);
            if (dh2 is not null) CryptoService.ZeroOut(dh2);
            if (dh3 is not null) CryptoService.ZeroOut(dh3);
            if (dh4 is not null) CryptoService.ZeroOut(dh4);
            if (dhConcat is not null) CryptoService.ZeroOut(dhConcat);
            if (ekSecretCopy is not null) CryptoService.ZeroOut(ekSecretCopy);
            if (ek is not null) CryptoService.ZeroOut(ek.PrivateKey);
        }
    }

    /// <summary>Responder side of X3DH.</summary>
    public static X3dhRespondResult? Respond(
        byte[] recipientIdentitySecret,
        byte[] signedPreKeySecret,
        byte[]? oneTimePreKeySecret,
        byte[] senderIdentityKey,
        byte[] senderEphemeralKey)
    {
        if (senderIdentityKey.Length != 32) return null;
        if (senderEphemeralKey.Length != 32) return null;

        byte[]? dh1 = null, dh2 = null, dh3 = null, dh4 = null, dhConcat = null;
        try
        {
            var ikBPub = CryptoService.PublicKeyFromSecret(recipientIdentitySecret);

            dh1 = CryptoService.Dh(signedPreKeySecret, senderIdentityKey);
            dh2 = CryptoService.Dh(recipientIdentitySecret, senderEphemeralKey);
            dh3 = CryptoService.Dh(signedPreKeySecret, senderEphemeralKey);

            if (oneTimePreKeySecret is not null)
            {
                dh4 = CryptoService.Dh(oneTimePreKeySecret, senderEphemeralKey);
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

            var x3dhSalt = Hkdf.X3dhIdentitySalt(senderIdentityKey, ikBPub);
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
        }
    }
}
