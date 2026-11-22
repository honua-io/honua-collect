// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Honua.Collect.Core.Maps;

public static class OsmTileUrl
{
    public const string Template = "https://tile.openstreetmap.org/{0}/{1}/{2}.png";

    public static string For(int zoom, int x, int y) =>
        string.Format(CultureInfo.InvariantCulture, Template, zoom, x, y);

    public static string CacheKey(int zoom, int x, int y) =>
        string.Create(CultureInfo.InvariantCulture, $"{zoom}/{x}/{y}");
}
