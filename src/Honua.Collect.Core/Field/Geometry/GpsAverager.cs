using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

/// <summary>
/// Averages a stream of GPS fixes into a single, more accurate position
/// (BACKLOG G3). Field crews hold the device over a point and collect many
/// samples; averaging cancels random error, so the result is the mean position
/// with an accuracy estimate that improves with sample count.
/// </summary>
public sealed class GpsAverager
{
    private double _sumLat;
    private double _sumLon;
    private double _sumAccuracy;
    private int _accuracySamples;

    /// <summary>Number of samples collected.</summary>
    public int SampleCount { get; private set; }

    /// <summary>Whether any samples have been collected.</summary>
    public bool HasSamples => SampleCount > 0;

    /// <summary>Adds a GPS fix to the average.</summary>
    /// <param name="fix">Position fix; its accuracy contributes when present.</param>
    public void Add(FieldGeoPoint fix)
    {
        ArgumentNullException.ThrowIfNull(fix);

        _sumLat += fix.Latitude;
        _sumLon += fix.Longitude;
        SampleCount++;

        if (fix.AccuracyMeters is { } accuracy)
        {
            _sumAccuracy += accuracy;
            _accuracySamples++;
        }
    }

    /// <summary>
    /// The averaged position. Latitude/longitude are the sample means; accuracy is
    /// the mean sample accuracy reduced by √n to reflect the error reduction from
    /// averaging independent fixes.
    /// </summary>
    /// <returns>The averaged point.</returns>
    public FieldGeoPoint Average()
    {
        if (SampleCount == 0)
        {
            throw new InvalidOperationException("No GPS samples have been collected.");
        }

        double? accuracy = _accuracySamples > 0
            ? _sumAccuracy / _accuracySamples / Math.Sqrt(_accuracySamples)
            : null;

        return new FieldGeoPoint(_sumLat / SampleCount, _sumLon / SampleCount, accuracy);
    }

    /// <summary>Resets the averager to collect a new point.</summary>
    public void Reset()
    {
        _sumLat = 0;
        _sumLon = 0;
        _sumAccuracy = 0;
        _accuracySamples = 0;
        SampleCount = 0;
    }
}
