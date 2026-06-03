using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Core.Maps;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Maps;

public class TileCacheTests
{
    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "honua-tilecache-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void PathFor_is_deterministic_and_uses_z_x_y_png_layout()
    {
        var cache = new TileCache("/cache");

        var a = cache.PathFor(12, 654, 1583);
        var b = cache.PathFor(12, 654, 1583);

        Assert.Equal(a, b);
        Assert.Equal(
            Path.Combine("/cache", "12", "654", "1583.png"),
            a);
    }

    [Fact]
    public void Different_tiles_get_different_paths()
    {
        var cache = new TileCache("/cache");

        Assert.NotEqual(cache.PathFor(12, 1, 2), cache.PathFor(12, 2, 1));
        Assert.NotEqual(cache.PathFor(11, 1, 2), cache.PathFor(12, 1, 2));
    }

    [Fact]
    public void Constructor_rejects_empty_root()
    {
        Assert.Throws<ArgumentException>(() => new TileCache(" "));
    }

    [Fact]
    public async Task Save_then_TryGetPath_round_trips_on_disk()
    {
        var root = NewTempRoot();
        try
        {
            var cache = new TileCache(root);
            var bytes = new byte[] { 1, 2, 3, 4, 5 };

            Assert.False(cache.Contains(7, 33, 44));
            Assert.False(cache.TryGetPath(7, 33, 44, out _));

            await cache.SaveAsync(7, 33, 44, bytes);

            Assert.True(cache.Contains(7, 33, 44));
            Assert.True(cache.TryGetPath(7, 33, 44, out var path));
            Assert.Equal(cache.PathFor(7, 33, 44), path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_overwrites_existing_tile()
    {
        var root = NewTempRoot();
        try
        {
            var cache = new TileCache(root);
            await cache.SaveAsync(3, 1, 1, new byte[] { 9 });
            await cache.SaveAsync(3, 1, 1, new byte[] { 7, 7 });

            Assert.True(cache.TryGetPath(3, 1, 1, out var path));
            Assert.Equal(new byte[] { 7, 7 }, await File.ReadAllBytesAsync(path));
            // No stray temp file left behind.
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TilesNeededForViewport_includes_the_centre_tile()
    {
        var center = new FieldGeoPoint(21.31, -157.81); // Honolulu
        const int zoom = 12;

        var expectedCenter = WebMercator.ToTile(center, zoom);
        var tiles = TileCache.TilesNeededForViewport(center, zoom, 800, 600);

        Assert.Contains(new TileCoordinate(zoom, expectedCenter.X, expectedCenter.Y), tiles);
    }

    [Fact]
    public void TilesNeededForViewport_matches_drawable_grid_dimensions()
    {
        var center = new FieldGeoPoint(45.5, -122.6); // Portland
        const int zoom = 14;
        const double w = 1024;
        const double h = 768;

        var tiles = TileCache.TilesNeededForViewport(center, zoom, w, h);

        // Re-derive the expected column/row span exactly as the drawable does.
        const int tile = WebMercator.TileSize;
        var (cx, cy) = WebMercator.ToWorldPixel(center, zoom);
        var originX = cx - w / 2.0;
        var originY = cy - h / 2.0;
        var firstCol = (int)Math.Floor(originX / tile);
        var firstRow = (int)Math.Floor(originY / tile);
        var lastCol = (int)Math.Floor((originX + w) / tile);
        var lastRow = (int)Math.Floor((originY + h) / tile);

        var expectedCount = (lastCol - firstCol + 1) * (lastRow - firstRow + 1);
        Assert.Equal(expectedCount, tiles.Count);
        Assert.All(tiles, t => Assert.Equal(zoom, t.Zoom));
    }

    [Fact]
    public void TilesNeededForViewport_clamps_off_world_tiles()
    {
        // Near the north pole at low zoom: the top of the viewport runs off the
        // world, so negative rows must be dropped, never returned.
        var center = new FieldGeoPoint(WebMercator.MaxLatitude, 0.0);
        var tiles = TileCache.TilesNeededForViewport(center, 1, 1024, 1024);

        var max = (1 << 1) - 1;
        Assert.All(tiles, t =>
        {
            Assert.InRange(t.X, 0, max);
            Assert.InRange(t.Y, 0, max);
        });
    }

    [Fact]
    public void TilesForArea_covers_the_corner_tiles_for_each_zoom()
    {
        var bbox = new GeoBoundingBox(south: 21.20, west: -157.95, north: 21.40, east: -157.65);

        for (var z = 10; z <= 14; z++)
        {
            var tiles = TileCache.TilesForArea(bbox, z, z);

            var nw = WebMercator.ToTile(new FieldGeoPoint(bbox.North, bbox.West), z);
            var se = WebMercator.ToTile(new FieldGeoPoint(bbox.South, bbox.East), z);

            Assert.Contains(new TileCoordinate(z, nw.X, nw.Y), tiles);
            Assert.Contains(new TileCoordinate(z, se.X, se.Y), tiles);

            // Full rectangular span, no gaps or duplicates.
            var minX = Math.Min(nw.X, se.X);
            var maxX = Math.Max(nw.X, se.X);
            var minY = Math.Min(nw.Y, se.Y);
            var maxY = Math.Max(nw.Y, se.Y);
            var expected = (maxX - minX + 1) * (maxY - minY + 1);
            Assert.Equal(expected, tiles.Count);
            Assert.Equal(tiles.Count, tiles.Distinct().Count());
        }
    }

    [Fact]
    public void TilesForArea_spans_the_whole_zoom_range()
    {
        var bbox = new GeoBoundingBox(0.0, 0.0, 1.0, 1.0);
        var tiles = TileCache.TilesForArea(bbox, 5, 8);

        var zooms = tiles.Select(t => t.Zoom).Distinct().OrderBy(z => z).ToArray();
        Assert.Equal(new[] { 5, 6, 7, 8 }, zooms);
    }

    [Fact]
    public void TilesForArea_rejects_inverted_zoom_range()
    {
        var bbox = new GeoBoundingBox(0.0, 0.0, 1.0, 1.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => TileCache.TilesForArea(bbox, 8, 5));
    }
}
