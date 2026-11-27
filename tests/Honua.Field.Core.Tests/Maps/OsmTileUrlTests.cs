// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Honua.Field.Core.Maps;

namespace Honua.Field.Core.Tests.Maps;

public class OsmTileUrlTests
{
    [Theory]
    [InlineData("https://tiles.example.com/{0}/{1}/{2}.png", false, false)]
    [InlineData("https://tiles.example.com/{0}/{1}/{2}.png", true, true)]
    [InlineData("https://tile.openstreetmap.org/{0}/{1}/{2}.png", true, false)]
    [InlineData("https://a.tile.openstreetmap.org./{0}/{1}/{2}.png", true, false)]
    [InlineData("https://openstreetmap.org/{0}/{1}/{2}.png", true, false)]
    [InlineData("file:///tiles/{0}/{1}/{2}.png", true, false)]
    public void Prefetch_requires_permission_and_a_nonpublic_http_source(string template, bool optIn, bool expected)
        => Assert.Equal(expected, OsmTileUrl.AllowsOfflinePrefetch(template, optIn));

    [Fact]
    public void Alternate_templates_use_distinct_caches_while_default_keeps_the_legacy_path()
    {
        const string root = "tiles";
        const string first = "https://tiles.example.com/base/{0}/{1}/{2}.png";
        const string second = "https://tiles.example.com/imagery/{0}/{1}/{2}.png";
        Assert.Equal(root, OsmTileUrl.CacheDirectory(root, "https://tile.openstreetmap.org/{0}/{1}/{2}.png"));
        Assert.NotEqual(root, OsmTileUrl.CacheDirectory(root, first));
        Assert.NotEqual(OsmTileUrl.CacheDirectory(root, first), OsmTileUrl.CacheDirectory(root, second));
    }

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
