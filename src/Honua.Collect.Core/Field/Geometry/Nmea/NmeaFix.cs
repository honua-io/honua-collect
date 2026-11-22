// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry.Nmea;

public sealed record NmeaFix
{
    public required NmeaFixQuality Quality { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    public double? AltitudeMeters { get; init; }

    public double? HorizontalAccuracyMeters { get; init; }

    public double? VerticalAccuracyMeters { get; init; }

    public double? Hdop { get; init; }

    public double? SpeedMetersPerSecond { get; init; }

    public double? CourseDegrees { get; init; }

    public int? SatellitesUsed { get; init; }

    public TimeSpan? UtcTime { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public const double DefaultUereMeters = 5.0;

    public bool HasPosition => Quality != NmeaFixQuality.None && Latitude is not null && Longitude is not null;

    public bool IsRtkFixed => Quality == NmeaFixQuality.RtkFixed;

    public bool MeetsAccuracy(double maxAccuracyMeters, bool requireAccuracy = false)
    {
        if (!HasPosition)
        {
            return false;
        }

        if (HorizontalAccuracyMeters is { } accuracy)
        {
            return accuracy <= maxAccuracyMeters;
        }

        return !requireAccuracy;
    }

    public FieldGeoPoint ToFieldGeoPoint()
    {
        if (!HasPosition)
        {
            throw new InvalidOperationException("The NMEA fix has no usable position.");
        }

        return new FieldGeoPoint(Latitude!.Value, Longitude!.Value, HorizontalAccuracyMeters);
    }
}
