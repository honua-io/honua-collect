// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Maps;

public sealed class GeoBoundingBox
{
    public GeoBoundingBox(double south, double west, double north, double east)
    {
        if (south > north)
        {
            throw new ArgumentException("South edge must be <= north edge.", nameof(south));
        }

        if (west > east)
        {
            throw new ArgumentException("West edge must be <= east edge.", nameof(west));
        }

        South = south;
        West = west;
        North = north;
        East = east;
    }

    public double South { get; }

    public double West { get; }

    public double North { get; }

    public double East { get; }

    public FieldGeoPoint Center => new((South + North) / 2.0, (West + East) / 2.0);

    public static GeoBoundingBox FromCorners(FieldGeoPoint a, FieldGeoPoint b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return new GeoBoundingBox(
            Math.Min(a.Latitude, b.Latitude),
            Math.Min(a.Longitude, b.Longitude),
            Math.Max(a.Latitude, b.Latitude),
            Math.Max(a.Longitude, b.Longitude));
    }
}
