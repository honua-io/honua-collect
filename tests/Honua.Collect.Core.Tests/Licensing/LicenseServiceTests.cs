using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Licensing;

namespace Honua.Collect.Core.Tests.Licensing;

public class LicenseServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = Now;

        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private static LicenseClaims Claims(CollectEdition edition, TimeSpan validFor, bool trial = false) => new()
    {
        Edition = edition,
        Customer = "Acme",
        LicenseId = "lic-1",
        IssuedAtUtc = Now,
        ExpiresAtUtc = Now + validFor,
        IsTrial = trial,
    };

    private static (LicenseService service, TestClock clock, byte[] priv) Build()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var clock = new TestClock();
        return (new LicenseService(pub, clock), clock, priv);
    }

    [Fact]
    public void Starts_on_the_community_baseline()
    {
        var (service, _, _) = Build();

        Assert.Equal(CollectEdition.Community, service.Entitlements.Edition);
        Assert.Null(service.ActiveLicense);
        Assert.False(service.Entitlements.Allows(CollectFeature.ReportsAndExports));
    }

    [Fact]
    public void Applying_a_valid_pro_key_unlocks_pro_features_and_raises_changed()
    {
        var (service, _, priv) = Build();
        var raised = 0;
        service.Changed += (_, _) => raised++;

        var status = service.Apply(LicenseKey.Issue(Claims(CollectEdition.Pro, TimeSpan.FromDays(365)), priv));

        Assert.Equal(LicenseStatus.Valid, status);
        Assert.Equal(CollectEdition.Pro, service.Entitlements.Edition);
        Assert.True(service.Entitlements.Allows(CollectFeature.ReportsAndExports));
        Assert.False(service.Entitlements.Allows(CollectFeature.EnterpriseAuthAndAdmin));
        Assert.NotNull(service.ActiveLicense);
        Assert.Equal("Acme", service.ActiveLicense!.Customer);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Upgrade_then_downgrade_tracks_edition_and_notifies()
    {
        var (service, _, priv) = Build();
        var raised = 0;
        service.Changed += (_, _) => raised++;

        service.Apply(LicenseKey.Issue(Claims(CollectEdition.Pro, TimeSpan.FromDays(365)), priv));
        service.Apply(LicenseKey.Issue(Claims(CollectEdition.Enterprise, TimeSpan.FromDays(365)), priv));
        Assert.Equal(CollectEdition.Enterprise, service.Entitlements.Edition);
        Assert.True(service.Entitlements.Allows(CollectFeature.EnterpriseAuthAndAdmin));

        service.Clear();
        Assert.Equal(CollectEdition.Community, service.Entitlements.Edition);
        Assert.Null(service.ActiveLicense);
        Assert.Equal(3, raised); // community→pro, pro→enterprise, enterprise→community
    }

    [Fact]
    public void A_tampered_or_garbage_key_stays_on_community()
    {
        var (service, _, _) = Build();

        var status = service.Apply("HLIC1.garbage.signature");

        Assert.Equal(LicenseStatus.Malformed, status);
        Assert.Equal(CollectEdition.Community, service.Entitlements.Edition);
    }

    [Fact]
    public void An_expired_key_does_not_grant_entitlements()
    {
        var (service, _, priv) = Build();
        // Coherent window entirely in the past (issued 10d ago, expired 1d ago).
        var expired = new LicenseClaims
        {
            Edition = CollectEdition.Pro,
            Customer = "Acme",
            LicenseId = "lic-1",
            IssuedAtUtc = Now.AddDays(-10),
            ExpiresAtUtc = Now.AddDays(-1),
        };

        var status = service.Apply(LicenseKey.Issue(expired, priv));

        Assert.Equal(LicenseStatus.Expired, status);
        Assert.Equal(CollectEdition.Community, service.Entitlements.Edition);
    }

    [Fact]
    public void A_trial_downgrades_to_community_when_it_expires()
    {
        var (service, clock, priv) = Build();
        service.Apply(LicenseKey.Issue(Claims(CollectEdition.Pro, TimeSpan.FromDays(14), trial: true), priv));
        Assert.Equal(CollectEdition.Pro, service.Entitlements.Edition);
        Assert.True(service.ActiveLicense!.IsTrial);

        var raised = 0;
        service.Changed += (_, _) => raised++;

        // Clock crosses the trial expiry; re-evaluation downgrades.
        clock.Utc = Now.AddDays(15);
        var status = service.Refresh();

        Assert.Equal(LicenseStatus.Expired, status);
        Assert.Equal(CollectEdition.Community, service.Entitlements.Edition);
        Assert.Null(service.ActiveLicense);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Default_service_uses_the_embedded_authority_key_and_rejects_untrusted_tokens()
    {
        var service = new LicenseService(); // embedded LicenseAuthority.PublicKey
        var (foreignPriv, _) = Ed25519Signing.GenerateKeyPair();

        var status = service.Apply(LicenseKey.Issue(Claims(CollectEdition.Enterprise, TimeSpan.FromDays(365)), foreignPriv));

        Assert.Equal(LicenseStatus.InvalidSignature, status);
        Assert.Equal(CollectEdition.Community, service.Entitlements.Edition);
        Assert.Equal(32, LicenseAuthority.PublicKey.Length);
    }
}
