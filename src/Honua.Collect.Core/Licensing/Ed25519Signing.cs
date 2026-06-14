using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Crypto.Signers;

namespace Honua.Collect.Core.Licensing;

/// <summary>
/// Minimal Ed25519 sign/verify over BouncyCastle — the primitive under signed
/// license keys. Verification is the only operation the shipping app performs
/// (against the embedded authority public key); signing and key generation exist
/// for the offline licensing authority / issuing tooling and the test suite.
/// Ed25519 keys are raw 32-byte values.
/// </summary>
public static class Ed25519Signing
{
    /// <summary>The fixed length, in bytes, of an Ed25519 public or private key.</summary>
    public const int KeySize = 32;

    /// <summary>The fixed length, in bytes, of an Ed25519 signature.</summary>
    public const int SignatureSize = 64;

    /// <summary>Generates a fresh Ed25519 key pair (issuer tooling / tests).</summary>
    /// <returns>The raw 32-byte private and public keys.</returns>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();
        var priv = (Ed25519PrivateKeyParameters)pair.Private;
        var pub = (Ed25519PublicKeyParameters)pair.Public;
        return (priv.GetEncoded(), pub.GetEncoded());
    }

    /// <summary>Derives the public key for a raw private key.</summary>
    /// <param name="privateKey">The raw 32-byte private key.</param>
    /// <returns>The raw 32-byte public key.</returns>
    public static byte[] PublicKeyFromPrivate(byte[] privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        return new Ed25519PrivateKeyParameters(privateKey, 0).GeneratePublicKey().GetEncoded();
    }

    /// <summary>Signs a message with a raw private key.</summary>
    /// <param name="message">The bytes to sign.</param>
    /// <param name="privateKey">The raw 32-byte private key.</param>
    /// <returns>A 64-byte signature.</returns>
    public static byte[] Sign(byte[] message, byte[] privateKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(privateKey);
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, new Ed25519PrivateKeyParameters(privateKey, 0));
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    /// <summary>
    /// Verifies a signature against a raw public key. Returns false (never throws)
    /// for a malformed key/signature, so callers can treat any failure uniformly as
    /// "untrusted".
    /// </summary>
    /// <param name="message">The signed bytes.</param>
    /// <param name="signature">The candidate signature.</param>
    /// <param name="publicKey">The raw 32-byte public key.</param>
    /// <returns><see langword="true"/> only when the signature is valid for the key.</returns>
    public static bool Verify(byte[] message, byte[] signature, byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(publicKey);
        if (publicKey.Length != KeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or CryptoException)
        {
            return false;
        }
    }
}
