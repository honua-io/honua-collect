// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Field.Core.Maps;

/// <summary>
/// Pure builders for the OpenStreetMap XYZ tile endpoint and the in-memory cache
/// key the embedded map uses (BACKLOG G4). Keeping the URL format and the
/// <c>z/x/y</c> key in Core — next to <see cref="TileCache"/> and
/// <see cref="Honua.Field.Core.Field.Geometry.WebMercator"/> — means the tile
/// addressing is unit-testable without a network or a map control; the app's
/// <c>OsmTileLoader</c> only wires fetch/decode on top.
/// </summary>
public static class OsmTileUrl
{
    /// <summary>
    /// The OSM tile URL template: <c>{0}=zoom</c>, <c>{1}=x</c>, <c>{2}=y</c>.
    /// Public so a caller can identify the source; format with <see cref="For"/>.
    /// </summary>
    public const string Template = PublicTemplate;

    /// <summary>Opt in only when the configured provider authorizes offline prefetch.</summary>
    public const bool AllowOfflinePrefetch = false;

    private const string PublicTemplate = "https://tile.openstreetmap.org/{0}/{1}/{2}.png";

    /// <summary>Checks explicit permission and excludes public OpenStreetMap hosts.</summary>
    public static bool AllowsOfflinePrefetch(string template, bool optedIn)
    {
        if (!optedIn || !Uri.TryCreate(template, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http"))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.');
        return !host.Equals("openstreetmap.org", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".openstreetmap.org", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Separates alternate tile URLs while retaining the existing public cache.</summary>
    public static string CacheDirectory(string root, string template) =>
        string.Equals(template, PublicTemplate, StringComparison.Ordinal)
            ? root
            : Path.Combine(root, "sources", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template))));

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
