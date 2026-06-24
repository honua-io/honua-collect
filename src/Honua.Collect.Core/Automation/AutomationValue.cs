using System.Globalization;

namespace Honua.Collect.Core.Automation;

/// <summary>
/// Value helpers shared by the automation runtime — chiefly equality used for
/// change detection so the cascade only re-triggers on real value changes (and
/// number-vs-string coercion matches how conditions compare). Pure and offline.
/// </summary>
internal static class AutomationValue
{
    /// <summary>Whether two field values are equal, coercing numerics like conditions do.</summary>
    /// <param name="a">First value.</param>
    /// <param name="b">Second value.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool AreEqual(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (TryToNumber(a, out var na) && TryToNumber(b, out var nb))
        {
            return na.Equals(nb);
        }

        return string.Equals(ToInvariantString(a), ToInvariantString(b), StringComparison.Ordinal);
    }

    private static bool TryToNumber(object? value, out double number)
    {
        switch (value)
        {
            case double d:
                number = d;
                return true;
            case int i:
                number = i;
                return true;
            case long l:
                number = l;
                return true;
            case null:
                number = 0;
                return false;
            default:
                return double.TryParse(ToInvariantString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out number);
        }
    }

    private static string ToInvariantString(object? value) => value switch
    {
        null => string.Empty,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
