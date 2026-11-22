// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Honua.Collect.Core.Field;

internal static class FieldValues
{
    public static bool AreEqual(object? left, object? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (TryAsDouble(left, out var l) && TryAsDouble(right, out var r))
        {
            return Math.Abs(l - r) < 0.000001;
        }

        return string.Equals(ToText(left), ToText(right), StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryAsDouble(object? value, out double parsed)
    {
        switch (value)
        {
            case null:
                parsed = default;
                return false;
            case double d: parsed = d; return true;
            case float f: parsed = f; return true;
            case decimal m: parsed = (double)m; return true;
            case int i: parsed = i; return true;
            case long l: parsed = l; return true;
            default:
                return double.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out parsed);
        }
    }

    public static string ToText(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
