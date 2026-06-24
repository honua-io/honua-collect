using System.Globalization;

namespace Honua.Collect.Core.Automation;

/// <summary>
/// A safe, data-driven condition guarding an automation rule: a field, an
/// operator, and a comparand — no arbitrary code, so it is sandboxed and runs
/// offline. Evaluated against the record's current values.
/// </summary>
/// <param name="FieldId">The field to test.</param>
/// <param name="Operator">The comparison to apply.</param>
/// <param name="Value">The comparand (ignored for <see cref="ConditionOperator.Exists"/>/<see cref="ConditionOperator.NotExists"/>).</param>
public sealed record RuleCondition(string FieldId, ConditionOperator Operator, object? Value = null) : IRuleCondition
{
    /// <summary>Evaluates the condition against a set of field values.</summary>
    /// <param name="values">The record's current values.</param>
    /// <returns><see langword="true"/> when the condition holds.</returns>
    public bool Matches(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        values.TryGetValue(FieldId, out var actual);

        return Operator switch
        {
            ConditionOperator.Exists => !IsMissing(actual),
            ConditionOperator.NotExists => IsMissing(actual),
            ConditionOperator.Equals => ValuesEqual(actual, Value),
            ConditionOperator.NotEquals => !ValuesEqual(actual, Value),
            ConditionOperator.GreaterThan => CompareNumbers(actual, Value) > 0,
            ConditionOperator.LessThan => CompareNumbers(actual, Value) < 0,
            _ => false,
        };
    }

    private static bool ValuesEqual(object? actual, object? expected)
    {
        if (IsMissing(actual) || IsMissing(expected))
        {
            return IsMissing(actual) && IsMissing(expected);
        }

        if (TryToNumber(actual, out var a) && TryToNumber(expected, out var b))
        {
            return a.Equals(b);
        }

        return string.Equals(ToInvariantString(actual), ToInvariantString(expected), StringComparison.Ordinal);
    }

    // Returns 0 when either side is not numeric, so ordered comparisons fail closed.
    private static int CompareNumbers(object? actual, object? expected)
        => TryToNumber(actual, out var a) && TryToNumber(expected, out var b) ? a.CompareTo(b) : 0;

    private static bool TryToNumber(object? value, out double number)
    {
        switch (value)
        {
            case null:
                number = 0;
                return false;
            case double d:
                number = d;
                return true;
            case int i:
                number = i;
                return true;
            case long l:
                number = l;
                return true;
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

    private static bool IsMissing(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        _ => false,
    };
}
