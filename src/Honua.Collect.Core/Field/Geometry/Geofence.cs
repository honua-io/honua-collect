// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

public sealed record Geofence
{
    public Geofence(FieldGeoPoint center, double radiusMeters)
    {
        ArgumentNullException.ThrowIfNull(center);
        if (radiusMeters <= 0 || double.IsNaN(radiusMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(radiusMeters), radiusMeters, "Radius must be positive.");
        }

        Center = center;
        RadiusMeters = radiusMeters;
    }

    public FieldGeoPoint Center { get; }

    public double RadiusMeters { get; }

    public double DistanceMeters(FieldGeoPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        // The SDK returns null only when a point lacks coordinates; treat that as
        // "infinitely far" so an uncomputable distance never reads as inside the fence.
        return SphericalFieldDistanceCalculator.Instance.CalculateDistanceMeters(Center, point)
            ?? double.PositiveInfinity;
    }

    public bool Contains(FieldGeoPoint point) => DistanceMeters(point) <= RadiusMeters;
}
