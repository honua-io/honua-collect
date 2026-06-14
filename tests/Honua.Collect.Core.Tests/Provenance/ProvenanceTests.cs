using System.Text;
using Honua.Collect.Core.Licensing;
using Honua.Collect.Core.Provenance;

namespace Honua.Collect.Core.Tests.Provenance;

public class ProvenanceTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 6, 14, 9, 30, 0, TimeSpan.Zero);
    private static readonly byte[] Photo = Encoding.UTF8.GetBytes("...pretend JPEG bytes...");

    private static SignedProvenance SignSample(byte[] priv, byte[]? content = null) => ProvenanceSigner.Sign(
        ContentHash.Sha256Hex(content ?? Photo),
        CapturedAt,
        capturingUserId: "field-user-7",
        deviceId: "device-abc",
        privateKey: priv,
        latitude: 48.1173,
        longitude: 11.51667,
        accuracyMeters: 0.03);

    // --- content hash ---------------------------------------------------------

    [Fact]
    public void Sha256Hex_is_lowercase_64_chars_and_stable()
    {
        var hash = ContentHash.Sha256Hex(Photo);

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.Equal(hash, ContentHash.Sha256Hex(Photo)); // deterministic
        Assert.True(ContentHash.Matches(Photo, hash));
        Assert.False(ContentHash.Matches(Encoding.UTF8.GetBytes("tampered"), hash));
    }

    // --- canonical encoding ---------------------------------------------------

    [Fact]
    public void Canonical_bytes_are_deterministic_and_sensitive_to_every_field()
    {
        var baseManifest = SignSample(Ed25519Signing.GenerateKeyPair().PrivateKey).Manifest;

        Assert.Equal(baseManifest.ToCanonicalBytes(), baseManifest.ToCanonicalBytes());
        Assert.NotEqual(baseManifest.ToCanonicalBytes(), (baseManifest with { DeviceId = "other" }).ToCanonicalBytes());
        Assert.NotEqual(baseManifest.ToCanonicalBytes(), (baseManifest with { Latitude = 0 }).ToCanonicalBytes());
        Assert.NotEqual(baseManifest.ToCanonicalBytes(), (baseManifest with { Latitude = null }).ToCanonicalBytes());
    }

    // --- sign + verify --------------------------------------------------------

    [Fact]
    public void Signed_provenance_verifies_against_the_signing_key()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var signed = SignSample(priv);

        Assert.True(ProvenanceVerifier.VerifySignature(signed, pub).IsValid);
        Assert.Equal(ProvenanceStatus.Valid, ProvenanceVerifier.Verify(signed, Photo, pub).Status);
    }

    [Fact]
    public void A_different_key_does_not_verify()
    {
        var (priv, _) = Ed25519Signing.GenerateKeyPair();
        var (_, otherPub) = Ed25519Signing.GenerateKeyPair();
        var signed = SignSample(priv);

        Assert.Equal(ProvenanceStatus.SignatureInvalid, ProvenanceVerifier.VerifySignature(signed, otherPub).Status);
    }

    [Fact]
    public void Tampered_metadata_breaks_the_signature()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var signed = SignSample(priv);

        // Move the recorded location after signing.
        var relocated = signed with { Manifest = signed.Manifest with { Latitude = 51.5, Longitude = -0.12 } };

        Assert.Equal(ProvenanceStatus.SignatureInvalid, ProvenanceVerifier.VerifySignature(relocated, pub).Status);
    }

    [Fact]
    public void Altered_media_is_detected_as_content_mismatch_even_with_a_valid_signature()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var signed = SignSample(priv); // hash bound to Photo

        var edited = Encoding.UTF8.GetBytes("...edited JPEG bytes...");
        var result = ProvenanceVerifier.Verify(signed, edited, pub);

        Assert.Equal(ProvenanceStatus.ContentMismatch, result.Status);
        Assert.False(result.IsValid);
        // The signature itself is still valid — only the content moved.
        Assert.True(ProvenanceVerifier.VerifySignature(signed, pub).IsValid);
    }

    [Theory]
    [InlineData("not-base64-!!")]
    [InlineData("")]
    public void Malformed_signature_is_reported(string signature)
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var signed = SignSample(priv) with { SignatureBase64 = signature };

        Assert.Equal(ProvenanceStatus.Malformed, ProvenanceVerifier.VerifySignature(signed, pub).Status);
    }

    [Fact]
    public void Verifier_guards_null_arguments()
    {
        var pub = Ed25519Signing.GenerateKeyPair().PublicKey;
        Assert.Throws<ArgumentNullException>(() => ProvenanceVerifier.VerifySignature(SignSample(Ed25519Signing.GenerateKeyPair().PrivateKey), null!));
        Assert.Throws<ArgumentNullException>(() => ProvenanceVerifier.Verify(null, null!, pub));
    }

    [Fact]
    public void Signer_validates_required_fields()
    {
        var priv = Ed25519Signing.GenerateKeyPair().PrivateKey;
        Assert.Throws<ArgumentException>(() =>
            ProvenanceSigner.Sign("  ", CapturedAt, "u", "d", priv));
        Assert.Throws<ArgumentException>(() =>
            ProvenanceSigner.Sign("hash", CapturedAt, "", "d", priv));
        Assert.Throws<ArgumentNullException>(() =>
            ProvenanceSigner.Sign((CaptureProvenance)null!, priv));
    }
}
