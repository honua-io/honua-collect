using Honua.Collect.Core.Maps;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Maps;

public class OfflineAreaPlannerTests
{
    [Fact]
    public void Plan_count_matches_TilesForArea()
    {
        var bbox = new GeoBoundingBox(21.20, -157.95, 21.40, -157.65);
        var plan = OfflineAreaPlanner.Plan(bbox, 12, 15);

        Assert.Equal(TileCache.TilesForArea(bbox, 12, 15).Count, plan.Count);
        Assert.Equal(plan.Tiles.Count, plan.Count);
    }

    [Fact]
    public void Plan_flags_areas_over_the_cap()
    {
        var bbox = new GeoBoundingBox(21.20, -157.95, 21.40, -157.65);

        var plan = OfflineAreaPlanner.Plan(bbox, 12, 18, maxTiles: 10);

        Assert.True(plan.Count > 10);
        Assert.True(plan.ExceedsCap);
        Assert.Equal(10, plan.MaxTiles);
    }

    [Fact]
    public void Plan_within_cap_does_not_flag()
    {
        var bbox = new GeoBoundingBox(21.30, -157.82, 21.32, -157.80);

        var plan = OfflineAreaPlanner.Plan(bbox, 12, 13, maxTiles: OfflineAreaPlanner.DefaultMaxTiles);

        Assert.False(plan.ExceedsCap);
        Assert.True(plan.Count >= 1);
    }

    [Fact]
    public void Plan_rejects_non_positive_cap()
    {
        var bbox = new GeoBoundingBox(0.0, 0.0, 1.0, 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => OfflineAreaPlanner.Plan(bbox, 1, 2, maxTiles: 0));
    }

    [Fact]
    public void BoundingBox_from_corners_normalises_order()
    {
        var a = new FieldGeoPoint(21.40, -157.65);
        var b = new FieldGeoPoint(21.20, -157.95);

        var box = GeoBoundingBox.FromCorners(a, b);

        Assert.Equal(21.20, box.South);
        Assert.Equal(-157.95, box.West);
        Assert.Equal(21.40, box.North);
        Assert.Equal(-157.65, box.East);
    }

    [Fact]
    public void BoundingBox_rejects_inverted_edges()
    {
        Assert.Throws<ArgumentException>(() => new GeoBoundingBox(10.0, 0.0, 5.0, 1.0));
    }
}
