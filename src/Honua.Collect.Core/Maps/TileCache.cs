// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Honua.Collect.Core.Field.Geometry;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Maps;

public sealed class TileCache
{
    public const string TileExtension = ".png";

    private readonly string _root;

    public TileCache(string cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            throw new ArgumentException("Cache root must be a non-empty path.", nameof(cacheRoot));
        }

        _root = cacheRoot;
    }

    public string Root => _root;

    public string PathFor(int zoom, int x, int y) =>
        Path.Combine(
            _root,
            zoom.ToString(CultureInfo.InvariantCulture),
            x.ToString(CultureInfo.InvariantCulture),
            y.ToString(CultureInfo.InvariantCulture) + TileExtension);

    public bool Contains(int zoom, int x, int y) => File.Exists(PathFor(zoom, x, y));

    public bool TryGetPath(int zoom, int x, int y, out string path)
    {
        path = PathFor(zoom, x, y);
        return File.Exists(path);
    }

    public async Task SaveAsync(int zoom, int x, int y, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var path = PathFor(zoom, x, y);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Write to a temp file then move, so a crash mid-write never leaves a
        // truncated tile that would later decode to garbage offline.
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    public static IReadOnlyList<TileCoordinate> TilesNeededForViewport(
        FieldGeoPoint center, int zoom, double widthPx, double heightPx)
    {
        ArgumentNullException.ThrowIfNull(center);

        const int tile = WebMercator.TileSize;
        var max = (1 << zoom) - 1;

        var (cx, cy) = WebMercator.ToWorldPixel(center, zoom);
        var originX = cx - widthPx / 2.0;
        var originY = cy - heightPx / 2.0;

        var firstCol = (int)Math.Floor(originX / tile);
        var firstRow = (int)Math.Floor(originY / tile);
        var lastCol = (int)Math.Floor((originX + widthPx) / tile);
        var lastRow = (int)Math.Floor((originY + heightPx) / tile);

        var tiles = new List<TileCoordinate>();
        for (var ty = firstRow; ty <= lastRow; ty++)
        {
            if (ty < 0 || ty > max)
            {
                continue;
            }

            for (var tx = firstCol; tx <= lastCol; tx++)
            {
                if (tx < 0 || tx > max)
                {
                    continue;
                }

                tiles.Add(new TileCoordinate(zoom, tx, ty));
            }
        }

        return tiles;
    }

    public static IReadOnlyList<TileCoordinate> TilesForArea(GeoBoundingBox bbox, int minZoom, int maxZoom)
    {
        ArgumentNullException.ThrowIfNull(bbox);
        if (minZoom < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minZoom), "Zoom must be non-negative.");
        }

        if (maxZoom < minZoom)
        {
            throw new ArgumentOutOfRangeException(nameof(maxZoom), "maxZoom must be >= minZoom.");
        }

        var tiles = new List<TileCoordinate>();
        for (var z = minZoom; z <= maxZoom; z++)
        {
            var max = (1 << z) - 1;

            // ToTile already clamps to the valid tile grid; north/west give the
            // min indices, south/east the max (y grows southward in XYZ).
            var (minX, minY) = WebMercator.ToTile(new FieldGeoPoint(bbox.North, bbox.West), z);
            var (maxX, maxY) = WebMercator.ToTile(new FieldGeoPoint(bbox.South, bbox.East), z);

            if (minX > maxX)
            {
                (minX, maxX) = (maxX, minX);
            }

            if (minY > maxY)
            {
                (minY, maxY) = (maxY, minY);
            }

            for (var x = Math.Max(0, minX); x <= Math.Min(max, maxX); x++)
            {
                for (var y = Math.Max(0, minY); y <= Math.Min(max, maxY); y++)
                {
                    tiles.Add(new TileCoordinate(z, x, y));
                }
            }
        }

        return tiles;
    }
}
