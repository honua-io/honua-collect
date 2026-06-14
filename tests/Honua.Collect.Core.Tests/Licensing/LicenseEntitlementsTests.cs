using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Licensing;

namespace Honua.Collect.Core.Tests.Licensing;

public class LicenseEntitlementsTests
{
    [Fact]
    public void Community_baseline_allows_no_gated_features()
    {
        var e = LicenseEntitlements.Community;

        Assert.Equal(CollectEdition.Community, e.Edition);
        Assert.False(e.IsLicensed);
        Assert.Null(e.License);
        Assert.False(e.Allows(CollectFeature.ReportsAndExports));
        Assert.Throws<FeatureNotEntitledException>(() => e.Require(CollectFeature.ReportsAndExports));
    }

    [Fact]
    public void Edition_grants_follow_the_tier_matrix()
    {
        var pro = new LicenseEntitlements(CollectEdition.Pro);

        Assert.True(pro.Allows(CollectFeature.ReportsAndExports));
        Assert.True(pro.Allows(CollectFeature.AdvancedSyncAndGis));
        Assert.False(pro.Allows(CollectFeature.EnterpriseAuthAndAdmin));
        pro.Require(CollectFeature.AiAssistedCapture); // does not throw
    }

    [Fact]
    public void Explicit_feature_grant_adds_a_capability_above_the_tier()
    {
        var addOn = new LicenseEntitlements(
            CollectEdition.Community,
            new HashSet<CollectFeature> { CollectFeature.ReportsAndExports });

        Assert.True(addOn.Allows(CollectFeature.ReportsAndExports)); // granted explicitly
        Assert.False(addOn.Allows(CollectFeature.AiAssistedCapture)); // not granted, tier too low
        addOn.Require(CollectFeature.ReportsAndExports);
    }

    [Fact]
    public void FromClaims_carries_edition_features_and_license()
    {
        var claims = new LicenseClaims
        {
            Edition = CollectEdition.Pro,
            Features = new HashSet<CollectFeature> { CollectFeature.EnterpriseAuthAndAdmin },
            Customer = "Acme",
            LicenseId = "lic-1",
            IssuedAtUtc = DateTimeOffset.UnixEpoch,
            ExpiresAtUtc = DateTimeOffset.UnixEpoch.AddYears(1),
        };

        var e = LicenseEntitlements.FromClaims(claims);

        Assert.True(e.IsLicensed);
        Assert.Same(claims, e.License);
        Assert.True(e.Allows(CollectFeature.EnterpriseAuthAndAdmin)); // explicit add-on on a Pro tier
        Assert.True(e.Allows(CollectFeature.ReportsAndExports)); // from the Pro tier
    }

    [Fact]
    public void FromClaims_guards_null()
    {
        Assert.Throws<ArgumentNullException>(() => LicenseEntitlements.FromClaims(null!));
    }
}
