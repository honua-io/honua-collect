using System.Globalization;
using Honua.Collect.Core.Field.Geometry;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Maps;

/// <summary>
/// A pure, device-free on-disk cache for XYZ slippy-map raster tiles, so a
/// viewed or pre-downloaded area keeps working fully offline (a core
/// Survey123/Fulcrum field capability). Tiles are stored under a cache root as
/// <c>{root}/{z}/{x}/{y}.png</c>, giving a deterministic key per
/// <c>(z, x, y)</c>. All filesystem-key and tile-enumeration logic lives here
/// (built on <see cref="WebMercator"/>) so it can be unit-tested without a
/// device or a map control; the app layer only wires fetch/decode on top.
/// </summary>
public sealed class TileCache
{
    /// <summary>The file extension used for cached tile images.</summary>
    public const string TileExtension = ".png";

    private readonly string _root;

    /// <summary>Creates a cache rooted at <paramref name="cacheRoot"/>.</summary>
    /// <param name="cacheRoot">
    /// The directory under which tiles are stored. Typically the app data
    /// directory's <c>tiles</c> folder; passed in by the app so Core stays free
    /// of platform (MAUI) types.
    /// </param>
    /// <exception cref="ArgumentException">If the root is null or whitespace.</exception>
    public TileCache(string cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            throw new ArgumentException("Cache root must be a non-empty path.", nameof(cacheRoot));
        }

        _root = cacheRoot;
    }

    /// <summary>The cache root directory.</summary>
    public string Root => _root;

    /// <summary>
    /// The deterministic on-disk path for a tile, whether or not it exists.
    /// </summary>
    /// <param name="zoom">Tile zoom level.</param>
    /// <param name="x">Tile column.</param>
    /// <param name="y">Tile row.</param>
    /// <returns>The absolute-style path <c>{root}/{z}/{x}/{y}.png</c>.</returns>
    public string PathFor(int zoom, int x, int y) =>
        Path.Combine(
            _root,
            zoom.ToString(CultureInfo.InvariantCulture),
            x.ToString(CultureInfo.InvariantCulture),
            y.ToString(CultureInfo.InvariantCulture) + TileExtension);

    /// <summary>Whether a tile is present on disk.</summary>
    /// <param name="zoom">Tile zoom level.</param>
    /// <param name="x">Tile column.</param>
    /// <param name="y">Tile row.</param>
    /// <returns><see langword="true"/> if the tile file exists.</returns>
    public bool Contains(int zoom, int x, int y) => File.Exists(PathFor(zoom, x, y));

    /// <summary>
    /// Returns the cached tile path if it exists, otherwise <see langword="false"/>.
    /// </summary>
    /// <param name="zoom">Tile zoom level.</param>
    /// <param name="x">Tile column.</param>
    /// <param name="y">Tile row.</param>
    /// <param name="path">The cached path when present.</param>
    /// <returns><see langword="true"/> if the tile is cached.</returns>
    public bool TryGetPath(int zoom, int x, int y, out string path)
    {
        path = PathFor(zoom, x, y);
        return File.Exists(path);
    }

    /// <summary>Writes a tile's encoded bytes to disk, creating directories as needed.</summary>
    /// <param name="zoom">Tile zoom level.</param>
    /// <param name="x">Tile column.</param>
    /// <param name="y">Tile row.</param>
    /// <param name="bytes">The encoded (PNG) tile image bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>
    /// Enumerates the <c>(z, x, y)</c> tiles covering a viewport centred on
    /// <paramref name="center"/> at <paramref name="zoom"/>, matching the tile
    /// grid that <see cref="WebMercator"/> draws. Out-of-range tiles (above the
    /// poles / off the world edge) are clamped out.
    /// </summary>
    /// <param name="center">The geographic point at the viewport centre.</param>
    /// <param name="zoom">Integer tile zoom level.</param>
    /// <param name="widthPx">Viewport width in pixels.</param>
    /// <param name="heightPx">Viewport height in pixels.</param>
    /// <returns>The distinct tiles the viewport touches.</returns>
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

    /// <summary>
    /// Enumerates the <c>(z, x, y)</c> tiles covering a geographic bounding box
    /// across an inclusive zoom range, for prefetching an offline area.
    /// </summary>
    /// <param name="bbox">The geographic area to cover.</param>
    /// <param name="minZoom">The lowest (coarsest) zoom, inclusive.</param>
    /// <param name="maxZoom">The highest (most detailed) zoom, inclusive.</param>
    /// <returns>Every tile the area touches across the zoom range.</returns>
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
