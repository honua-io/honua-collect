using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry.Nmea;

/// <summary>
/// A position fix assembled from a GNSS receiver's NMEA output (BACKLOG G5): the
/// coordinate plus the quality signals a field crew needs to decide whether to
/// trust a vertex — fix type, estimated horizontal accuracy, HDOP, and satellite
/// count. Platform-neutral so the receiver transport (Bluetooth/USB) can live in
/// the app while this — the part worth testing — stays in Core.
/// </summary>
public sealed record NmeaFix
{
    /// <summary>The fix quality / RTK solution type.</summary>
    public required NmeaFixQuality Quality { get; init; }

    /// <summary>Latitude in decimal degrees (north positive), or null with no positional fix.</summary>
    public double? Latitude { get; init; }

    /// <summary>Longitude in decimal degrees (east positive), or null with no positional fix.</summary>
    public double? Longitude { get; init; }

    /// <summary>Altitude above mean sea level in metres, when reported.</summary>
    public double? AltitudeMeters { get; init; }

    /// <summary>
    /// Estimated horizontal accuracy in metres (1σ) from the GST sentence's lat/lon
    /// standard deviations, when available.
    /// </summary>
    public double? HorizontalAccuracyMeters { get; init; }

    /// <summary>Horizontal dilution of precision, when reported.</summary>
    public double? Hdop { get; init; }

    /// <summary>Number of satellites used in the solution, when reported.</summary>
    public int? SatellitesUsed { get; init; }

    /// <summary>UTC time-of-day of the fix, when reported.</summary>
    public TimeSpan? UtcTime { get; init; }

    /// <summary>Whether the fix carries a usable coordinate (quality &gt; None and lat/lon present).</summary>
    public bool HasPosition => Quality != NmeaFixQuality.None && Latitude is not null && Longitude is not null;

    /// <summary>Whether the fix is a centimetre-grade RTK fixed solution.</summary>
    public bool IsRtkFixed => Quality == NmeaFixQuality.RtkFixed;

    /// <summary>
    /// Whether the fix passes an accuracy gate: it has a position and, when a horizontal
    /// accuracy estimate is present, that estimate is at or below the threshold. A fix
    /// with no accuracy estimate is accepted only when <paramref name="requireAccuracy"/>
    /// is false (the caller can insist on a measured accuracy for high-grade work).
    /// </summary>
    /// <param name="maxAccuracyMeters">Maximum acceptable horizontal accuracy in metres.</param>
    /// <param name="requireAccuracy">When true, reject a fix that reports no accuracy estimate.</param>
    /// <returns><see langword="true"/> when the fix is acceptable for capture.</returns>
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

    /// <summary>Converts the fix to a <see cref="FieldGeoPoint"/> for capture.</summary>
    /// <returns>The point with its horizontal accuracy estimate.</returns>
    /// <exception cref="InvalidOperationException">The fix has no usable position.</exception>
    public FieldGeoPoint ToFieldGeoPoint()
    {
        if (!HasPosition)
        {
            throw new InvalidOperationException("The NMEA fix has no usable position.");
        }

        return new FieldGeoPoint(Latitude!.Value, Longitude!.Value, HorizontalAccuracyMeters);
    }
}
