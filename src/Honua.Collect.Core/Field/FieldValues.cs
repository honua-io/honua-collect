using System.Globalization;

namespace Honua.Collect.Core.Field;

/// <summary>
/// Shared value-coercion helpers for comparing portable field values. Form
/// values arrive as loosely typed <see cref="object"/>s (a number may be an
/// <see cref="int"/>, a <see cref="double"/>, or a numeric string), so equality
/// and ordering need consistent coercion. Used by both the form runtime
/// (visibility evaluation) and the conflict detector so they agree on whether
/// two values are "the same".
/// </summary>
internal static class FieldValues
{
    /// <summary>Loosely-typed equality: numeric when both coerce to numbers, else case-insensitive text.</summary>
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

    /// <summary>Attempts to coerce a value to a <see cref="double"/>.</summary>
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

    /// <summary>Renders a value to its invariant text form.</summary>
    public static string ToText(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
