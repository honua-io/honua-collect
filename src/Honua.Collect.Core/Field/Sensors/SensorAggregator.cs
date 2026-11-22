// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Sensors;

public enum SensorAggregation
{
    Latest = 0,

    Average = 1,

    Minimum = 2,

    Maximum = 3,
}

public readonly record struct SensorAggregateResult(
    double Value,
    string? Unit,
    DateTimeOffset TimestampUtc,
    SensorQuality Quality,
    int SampleCount);

public static class SensorAggregator
{
    public static SensorAggregateResult? Aggregate(
        IReadOnlyList<SensorReading> readings,
        SensorAggregation aggregation)
    {
        ArgumentNullException.ThrowIfNull(readings);

        SensorReading? newest = null;
        var quality = SensorQuality.Good;
        var count = 0;
        var sum = 0.0;
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;

        foreach (var reading in readings)
        {
            if (!reading.IsUsable)
            {
                continue;
            }

            count++;
            sum += reading.Value;
            min = Math.Min(min, reading.Value);
            max = Math.Max(max, reading.Value);

            if (reading.Quality > quality)
            {
                quality = reading.Quality;
            }

            if (newest is null || reading.TimestampUtc >= newest.Value.TimestampUtc)
            {
                newest = reading;
            }
        }

        if (count == 0 || newest is null)
        {
            return null;
        }

        var value = aggregation switch
        {
            SensorAggregation.Latest => newest.Value.Value,
            SensorAggregation.Average => sum / count,
            SensorAggregation.Minimum => min,
            SensorAggregation.Maximum => max,
            _ => throw new ArgumentOutOfRangeException(nameof(aggregation), aggregation, "Unknown aggregation."),
        };

        return new SensorAggregateResult(value, newest.Value.Unit, newest.Value.TimestampUtc, quality, count);
    }
}
