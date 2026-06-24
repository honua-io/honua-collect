namespace Honua.Collect.Core.Field.Sensors;

/// <summary>How a window of <see cref="SensorReading"/>s collapses to a single value.</summary>
public enum SensorAggregation
{
    /// <summary>The most recent reading's value.</summary>
    Latest = 0,

    /// <summary>The arithmetic mean of the window.</summary>
    Average = 1,

    /// <summary>The minimum value in the window.</summary>
    Minimum = 2,

    /// <summary>The maximum value in the window.</summary>
    Maximum = 3,
}

/// <summary>
/// The result of aggregating a window of readings: the value, the unit it carries
/// (taken from the readings), the timestamp of the newest contributing reading,
/// the worst quality seen, and how many readings contributed.
/// </summary>
/// <param name="Value">The aggregated value.</param>
/// <param name="Unit">The unit of the contributing readings, or <see langword="null"/>.</param>
/// <param name="TimestampUtc">Timestamp of the newest contributing reading.</param>
/// <param name="Quality">The worst (highest) quality flag among contributors.</param>
/// <param name="SampleCount">Number of readings that contributed.</param>
public readonly record struct SensorAggregateResult(
    double Value,
    string? Unit,
    DateTimeOffset TimestampUtc,
    SensorQuality Quality,
    int SampleCount);

/// <summary>
/// Collapses a window of readings into a single value (BACKLOG I3 aggregation
/// step). Operates over a snapshot from a <see cref="SensorReadingBuffer"/>, so
/// aggregation is a pure function of the readings and trivially testable.
/// </summary>
public static class SensorAggregator
{
    /// <summary>
    /// Aggregates a window of readings. Readings flagged <see cref="SensorQuality.Bad"/>
    /// are excluded; the result's quality is the worst flag among the readings that
    /// did contribute. The unit and timestamp come from the newest contributing reading.
    /// </summary>
    /// <param name="readings">The window to aggregate (oldest to newest).</param>
    /// <param name="aggregation">The aggregation to apply.</param>
    /// <returns>The aggregate, or <see langword="null"/> when no usable readings exist.</returns>
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
