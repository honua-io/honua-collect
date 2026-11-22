// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Honua.Collect.Core.Field.Sensors;

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

    public static bool IsKnown(string? unit) => unit is not null && Map.ContainsKey(Normalize(unit));

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
