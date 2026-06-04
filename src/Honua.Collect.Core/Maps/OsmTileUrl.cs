using System.Globalization;

namespace Honua.Collect.Core.Maps;

/// <summary>
/// Pure builders for the OpenStreetMap XYZ tile endpoint and the in-memory cache
/// key the embedded map uses (BACKLOG G4). Keeping the URL format and the
/// <c>z/x/y</c> key in Core — next to <see cref="TileCache"/> and
/// <see cref="Honua.Collect.Core.Field.Geometry.WebMercator"/> — means the tile
/// addressing is unit-testable without a network or a map control; the app's
/// <c>OsmTileLoader</c> only wires fetch/decode on top.
/// </summary>
public static class OsmTileUrl
{
    /// <summary>
    /// The OSM tile URL template: <c>{0}=zoom</c>, <c>{1}=x</c>, <c>{2}=y</c>.
    /// Public so a caller can identify the source; format with <see cref="For"/>.
    /// </summary>
    public const string Template = "https://tile.openstreetmap.org/{0}/{1}/{2}.png";

    /// <summary>
    /// Builds the absolute URL for an XYZ slippy-map tile on the public OSM tile
    /// server, e.g. <c>https://tile.openstreetmap.org/14/8192/5461.png</c>.
    /// </summary>
    /// <param name="zoom">Tile zoom level.</param>
    /// <param name="x">Tile column.</param>
    /// <param name="y">Tile row.</param>
    /// <returns>The tile request URL, formatted invariantly.</returns>
    public static string For(int zoom, int x, int y) =>
        string.Format(CultureInfo.InvariantCulture, Template, zoom, x, y);

    /// <summary>
    /// The deterministic in-memory cache key for a tile, <c>"{zoom}/{x}/{y}"</c>.
    /// Stable for the same coordinate so the decoded-image cache and the in-flight
    /// set agree across calls.
    /// </summary>
    /// <param name="zoom">Tile zoom level.</param>
    /// <param name="x">Tile column.</param>
    /// <param name="y">Tile row.</param>
    /// <returns>The cache key.</returns>
    public static string CacheKey(int zoom, int x, int y) =>
        string.Create(CultureInfo.InvariantCulture, $"{zoom}/{x}/{y}");
}
