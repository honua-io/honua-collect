using System.Globalization;
using Honua.Collect.Core.Maps;

namespace Honua.Collect.Core.Tests.Maps;

public class OsmTileUrlTests
{
    [Fact]
    public void For_builds_the_xyz_tile_url()
        => Assert.Equal(
            "https://tile.openstreetmap.org/14/8192/5461.png",
            OsmTileUrl.For(14, 8192, 5461));

    [Fact]
    public void For_handles_zero_coordinates()
        => Assert.Equal("https://tile.openstreetmap.org/0/0/0.png", OsmTileUrl.For(0, 0, 0));

    [Fact]
    public void For_is_culture_invariant()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            // A culture with a non-ASCII digit grouping must not leak into the URL.
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("https://tile.openstreetmap.org/12/1234/5678.png", OsmTileUrl.For(12, 1234, 5678));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void CacheKey_is_z_x_y_slash_separated()
        => Assert.Equal("14/8192/5461", OsmTileUrl.CacheKey(14, 8192, 5461));

    [Fact]
    public void CacheKey_is_deterministic_for_the_same_coordinate()
        => Assert.Equal(OsmTileUrl.CacheKey(3, 4, 5), OsmTileUrl.CacheKey(3, 4, 5));

    [Fact]
    public void CacheKey_differs_per_coordinate()
    {
        Assert.NotEqual(OsmTileUrl.CacheKey(3, 4, 5), OsmTileUrl.CacheKey(3, 5, 4));
        Assert.NotEqual(OsmTileUrl.CacheKey(3, 4, 5), OsmTileUrl.CacheKey(4, 4, 5));
    }
}
