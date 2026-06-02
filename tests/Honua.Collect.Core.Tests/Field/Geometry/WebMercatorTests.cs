using Honua.Collect.Core.Field.Geometry;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Geometry;

public class WebMercatorTests
{
    [Fact]
    public void MapSize_doubles_each_zoom_level()
    {
        Assert.Equal(256, WebMercator.MapSize(0));
        Assert.Equal(512, WebMercator.MapSize(1));
        Assert.Equal(256 * 1024, WebMercator.MapSize(10));
    }

    [Fact]
    public void Origin_lon_lat_maps_to_top_left_world_pixel()
    {
        // lon -180, the max Mercator latitude -> world pixel (0,0).
        var (x, y) = WebMercator.ToWorldPixel(WebMercator.MaxLatitude, -180.0, 5);
        Assert.Equal(0.0, x, 6);
        Assert.Equal(0.0, y, 3);
    }

    [Fact]
    public void Null_island_maps_to_world_centre()
    {
        // lon/lat (0,0) -> exact centre of the world at any zoom.
        var (x, y) = WebMercator.ToWorldPixel(0.0, 0.0, 8);
        var half = WebMercator.MapSize(8) / 2.0;
        Assert.Equal(half, x, 6);
        Assert.Equal(half, y, 6);
    }

    [Theory]
    [InlineData(21.31, -157.81, 12)]   // Honolulu
    [InlineData(45.5, -122.6, 14)]     // Portland
    [InlineData(-33.87, 151.21, 10)]   // Sydney (southern + eastern hemisphere)
    [InlineData(51.5, -0.12, 16)]      // London (near prime meridian)
    public void WorldPixel_round_trips_back_to_lat_lon(double lat, double lon, int zoom)
    {
        var (x, y) = WebMercator.ToWorldPixel(lat, lon, zoom);
        var back = WebMercator.FromWorldPixel(x, y, zoom);

        Assert.Equal(lat, back.Latitude, 6);
        Assert.Equal(lon, back.Longitude, 6);
    }

    [Fact]
    public void ToTile_matches_known_osm_tile_indices()
    {
        // Honolulu at z12 is OSM tile x=251, y=1799 (slippy-map XYZ reference).
        var (tx, ty) = WebMercator.ToTile(new FieldGeoPoint(21.3069, -157.8583), 12);
        Assert.Equal(251, tx);
        Assert.Equal(1799, ty);
    }

    [Fact]
    public void ToTile_is_clamped_to_valid_range()
    {
        var (tx, ty) = WebMercator.ToTile(new FieldGeoPoint(89.9, 179.9), 3);
        Assert.InRange(tx, 0, 7);
        Assert.InRange(ty, 0, 7);
    }

    [Fact]
    public void Center_point_maps_to_viewport_middle()
    {
        var center = new FieldGeoPoint(21.31, -157.81);
        var (sx, sy) = WebMercator.ToScreen(center, center, 13, 400, 600);

        Assert.Equal(200, sx, 6);
        Assert.Equal(300, sy, 6);
    }

    [Fact]
    public void Screen_mapping_round_trips_through_geographic_space()
    {
        var center = new FieldGeoPoint(21.31, -157.81);
        const int zoom = 14;
        const double w = 360, h = 640;

        // A tap 80px right / 50px down of centre -> a point -> back to the same pixel.
        var geo = WebMercator.FromScreen(280, 370, center, zoom, w, h);
        var (sx, sy) = WebMercator.ToScreen(geo, center, zoom, w, h);

        Assert.Equal(280, sx, 4);
        Assert.Equal(370, sy, 4);
    }

    [Fact]
    public void Moving_east_increases_longitude_moving_south_decreases_latitude()
    {
        var center = new FieldGeoPoint(21.31, -157.81);
        const int zoom = 12;

        var east = WebMercator.FromScreen(300, 320, center, zoom, 400, 640);   // right of centre
        var south = WebMercator.FromScreen(200, 500, center, zoom, 400, 640);   // below centre

        Assert.True(east.Longitude > center.Longitude, "east of centre should be a greater longitude");
        Assert.True(south.Latitude < center.Latitude, "below centre should be a lower latitude");
    }
}
