namespace Honua.Collect.Core.Maps;

/// <summary>
/// Plans an offline-basemap download: given a geographic area and a zoom range,
/// it enumerates the tiles to fetch and reports the count, so a caller can show
/// download progress and refuse areas that are too large to download politely
/// over a field connection.
/// </summary>
public static class OfflineAreaPlanner
{
    /// <summary>
    /// The default cap on tiles per offline area. The public OSM tile server is
    /// a shared community resource with a bulk-download policy; a few thousand
    /// tiles covers a typical work-site at field zooms without abusing it.
    /// </summary>
    public const int DefaultMaxTiles = 5_000;

    /// <summary>
    /// Plans the tile set for an offline area.
    /// </summary>
    /// <param name="bbox">The geographic area to cover.</param>
    /// <param name="minZoom">The lowest (coarsest) zoom, inclusive.</param>
    /// <param name="maxZoom">The highest (most detailed) zoom, inclusive.</param>
    /// <param name="maxTiles">The maximum tiles allowed; defaults to <see cref="DefaultMaxTiles"/>.</param>
    /// <returns>
    /// A plan with the full tile list and count. When the count exceeds
    /// <paramref name="maxTiles"/>, <see cref="OfflineAreaPlan.ExceedsCap"/> is
    /// <see langword="true"/> so the caller can refuse or narrow the area.
    /// </returns>
    public static OfflineAreaPlan Plan(GeoBoundingBox bbox, int minZoom, int maxZoom, int maxTiles = DefaultMaxTiles)
    {
        ArgumentNullException.ThrowIfNull(bbox);
        if (maxTiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTiles), "maxTiles must be positive.");
        }

        var tiles = TileCache.TilesForArea(bbox, minZoom, maxZoom);
        return new OfflineAreaPlan(tiles, maxTiles);
    }
}

/// <summary>The result of planning an offline area: the tiles and a cap check.</summary>
public sealed class OfflineAreaPlan
{
    internal OfflineAreaPlan(IReadOnlyList<TileCoordinate> tiles, int maxTiles)
    {
        Tiles = tiles;
        MaxTiles = maxTiles;
    }

    /// <summary>Every tile the area requires across the zoom range.</summary>
    public IReadOnlyList<TileCoordinate> Tiles { get; }

    /// <summary>The number of tiles to download.</summary>
    public int Count => Tiles.Count;

    /// <summary>The cap the plan was measured against.</summary>
    public int MaxTiles { get; }

    /// <summary>Whether the area is larger than the allowed cap.</summary>
    public bool ExceedsCap => Count > MaxTiles;
}
