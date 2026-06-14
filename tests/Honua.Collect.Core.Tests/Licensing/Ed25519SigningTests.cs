using System.Text;
using Honua.Collect.Core.Licensing;

namespace Honua.Collect.Core.Tests.Licensing;

public class Ed25519SigningTests
{
    [Fact]
    public void Sign_then_verify_round_trips()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var message = Encoding.UTF8.GetBytes("the quick brown fox");

        var signature = Ed25519Signing.Sign(message, priv);

        Assert.Equal(Ed25519Signing.SignatureSize, signature.Length);
        Assert.True(Ed25519Signing.Verify(message, signature, pub));
    }

    [Fact]
    public void Verify_fails_for_a_modified_message()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var signature = Ed25519Signing.Sign(Encoding.UTF8.GetBytes("original"), priv);

        Assert.False(Ed25519Signing.Verify(Encoding.UTF8.GetBytes("modified"), signature, pub));
    }

    [Fact]
    public void Verify_fails_for_a_different_key()
    {
        var (priv, _) = Ed25519Signing.GenerateKeyPair();
        var (_, otherPub) = Ed25519Signing.GenerateKeyPair();
        var message = Encoding.UTF8.GetBytes("data");
        var signature = Ed25519Signing.Sign(message, priv);

        Assert.False(Ed25519Signing.Verify(message, signature, otherPub));
    }

    [Fact]
    public void Verify_returns_false_for_wrong_sized_inputs()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var message = Encoding.UTF8.GetBytes("data");
        var signature = Ed25519Signing.Sign(message, priv);

        Assert.False(Ed25519Signing.Verify(message, signature, new byte[10]));
        Assert.False(Ed25519Signing.Verify(message, new byte[10], pub));
    }

    [Fact]
    public void PublicKeyFromPrivate_matches_generated_public_key()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        Assert.Equal(pub, Ed25519Signing.PublicKeyFromPrivate(priv));
    }

    [Fact]
    public void Methods_guard_null_arguments()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        Assert.Throws<ArgumentNullException>(() => Ed25519Signing.Sign(null!, priv));
        Assert.Throws<ArgumentNullException>(() => Ed25519Signing.Sign([1], null!));
        Assert.Throws<ArgumentNullException>(() => Ed25519Signing.Verify(null!, [1], pub));
        Assert.Throws<ArgumentNullException>(() => Ed25519Signing.PublicKeyFromPrivate(null!));
    }
}
