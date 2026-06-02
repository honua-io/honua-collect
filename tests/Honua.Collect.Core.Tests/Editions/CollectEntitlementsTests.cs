using Honua.Collect.Core.Editions;

namespace Honua.Collect.Core.Tests.Editions;

public class CollectEntitlementsTests
{
    [Fact]
    public void Community_unlocks_no_gated_features()
    {
        var entitlements = CollectEntitlements.Community;

        Assert.False(entitlements.Allows(CollectFeature.ReportsAndExports));
        Assert.False(entitlements.Allows(CollectFeature.AiAssistedCapture));
        Assert.False(entitlements.Allows(CollectFeature.AdvancedSyncAndGis));
        Assert.False(entitlements.Allows(CollectFeature.EnterpriseAuthAndAdmin));
    }

    [Fact]
    public void Pro_unlocks_pro_features_but_not_enterprise()
    {
        var pro = new CollectEntitlements(CollectEdition.Pro);

        Assert.True(pro.Allows(CollectFeature.ReportsAndExports));
        Assert.True(pro.Allows(CollectFeature.AdvancedSyncAndGis));
        Assert.False(pro.Allows(CollectFeature.EnterpriseAuthAndAdmin));
    }

    [Fact]
    public void Enterprise_unlocks_everything()
    {
        var ent = new CollectEntitlements(CollectEdition.Enterprise);

        Assert.True(ent.Allows(CollectFeature.ReportsAndExports));
        Assert.True(ent.Allows(CollectFeature.EnterpriseAuthAndAdmin));
    }

    [Fact]
    public void Require_throws_with_the_required_edition_when_not_entitled()
    {
        var ex = Assert.Throws<FeatureNotEntitledException>(
            () => CollectEntitlements.Community.Require(CollectFeature.ReportsAndExports));

        Assert.Equal(CollectFeature.ReportsAndExports, ex.Feature);
        Assert.Equal(CollectEdition.Community, ex.CurrentEdition);
        Assert.Equal(CollectEdition.Pro, ex.RequiredEdition);
    }

    [Fact]
    public void Require_passes_silently_when_entitled()
    {
        var pro = new CollectEntitlements(CollectEdition.Pro);
        pro.Require(CollectFeature.ReportsAndExports); // does not throw
    }
}
