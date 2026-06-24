using Honua.Collect.Core.Field.Geometry.Nmea;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

/// <summary>
/// The reason a candidate vertex was rejected by the quality gate, or
/// <see cref="None"/> when it was accepted.
/// </summary>
public enum VertexQualityRejection
{
    /// <summary>The vertex was accepted.</summary>
    None,

    /// <summary>Fewer averaged samples than the configured minimum.</summary>
    TooFewSamples,

    /// <summary>The live receiver fix failed its accuracy / fix-type gate.</summary>
    FixBelowAccuracy,

    /// <summary>The averaged position's own accuracy estimate exceeded the maximum.</summary>
    AveragedAccuracyTooLow,
}

/// <summary>
/// Whether a candidate vertex meets the capture-quality bar, and why not when it
/// does not.
/// </summary>
/// <param name="Accepted">Whether the vertex passed every gate.</param>
/// <param name="Rejection">The first failing gate, or <see cref="VertexQualityRejection.None"/>.</param>
/// <param name="Point">The averaged position when one is available, else <see langword="null"/>.</param>
/// <param name="SampleCount">The number of samples that fed the average.</param>
public sealed record VertexQualityResult(
    bool Accepted,
    VertexQualityRejection Rejection,
    FieldGeoPoint? Point,
    int SampleCount);

/// <summary>
/// A vertex-quality gate (BACKLOG G3 enhancement) that combines averaged-position
/// stability with live receiver fix quality before a vertex is committed.
/// </summary>
/// <remarks>
/// This reuses, rather than re-implements, the two existing signals: the
/// <see cref="GpsAverager"/> output (mean position + √n-reduced accuracy) and the
/// receiver fix's own gate, <see cref="NmeaFix.MeetsAccuracy"/> (horizontal accuracy
/// and/or fix type). A crew can require both a settled average and a live fix that is
/// still good at the moment of capture, which catches the case where the receiver
/// degrades (loses RTK) part-way through a hold.
/// </remarks>
public sealed class VertexQualityGate
{
    /// <summary>The minimum number of averaged samples required (default 1).</summary>
    public int MinimumSamples { get; init; } = 1;

    /// <summary>
    /// The maximum acceptable horizontal accuracy in metres applied to both the live
    /// fix and the averaged position. <see langword="null"/> disables the accuracy
    /// checks (sample count and fix presence are still enforced).
    /// </summary>
    public double? MaxAccuracyMeters { get; init; }

    /// <summary>
    /// When true, a live fix with no accuracy estimate is rejected; passed straight
    /// through to <see cref="NmeaFix.MeetsAccuracy"/>.
    /// </summary>
    public bool RequireFixAccuracy { get; init; }

    /// <summary>
    /// Evaluates a candidate vertex from an averager and, optionally, the latest
    /// live receiver fix.
    /// </summary>
    /// <param name="averager">The averager holding the held-point samples.</param>
    /// <param name="latestFix">
    /// The most recent receiver fix, when available; its <see cref="NmeaFix.MeetsAccuracy"/>
    /// gate is applied so a vertex is rejected if the receiver has degraded.
    /// </param>
    /// <returns>The quality result.</returns>
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
