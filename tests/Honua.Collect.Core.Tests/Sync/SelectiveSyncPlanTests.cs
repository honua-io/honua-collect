using Honua.Collect.Core.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

public class SelectiveSyncPlanTests
{
    [Fact]
    public void SyncEverything_includes_any_layer_and_record()
    {
        var plan = SelectiveSyncPlan.SyncEverything;

        Assert.True(plan.IncludesLayer("anything"));
        Assert.True(plan.IncludesRecord("anything", null));
        Assert.True(plan.IncludesRecord("anything", new FieldGeoPoint(0, 0)));
    }

    [Fact]
    public void Unlisted_layers_are_excluded_by_default()
    {
        var plan = new SelectiveSyncPlan([new LayerSyncScope { LayerKey = "trees" }]);

        Assert.True(plan.IncludesLayer("trees"));
        Assert.False(plan.IncludesLayer("roads"));
        Assert.False(plan.IncludesRecord("roads", new FieldGeoPoint(0, 0)));
    }

    [Fact]
    public void Disabled_layer_never_syncs()
    {
        var plan = new SelectiveSyncPlan([new LayerSyncScope { LayerKey = "trees", Enabled = false }]);

        Assert.False(plan.IncludesLayer("trees"));
        Assert.False(plan.IncludesRecord("trees", new FieldGeoPoint(0, 0)));
    }

    [Fact]
    public void Whole_layer_scope_includes_records_regardless_of_location()
    {
        var plan = new SelectiveSyncPlan([new LayerSyncScope { LayerKey = "trees" }]);

        Assert.True(plan.IncludesRecord("trees", null));
        Assert.True(plan.IncludesRecord("trees", new FieldGeoPoint(48, 2)));
    }

    [Fact]
    public void Area_restricted_layer_includes_only_records_inside_an_area()
    {
        var plan = new SelectiveSyncPlan(
        [
            new LayerSyncScope
            {
                LayerKey = "trees",
                Areas = [new SyncAreaBounds(45, -123, 46, -122)],
            },
        ]);

        Assert.True(plan.IncludesRecord("trees", new FieldGeoPoint(45.5, -122.5))); // inside
        Assert.False(plan.IncludesRecord("trees", new FieldGeoPoint(40, -122.5)));  // outside
        Assert.False(plan.IncludesRecord("trees", null));                           // unplaceable
    }

    [Fact]
    public void Record_matches_when_inside_any_of_several_areas()
    {
        var plan = new SelectiveSyncPlan(
        [
            new LayerSyncScope
            {
                LayerKey = "trees",
                Areas =
                [
                    new SyncAreaBounds(45, -123, 46, -122),
                    new SyncAreaBounds(0, 0, 1, 1),
                ],
            },
        ]);

        Assert.True(plan.IncludesRecord("trees", new FieldGeoPoint(0.5, 0.5)));
    }

    [Fact]
    public void Bounds_contains_is_inclusive_of_edges()
    {
        var bounds = new SyncAreaBounds(45, -123, 46, -122);

        Assert.True(bounds.Contains(new FieldGeoPoint(45, -123)));  // corner
        Assert.True(bounds.Contains(new FieldGeoPoint(46, -122)));  // opposite corner
        Assert.False(bounds.Contains(new FieldGeoPoint(46.0001, -122)));
    }
}
