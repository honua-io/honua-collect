// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Maps;

public static class OfflineAreaPlanner
{
    public const int DefaultMaxTiles = 5_000;

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

public sealed class OfflineAreaPlan
{
    internal OfflineAreaPlan(IReadOnlyList<TileCoordinate> tiles, int maxTiles)
    {
        Tiles = tiles;
        MaxTiles = maxTiles;
    }

    public IReadOnlyList<TileCoordinate> Tiles { get; }

    public int Count => Tiles.Count;

    public int MaxTiles { get; }

    public bool ExceedsCap => Count > MaxTiles;
}
