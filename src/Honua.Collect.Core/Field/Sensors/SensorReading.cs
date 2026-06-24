namespace Honua.Collect.Core.Field.Sensors;

/// <summary>
/// A single measurement emitted by a sensor (BACKLOG I1/I3). Platform-neutral so
/// the device radio (Bluetooth/USB/NFC) lives in the app while this — the part
/// worth testing — stays in Core. Mirrors the shape of the NMEA/GNSS work
/// (<see cref="Geometry.Nmea.NmeaFix"/>): a transport decodes a wire frame into
/// one of these, and the ingestion pipeline turns a stream of them into a field
/// value.
/// </summary>
/// <param name="SensorId">
/// Stable identifier of the physical sensor / IoT device (for example a BLE
/// peripheral address or a logical device name).
/// </param>
/// <param name="Channel">
/// The measurement channel on that sensor — a single device can expose several
/// (for example <c>temperature</c>, <c>humidity</c>, <c>battery</c>). The
/// channel is the unit of aggregation and field binding.
/// </param>
/// <param name="Value">The measured value.</param>
/// <param name="Unit">
/// The unit the value is expressed in (for example <c>degC</c>, <c>%</c>,
/// <c>ppm</c>), or <see langword="null"/> when dimensionless. Used by
/// <see cref="SensorFieldBinding"/> for unit handling.
/// </param>
/// <param name="TimestampUtc">When the measurement was taken (UTC).</param>
/// <param name="Quality">The reading's quality flag.</param>
public readonly record struct SensorReading(
    string SensorId,
    string Channel,
    double Value,
    string? Unit,
    DateTimeOffset TimestampUtc,
    SensorQuality Quality = SensorQuality.Good)
{
    /// <summary>Whether this reading is usable for binding (its quality is not <see cref="SensorQuality.Bad"/>).</summary>
    public bool IsUsable => Quality != SensorQuality.Bad;

    /// <summary>The age of this reading relative to a reference instant (typically "now").</summary>
    /// <param name="asOfUtc">Reference instant.</param>
    /// <returns>The non-negative age; zero for readings stamped in the future.</returns>
    public TimeSpan AgeAt(DateTimeOffset asOfUtc)
    {
        var age = asOfUtc - TimestampUtc;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
