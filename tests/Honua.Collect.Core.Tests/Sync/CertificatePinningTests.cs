using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Honua.Collect.Core.Sync;

namespace Honua.Collect.Core.Tests.Sync;

public class CertificatePinningTests
{
    private static X509Certificate2 SelfSigned(string cn = "honua-test")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void ComputeSpkiPin_is_stable_and_keyed_to_the_public_key()
    {
        using var cert = SelfSigned();

        var a = CertificatePinning.ComputeSpkiPin(cert);
        var b = CertificatePinning.ComputeSpkiPin(cert);

        Assert.Equal(a, b);                       // deterministic
        Assert.Equal(44, a.Length);               // base64 of a 32-byte SHA-256
        using var other = SelfSigned("different");
        Assert.NotEqual(a, CertificatePinning.ComputeSpkiPin(other)); // different key → different pin
    }

    [Fact]
    public void No_pins_configured_means_no_callback_and_platform_validation()
    {
        Assert.Null(CertificatePinning.CreateValidationCallback(Array.Empty<string>()));
        using var cert = SelfSigned();
        // With no pins, a clean chain is trusted; a flagged chain is not.
        Assert.True(CertificatePinning.IsTrusted(cert, SslPolicyErrors.None, Array.Empty<string>()));
        Assert.False(CertificatePinning.IsTrusted(cert, SslPolicyErrors.RemoteCertificateNameMismatch, Array.Empty<string>()));
    }

    [Fact]
    public void Matching_pin_on_a_clean_chain_is_trusted()
    {
        using var cert = SelfSigned();
        var pins = new[] { CertificatePinning.ComputeSpkiPin(cert) };

        Assert.True(CertificatePinning.IsTrusted(cert, SslPolicyErrors.None, pins));
    }

    [Fact]
    public void Non_matching_pin_is_rejected_even_on_a_clean_chain()
    {
        using var presented = SelfSigned();
        using var expected = SelfSigned("expected");
        var pins = new[] { CertificatePinning.ComputeSpkiPin(expected) };

        // The chain validates, but the leaf isn't the pinned key — rogue/mis-issued cert.
        Assert.False(CertificatePinning.IsTrusted(presented, SslPolicyErrors.None, pins));
    }

    [Fact]
    public void A_chain_error_is_never_overridden_by_a_matching_pin()
    {
        using var cert = SelfSigned();
        var pins = new[] { CertificatePinning.ComputeSpkiPin(cert) };

        // Pinning is additive: a platform-rejected chain stays rejected.
        Assert.False(CertificatePinning.IsTrusted(cert, SslPolicyErrors.RemoteCertificateChainErrors, pins));
    }

    [Fact]
    public void Pinning_on_but_no_certificate_fails_closed()
    {
        var pins = new[] { "AAAA" };
        Assert.False(CertificatePinning.IsTrusted(null, SslPolicyErrors.None, pins));
    }

    [Fact]
    public void Callback_enforces_the_configured_pin()
    {
        using var cert = SelfSigned();
        var callback = CertificatePinning.CreateValidationCallback(new[] { CertificatePinning.ComputeSpkiPin(cert) });
        Assert.NotNull(callback);

        using var request = new HttpRequestMessage();
        Assert.True(callback!(request, cert, null, SslPolicyErrors.None));

        using var rogue = SelfSigned("rogue");
        Assert.False(callback(request, rogue, null, SslPolicyErrors.None));
    }
}
