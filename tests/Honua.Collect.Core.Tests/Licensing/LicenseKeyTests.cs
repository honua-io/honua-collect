using System.Text;
using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Licensing;

namespace Honua.Collect.Core.Tests.Licensing;

public class LicenseKeyTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

    private static LicenseClaims Claims(
        CollectEdition edition = CollectEdition.Pro,
        TimeSpan? validFor = null,
        IReadOnlySet<CollectFeature>? features = null,
        bool trial = false) => new()
        {
            Edition = edition,
            Features = features ?? new HashSet<CollectFeature>(),
            Customer = "Acme Field Services",
            LicenseId = "lic-001",
            IssuedAtUtc = Now,
            ExpiresAtUtc = Now + (validFor ?? TimeSpan.FromDays(365)),
            IsTrial = trial,
        };

    [Fact]
    public void Issue_then_verify_round_trips_all_claims()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var features = new HashSet<CollectFeature> { CollectFeature.EnterpriseAuthAndAdmin };
        var token = LicenseKey.Issue(Claims(features: features, trial: true), priv);

        var result = LicenseKey.Verify(token, pub, Now.AddDays(1));

        Assert.Equal(LicenseStatus.Valid, result.Status);
        Assert.True(result.IsValid);
        var c = result.Claims!;
        Assert.Equal(CollectEdition.Pro, c.Edition);
        Assert.Equal("Acme Field Services", c.Customer);
        Assert.Equal("lic-001", c.LicenseId);
        Assert.True(c.IsTrial);
        Assert.Contains(CollectFeature.EnterpriseAuthAndAdmin, c.Features);
    }

    [Fact]
    public void Tampered_payload_fails_signature()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var token = LicenseKey.Issue(Claims(CollectEdition.Community), priv);

        // Flip the payload segment to an Enterprise claim re-encoded without re-signing.
        var forged = LicenseKey.Issue(Claims(CollectEdition.Enterprise), priv).Split('.')[1];
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{forged}.{parts[2]}";

        var result = LicenseKey.Verify(tampered, pub, Now.AddDays(1));

        Assert.Equal(LicenseStatus.InvalidSignature, result.Status);
        Assert.Null(result.Claims);
    }

    [Fact]
    public void A_different_authority_key_is_untrusted()
    {
        var (priv, _) = Ed25519Signing.GenerateKeyPair();
        var (_, otherPub) = Ed25519Signing.GenerateKeyPair();
        var token = LicenseKey.Issue(Claims(), priv);

        Assert.Equal(LicenseStatus.InvalidSignature, LicenseKey.Verify(token, otherPub, Now.AddDays(1)).Status);
    }

    [Fact]
    public void Expired_license_reports_expired_but_still_decodes_claims()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var token = LicenseKey.Issue(Claims(validFor: TimeSpan.FromDays(30)), priv);

        var result = LicenseKey.Verify(token, pub, Now.AddDays(31));

        Assert.Equal(LicenseStatus.Expired, result.Status);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Claims); // signature was valid, so claims are trustworthy
    }

    [Fact]
    public void Not_yet_active_license_reports_not_yet_valid()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var token = LicenseKey.Issue(Claims(), priv);

        var result = LicenseKey.Verify(token, pub, Now.AddDays(-1));

        Assert.Equal(LicenseStatus.NotYetValid, result.Status);
        Assert.NotNull(result.Claims);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("WRONG.aaa.bbb")]
    [InlineData("HLIC1.only-two")]
    [InlineData("HLIC1.@@@notbase64@@@.bbb")]
    public void Malformed_tokens_are_rejected(string? token)
    {
        var pub = Ed25519Signing.GenerateKeyPair().PublicKey;
        Assert.Equal(LicenseStatus.Malformed, LicenseKey.Verify(token, pub, Now).Status);
    }

    [Fact]
    public void Incoherent_validity_window_is_malformed()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var bad = Claims() with { IssuedAtUtc = Now, ExpiresAtUtc = Now.AddDays(-1) };
        var token = LicenseKey.Issue(bad, priv);

        Assert.Equal(LicenseStatus.Malformed, LicenseKey.Verify(token, pub, Now).Status);
    }

    [Fact]
    public void Unknown_feature_name_is_malformed_even_with_a_valid_signature()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        // Hand-craft a signed token whose payload lists a bogus feature.
        var payloadJson = "{\"edition\":1,\"features\":[\"TimeTravel\"],\"customer\":\"x\",\"lid\":\"l\"," +
            "\"iat\":\"2026-06-14T12:00:00+00:00\",\"exp\":\"2027-06-14T12:00:00+00:00\",\"trial\":false}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var signingInput = $"HLIC1.{encoded}";
        var sig = Ed25519Signing.Sign(Encoding.ASCII.GetBytes(signingInput), priv);
        var token = $"{signingInput}.{Convert.ToBase64String(sig).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

        Assert.Equal(LicenseStatus.Malformed, LicenseKey.Verify(token, pub, Now).Status);
    }

    [Fact]
    public void Issue_and_verify_guard_null_arguments()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        Assert.Throws<ArgumentNullException>(() => LicenseKey.Issue(null!, priv));
        Assert.Throws<ArgumentNullException>(() => LicenseKey.Issue(Claims(), null!));
        Assert.Throws<ArgumentNullException>(() => LicenseKey.Verify("x", null!, Now));
    }
}
