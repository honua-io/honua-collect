using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Core.Sync;

namespace Honua.Collect.Core.Tests.Sync;

public class SyncExtentTests
{
    [Fact]
    public void Contains_is_inclusive_of_edges_in_crs_units()
    {
        // A UTM-style extent in metres — far outside any valid lat/lon range, which
        // is the whole point: these projected easting/northing values would be
        // nonsense if funnelled through a WGS84 lat/lon type (#302).
        var extent = new SyncExtent(500_000, 4_100_000, 510_000, 4_110_000);

        Assert.True(extent.Contains(new PlanarPoint(505_000, 4_105_000)));  // inside
        Assert.True(extent.Contains(new PlanarPoint(500_000, 4_100_000)));  // min corner
        Assert.True(extent.Contains(new PlanarPoint(510_000, 4_110_000)));  // max corner
        Assert.False(extent.Contains(new PlanarPoint(499_999, 4_105_000))); // west of box
        Assert.False(extent.Contains(new PlanarPoint(505_000, 4_110_001))); // north of box
    }

    [Fact]
    public void Corners_are_normalised_regardless_of_orientation()
    {
        var a = new SyncExtent(510_000, 4_110_000, 500_000, 4_100_000); // max,min order
        var b = new SyncExtent(500_000, 4_100_000, 510_000, 4_110_000); // min,max order

        Assert.Equal(b.MinX, a.MinX);
        Assert.Equal(b.MaxY, a.MaxY);
        Assert.True(a.Contains(new PlanarPoint(505_000, 4_105_000)));
    }

    [Fact]
    public void Projected_coordinates_are_not_treated_as_latlon()
    {
        // A point whose projected northing (4,105,000 m) is wildly outside the
        // [-90, 90] latitude band still tests correctly because the extent never
        // re-projects or assumes degrees.
        var extent = new SyncExtent(500_000, 4_100_000, 510_000, 4_110_000);

        Assert.True(extent.Contains(new PlanarPoint(505_000, 4_105_000)));
    }
}
