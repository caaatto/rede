using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Rede.Core.Crypto;

/// <summary>
/// ML-KEM-768 (FIPS 203) wrapper for PQXDH hybrid handshake.
/// Public: 1184 B, Private: 2400 B, Ciphertext: 1088 B, Shared secret: 32 B.
/// </summary>
public static class PQKem
{
    public const int PublicKeySize = 1184;
    public const int PrivateKeySize = 2400;
    public const int CiphertextSize = 1088;
    public const int SharedSecretSize = 32;

    private static readonly SecureRandom Random = new SecureRandom();

    /// <summary>Generate an ML-KEM-768 keypair.</summary>
    public static (byte[] pub, byte[] priv) GenerateKeyPair()
    {
        var gen = new MLKemKeyPairGenerator();
        gen.Init(new MLKemKeyGenerationParameters(Random, MLKemParameters.ml_kem_768));
        var kp = gen.GenerateKeyPair();
        var pub = ((MLKemPublicKeyParameters)kp.Public).GetEncoded();
        var priv = ((MLKemPrivateKeyParameters)kp.Private).GetEncoded();
        return (pub, priv);
    }

    /// <summary>
    /// Encapsulate against a peer's public key.
    /// Returns (ciphertext, sharedSecret). Send ciphertext to peer; keep sharedSecret.
    /// </summary>
    public static (byte[] ciphertext, byte[] sharedSecret) Encapsulate(byte[] publicKey)
    {
        if (publicKey is null || publicKey.Length != PublicKeySize)
            throw new ArgumentException($"ML-KEM public key must be {PublicKeySize} bytes", nameof(publicKey));
        var pub = MLKemPublicKeyParameters.FromEncoding(MLKemParameters.ml_kem_768, publicKey);
        var enc = new MLKemEncapsulator(MLKemParameters.ml_kem_768);
        enc.Init(pub);
        var ct = new byte[enc.EncapsulationLength];
        var ss = new byte[enc.SecretLength];
        enc.Encapsulate(ct, 0, ct.Length, ss, 0, ss.Length);
        return (ct, ss);
    }

    /// <summary>
    /// Decapsulate a ciphertext with our private key. Returns the shared secret.
    /// </summary>
    public static byte[] Decapsulate(byte[] privateKey, byte[] ciphertext)
    {
        if (privateKey is null || privateKey.Length != PrivateKeySize)
            throw new ArgumentException($"ML-KEM private key must be {PrivateKeySize} bytes", nameof(privateKey));
        if (ciphertext is null || ciphertext.Length != CiphertextSize)
            throw new ArgumentException($"ML-KEM ciphertext must be {CiphertextSize} bytes", nameof(ciphertext));
        var priv = MLKemPrivateKeyParameters.FromEncoding(MLKemParameters.ml_kem_768, privateKey);
        var dec = new MLKemDecapsulator(MLKemParameters.ml_kem_768);
        dec.Init(priv);
        var ss = new byte[dec.SecretLength];
        dec.Decapsulate(ciphertext, 0, ciphertext.Length, ss, 0, ss.Length);
        return ss;
    }
}
