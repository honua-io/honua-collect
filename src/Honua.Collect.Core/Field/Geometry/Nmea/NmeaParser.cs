using System.Globalization;

namespace Honua.Collect.Core.Field.Geometry.Nmea;

/// <summary>
/// Low-level NMEA 0183 parsing: checksum validation and the field conversions used
/// by <see cref="NmeaFixAssembler"/>. Kept separate (and public) so the checksum
/// and coordinate maths are unit-tested directly.
/// </summary>
public static class NmeaParser
{
    /// <summary>
    /// Validates a sentence's <c>*HH</c> checksum (XOR of the bytes between <c>$</c>
    /// and <c>*</c>). Returns false for any structurally invalid sentence.
    /// </summary>
    /// <param name="sentence">A raw NMEA sentence, optionally with a trailing newline.</param>
    /// <returns><see langword="true"/> when the checksum is present and correct.</returns>
    public static bool ValidateChecksum(string? sentence)
    {
        if (string.IsNullOrEmpty(sentence))
        {
            return false;
        }

        var line = sentence.Trim();
        if (line.Length < 4 || line[0] != '$')
        {
            return false;
        }

        var star = line.LastIndexOf('*');
        if (star < 1 || star + 3 > line.Length)
        {
            return false;
        }

        var checksumText = line.Substring(star + 1, 2);
        if (!byte.TryParse(checksumText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expected))
        {
            return false;
        }

        byte actual = 0;
        for (var i = 1; i < star; i++)
        {
            actual ^= (byte)line[i];
        }

        return actual == expected;
    }

    /// <summary>Returns the comma-separated fields of a checksum-valid sentence (the part between <c>$</c> and <c>*</c>).</summary>
    /// <param name="sentence">A raw NMEA sentence.</param>
    /// <returns>The fields, or null when the sentence is invalid.</returns>
    internal static string[]? SplitFields(string sentence)
    {
        if (!ValidateChecksum(sentence))
        {
            return null;
        }

        var line = sentence.Trim();
        var star = line.LastIndexOf('*');
        return line.Substring(1, star - 1).Split(',');
    }

    /// <summary>Parses an NMEA <c>ddmm.mmmm</c> latitude with an N/S hemisphere into decimal degrees.</summary>
    /// <param name="value">The <c>ddmm.mmmm</c> field.</param>
    /// <param name="hemisphere">"N" or "S".</param>
    /// <returns>Decimal degrees (north positive), or null when unparseable / out of range.</returns>
    public static double? ParseLatitude(string? value, string? hemisphere)
        => ParseCoordinate(value, hemisphere, degreeDigits: 2, maxDegrees: 90, negative: "S", positive: "N");

    /// <summary>Parses an NMEA <c>dddmm.mmmm</c> longitude with an E/W hemisphere into decimal degrees.</summary>
    /// <param name="value">The <c>dddmm.mmmm</c> field.</param>
    /// <param name="hemisphere">"E" or "W".</param>
    /// <returns>Decimal degrees (east positive), or null when unparseable / out of range.</returns>
    public static double? ParseLongitude(string? value, string? hemisphere)
        => ParseCoordinate(value, hemisphere, degreeDigits: 3, maxDegrees: 180, negative: "W", positive: "E");

    private static double? ParseCoordinate(string? value, string? hemisphere, int degreeDigits, double maxDegrees, string negative, string positive)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(hemisphere))
        {
            return null;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw) || raw < 0)
        {
            return null;
        }

        // ddmm.mmmm / dddmm.mmmm: integer part is DDD then MM, fractional is minutes.
        var degrees = Math.Floor(raw / 100);
        var minutes = raw - (degrees * 100);
        if (minutes >= 60 || degrees > maxDegrees)
        {
            return null;
        }

        var result = degrees + (minutes / 60.0);
        if (result > maxDegrees)
        {
            return null;
        }

        if (string.Equals(hemisphere, negative, StringComparison.OrdinalIgnoreCase))
        {
            return -result;
        }

        return string.Equals(hemisphere, positive, StringComparison.OrdinalIgnoreCase) ? result : (double?)null;
    }

    /// <summary>Parses an NMEA <c>hhmmss(.sss)</c> time-of-day field into a <see cref="TimeSpan"/>.</summary>
    /// <param name="value">The time field.</param>
    /// <returns>The UTC time-of-day, or null when unparseable.</returns>
    public static TimeSpan? ParseTime(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 6)
        {
            return null;
        }

        if (!int.TryParse(value.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(value.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(value.AsSpan(4), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        if (hours > 23 || minutes > 59 || seconds >= 60)
        {
            return null;
        }

        return new TimeSpan(0, hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Parses an invariant-culture double field, returning null for an empty/invalid field.</summary>
    internal static double? ParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    /// <summary>Parses an invariant-culture integer field, returning null for an empty/invalid field.</summary>
    internal static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
}
