using Honua.Collect.Core.Editions;

namespace Honua.Collect.Core.Tests.Editions;

public class CollectFeaturesTests
{
    [Theory]
    // Community unlocks none of the gated features.
    [InlineData(CollectEdition.Community, CollectFeature.ReportsAndExports, false)]
    [InlineData(CollectEdition.Community, CollectFeature.EnterpriseAuthAndAdmin, false)]
    // Pro unlocks the three Pro features but not Enterprise.
    [InlineData(CollectEdition.Pro, CollectFeature.ReportsAndExports, true)]
    [InlineData(CollectEdition.Pro, CollectFeature.AiAssistedCapture, true)]
    [InlineData(CollectEdition.Pro, CollectFeature.AdvancedSyncAndGis, true)]
    [InlineData(CollectEdition.Pro, CollectFeature.EnterpriseAuthAndAdmin, false)]
    // Enterprise unlocks everything.
    [InlineData(CollectEdition.Enterprise, CollectFeature.ReportsAndExports, true)]
    [InlineData(CollectEdition.Enterprise, CollectFeature.EnterpriseAuthAndAdmin, true)]
    public void Includes_respects_minimum_edition(
        CollectEdition edition, CollectFeature feature, bool expected)
        => Assert.Equal(expected, edition.Includes(feature));

    [Fact]
    public void Every_feature_has_a_minimum_edition()
    {
        foreach (var feature in Enum.GetValues<CollectFeature>())
        {
            Assert.True(
                CollectFeatures.MinimumEdition.ContainsKey(feature),
                $"{feature} is missing from the tier matrix.");
        }
    }
}
