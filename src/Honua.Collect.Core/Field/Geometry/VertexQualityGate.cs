// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Geometry.Nmea;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

public enum VertexQualityRejection
{
    None,

    TooFewSamples,

    FixBelowAccuracy,

    AveragedAccuracyTooLow,
}

public sealed record VertexQualityResult(
    bool Accepted,
    VertexQualityRejection Rejection,
    FieldGeoPoint? Point,
    int SampleCount);

public sealed class VertexQualityGate
{
    public int MinimumSamples { get; init; } = 1;

    public double? MaxAccuracyMeters { get; init; }

    public bool RequireFixAccuracy { get; init; }

    public VertexQualityResult Evaluate(GpsAverager averager, NmeaFix? latestFix = null)
    {
        ArgumentNullException.ThrowIfNull(averager);

        if (averager.SampleCount < Math.Max(1, MinimumSamples))
        {
            return new VertexQualityResult(false, VertexQualityRejection.TooFewSamples, null, averager.SampleCount);
        }

        // Live fix gate: when a fix is supplied it must still pass its own accuracy /
        // fix-type bar at the moment of capture.
        if (latestFix is not null && MaxAccuracyMeters is { } maxFix &&
            !latestFix.MeetsAccuracy(maxFix, RequireFixAccuracy))
        {
            return new VertexQualityResult(false, VertexQualityRejection.FixBelowAccuracy, null, averager.SampleCount);
        }

        var averaged = averager.Average();

        // Averaged-position accuracy gate.
        if (MaxAccuracyMeters is { } maxAvg && averaged.AccuracyMeters is { } accuracy && accuracy > maxAvg)
        {
            return new VertexQualityResult(
                false, VertexQualityRejection.AveragedAccuracyTooLow, averaged, averager.SampleCount);
        }

        return new VertexQualityResult(true, VertexQualityRejection.None, averaged, averager.SampleCount);
    }
}
