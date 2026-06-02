using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

/// <summary>
/// Spherical Web Mercator (EPSG:3857) projection math for a slippy-map tile
/// surface — the coordinate backbone for the embedded 2D map (BACKLOG G4). All
/// methods are pure so screen↔geographic mapping is unit-testable without a
/// device or a map control.
/// </summary>
/// <remarks>
/// "World pixels" are the global pixel coordinate space at a given integer zoom:
/// the world is <c>256 · 2^zoom</c> pixels square, origin (0,0) at the top-left
/// (lon −180°, the Mercator latitude limit ≈ +85.0511°), x increasing east and
/// y increasing south. This matches the OSM/XYZ slippy-map convention.
/// </remarks>
public static class WebMercator
{
    /// <summary>Edge length in pixels of a single map tile.</summary>
    public const int TileSize = 256;

    /// <summary>The maximum absolute latitude representable in Web Mercator.</summary>
    public const double MaxLatitude = 85.05112877980659;

    /// <summary>Total width/height of the world in pixels at the given zoom.</summary>
    /// <param name="zoom">Integer tile zoom level (0 = whole world in one tile).</param>
    /// <returns>The map size in pixels.</returns>
    public static double MapSize(int zoom) => (double)TileSize * (1L << zoom);

    /// <summary>Projects a geographic point to global world-pixel coordinates at a zoom.</summary>
    /// <param name="point">The geographic point.</param>
    /// <param name="zoom">Integer tile zoom level.</param>
    /// <returns>The world-pixel (x, y).</returns>
    public static (double X, double Y) ToWorldPixel(FieldGeoPoint point, int zoom)
    {
        ArgumentNullException.ThrowIfNull(point);
        return ToWorldPixel(point.Latitude, point.Longitude, zoom);
    }

    /// <summary>Projects a lat/lon to global world-pixel coordinates at a zoom.</summary>
    /// <param name="latitude">Latitude in decimal degrees.</param>
    /// <param name="longitude">Longitude in decimal degrees.</param>
    /// <param name="zoom">Integer tile zoom level.</param>
    /// <returns>The world-pixel (x, y).</returns>
    public static (double X, double Y) ToWorldPixel(double latitude, double longitude, int zoom)
    {
        var size = MapSize(zoom);
        var lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);

        var x = (longitude + 180.0) / 360.0 * size;

        var sinLat = Math.Sin(lat * Math.PI / 180.0);
        var y = (0.5 - Math.Log((1 + sinLat) / (1 - sinLat)) / (4 * Math.PI)) * size;

        return (x, y);
    }

    /// <summary>Inverts <see cref="ToWorldPixel(double, double, int)"/> back to a geographic point.</summary>
    /// <param name="x">World-pixel x.</param>
    /// <param name="y">World-pixel y.</param>
    /// <param name="zoom">Integer tile zoom level.</param>
    /// <returns>The geographic point.</returns>
    public static FieldGeoPoint FromWorldPixel(double x, double y, int zoom)
    {
        var size = MapSize(zoom);

        // Wrap x around the antimeridian; clamp y to the valid Mercator band.
        var fx = x / size - Math.Floor(x / size);
        var fy = Math.Clamp(y / size, 0.0, 1.0);

        var longitude = fx * 360.0 - 180.0;
        var n = Math.PI * (1 - 2 * fy);
        var latitude = Math.Atan(Math.Sinh(n)) * 180.0 / Math.PI;

        return new FieldGeoPoint(latitude, longitude);
    }

    /// <summary>The XYZ tile column/row containing a geographic point at a zoom.</summary>
    /// <param name="point">The geographic point.</param>
    /// <param name="zoom">Integer tile zoom level.</param>
    /// <returns>The tile (x, y) indices.</returns>
    public static (int X, int Y) ToTile(FieldGeoPoint point, int zoom)
    {
        ArgumentNullException.ThrowIfNull(point);
        var (px, py) = ToWorldPixel(point, zoom);
        var max = (1 << zoom) - 1;
        return (
            Math.Clamp((int)Math.Floor(px / TileSize), 0, max),
            Math.Clamp((int)Math.Floor(py / TileSize), 0, max));
    }

    /// <summary>
    /// Maps a geographic point to a screen pixel within a viewport, given the map
    /// <paramref name="center"/>, <paramref name="zoom"/>, and the viewport size.
    /// The center maps to the middle of the viewport.
    /// </summary>
    /// <param name="point">The geographic point to place.</param>
    /// <param name="center">The geographic point at the viewport centre.</param>
    /// <param name="zoom">Integer tile zoom level.</param>
    /// <param name="viewportWidth">Viewport width in pixels.</param>
    /// <param name="viewportHeight">Viewport height in pixels.</param>
    /// <returns>The screen-pixel (x, y), which may fall outside the viewport.</returns>
    public static (double X, double Y) ToScreen(
        FieldGeoPoint point, FieldGeoPoint center, int zoom, double viewportWidth, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(center);

        var (px, py) = ToWorldPixel(point, zoom);
        var (cx, cy) = ToWorldPixel(center, zoom);
        return (px - cx + viewportWidth / 2.0, py - cy + viewportHeight / 2.0);
    }

    /// <summary>
    /// Inverts <see cref="ToScreen"/>: maps a screen pixel within a viewport back
    /// to a geographic point, given the same map <paramref name="center"/>,
    /// <paramref name="zoom"/>, and viewport size.
    /// </summary>
    /// <param name="screenX">Screen-pixel x within the viewport.</param>
    /// <param name="screenY">Screen-pixel y within the viewport.</param>
    /// <param name="center">The geographic point at the viewport centre.</param>
    /// <param name="zoom">Integer tile zoom level.</param>
    /// <param name="viewportWidth">Viewport width in pixels.</param>
    /// <param name="viewportHeight">Viewport height in pixels.</param>
    /// <returns>The geographic point under the screen pixel.</returns>
    public static FieldGeoPoint FromScreen(
        double screenX, double screenY, FieldGeoPoint center, int zoom, double viewportWidth, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(center);

        var (cx, cy) = ToWorldPixel(center, zoom);
        var worldX = screenX - viewportWidth / 2.0 + cx;
        var worldY = screenY - viewportHeight / 2.0 + cy;
        return FromWorldPixel(worldX, worldY, zoom);
    }
}
