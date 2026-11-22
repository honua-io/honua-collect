// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Geometry;

public readonly record struct PlanarPoint(double X, double Y)
{
    public double DistanceSquaredTo(PlanarPoint other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return (dx * dx) + (dy * dy);
    }

    public double DistanceTo(PlanarPoint other) => Math.Sqrt(DistanceSquaredTo(other));
}
