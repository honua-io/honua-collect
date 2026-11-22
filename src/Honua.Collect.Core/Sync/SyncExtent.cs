// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Geometry;

namespace Honua.Collect.Core.Sync;

public sealed record SyncExtent
{
    public SyncExtent(double minX, double minY, double maxX, double maxY)
    {
        MinX = Math.Min(minX, maxX);
        MinY = Math.Min(minY, maxY);
        MaxX = Math.Max(minX, maxX);
        MaxY = Math.Max(minY, maxY);
    }

    public double MinX { get; }

    public double MinY { get; }

    public double MaxX { get; }

    public double MaxY { get; }

    public bool Contains(PlanarPoint point)
        => point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;
}
