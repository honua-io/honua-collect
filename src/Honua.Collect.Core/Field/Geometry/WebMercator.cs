// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

public static class WebMercator
{
    public const int TileSize = 256;

    public const double MaxLatitude = 85.05112877980659;

    public static double MapSize(int zoom) => (double)TileSize * (1L << zoom);

    public static (double X, double Y) ToWorldPixel(FieldGeoPoint point, int zoom)
    {
        ArgumentNullException.ThrowIfNull(point);
        return ToWorldPixel(point.Latitude, point.Longitude, zoom);
    }

    public static (double X, double Y) ToWorldPixel(double latitude, double longitude, int zoom)
    {
        var size = MapSize(zoom);
        var lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);

        var x = (longitude + 180.0) / 360.0 * size;

        var sinLat = Math.Sin(lat * Math.PI / 180.0);
        var y = (0.5 - Math.Log((1 + sinLat) / (1 - sinLat)) / (4 * Math.PI)) * size;

        return (x, y);
    }

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

    public static (int X, int Y) ToTile(FieldGeoPoint point, int zoom)
    {
        ArgumentNullException.ThrowIfNull(point);
        var (px, py) = ToWorldPixel(point, zoom);
        var max = (1 << zoom) - 1;
        return (
            Math.Clamp((int)Math.Floor(px / TileSize), 0, max),
            Math.Clamp((int)Math.Floor(py / TileSize), 0, max));
    }

    public static (double X, double Y) ToScreen(
        FieldGeoPoint point, FieldGeoPoint center, int zoom, double viewportWidth, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(center);

        var (px, py) = ToWorldPixel(point, zoom);
        var (cx, cy) = ToWorldPixel(center, zoom);
        return (px - cx + viewportWidth / 2.0, py - cy + viewportHeight / 2.0);
    }

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
