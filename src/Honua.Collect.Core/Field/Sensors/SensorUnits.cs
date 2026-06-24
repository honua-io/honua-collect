using System.Globalization;

namespace Honua.Collect.Core.Field.Sensors;

/// <summary>
/// Platform-neutral unit conversion for the handful of dimensions sensors in the
/// field commonly emit (temperature, length, pressure). A <see cref="SensorFieldBinding"/>
/// uses this to convert a reading's unit to the unit the form field expects, so a
/// probe reporting <c>degF</c> can drive a field authored in <c>degC</c>.
/// </summary>
public static class SensorUnits
{
    // Canonical unit per dimension, with each known unit's (toCanonical, fromCanonical) affine pair.
    private static readonly Dictionary<string, (string Dimension, Func<double, double> ToCanonical, Func<double, double> FromCanonical)> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Temperature — canonical Celsius.
            ["degc"] = ("temperature", x => x, x => x),
            ["c"] = ("temperature", x => x, x => x),
            ["degf"] = ("temperature", f => (f - 32.0) * 5.0 / 9.0, c => c * 9.0 / 5.0 + 32.0),
            ["f"] = ("temperature", f => (f - 32.0) * 5.0 / 9.0, c => c * 9.0 / 5.0 + 32.0),
            ["k"] = ("temperature", k => k - 273.15, c => c + 273.15),

            // Length — canonical metre.
            ["m"] = ("length", x => x, x => x),
            ["cm"] = ("length", x => x / 100.0, x => x * 100.0),
            ["mm"] = ("length", x => x / 1000.0, x => x * 1000.0),
            ["km"] = ("length", x => x * 1000.0, x => x / 1000.0),
            ["ft"] = ("length", x => x * 0.3048, x => x / 0.3048),
            ["in"] = ("length", x => x * 0.0254, x => x / 0.0254),

            // Pressure — canonical pascal.
            ["pa"] = ("pressure", x => x, x => x),
            ["kpa"] = ("pressure", x => x * 1000.0, x => x / 1000.0),
            ["hpa"] = ("pressure", x => x * 100.0, x => x / 100.0),
            ["mbar"] = ("pressure", x => x * 100.0, x => x / 100.0),
            ["bar"] = ("pressure", x => x * 100000.0, x => x / 100000.0),
        };

    /// <summary>Whether a unit string is one this converter understands.</summary>
    /// <param name="unit">Unit symbol (case-insensitive).</param>
    public static bool IsKnown(string? unit) => unit is not null && Map.ContainsKey(Normalize(unit));

    /// <summary>
    /// Attempts to convert a value between two units of the same dimension.
    /// Treats a <see langword="null"/>/empty <paramref name="from"/> or
    /// <paramref name="to"/> as "no unit": the value passes through unchanged.
    /// </summary>
    /// <param name="value">The value in <paramref name="from"/> units.</param>
    /// <param name="from">Source unit, or null/empty for dimensionless.</param>
    /// <param name="to">Target unit, or null/empty for dimensionless.</param>
    /// <param name="converted">The converted value when successful.</param>
    /// <returns><see langword="true"/> on success; <see langword="false"/> when the units are incompatible.</returns>
    public static bool TryConvert(double value, string? from, string? to, out double converted)
    {
        converted = value;

        // No-unit on either side, or identical units: pass through.
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)
            || string.Equals(Normalize(from), Normalize(to), StringComparison.Ordinal))
        {
            return true;
        }

        if (!Map.TryGetValue(Normalize(from), out var fromUnit) || !Map.TryGetValue(Normalize(to), out var toUnit))
        {
            return false;
        }

        if (!string.Equals(fromUnit.Dimension, toUnit.Dimension, StringComparison.Ordinal))
        {
            return false;
        }

        converted = toUnit.FromCanonical(fromUnit.ToCanonical(value));
        return true;
    }

    private static string Normalize(string unit)
        => unit.Trim().ToLower(CultureInfo.InvariantCulture).Replace("°", string.Empty);
}
