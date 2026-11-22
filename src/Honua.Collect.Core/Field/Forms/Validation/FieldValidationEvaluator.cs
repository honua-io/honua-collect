// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Forms.Expressions;

namespace Honua.Collect.Core.Field.Forms.Validation;

/// <summary>
/// Evaluates product-level <see cref="FormFieldValidationRule"/>s against a live
/// <see cref="FieldState"/> and the current record values: conditional
/// requiredness, regex pattern matching (with a ReDoS-safe timeout), numeric
/// range bounds, and text length bounds.
/// </summary>
public static class FieldValidationEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Computes whether <paramref name="field"/> is currently required, combining
    /// its static <see cref="FormField.Required"/> flag with any static or
    /// conditional <see cref="FormFieldValidationRule"/> targeting the field.
    /// </summary>
    /// <param name="field">The field definition.</param>
    /// <param name="rules">Validation rules active for the form, or <see langword="null"/>.</param>
    /// <param name="values">Current record values used to evaluate conditional expressions.</param>
    /// <returns><see langword="true"/> when a value is currently required.</returns>
    public static bool IsRequired(
        FormField field,
        IReadOnlyList<FormFieldValidationRule>? rules,
        IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(values);

        if (field.Required)
        {
            return true;
        }

        if (rules is null || rules.Count == 0)
        {
            return false;
        }

        foreach (var rule in rules)
        {
            if (!string.Equals(rule.FieldId, field.FieldId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsRuleRequiringField(rule, values))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Evaluates all <paramref name="rules"/> matching <paramref name="state"/>.
    /// Hidden fields always produce zero errors.
    /// </summary>
    /// <param name="state">The live field state.</param>
    /// <param name="rules">Validation rules active for the form, or <see langword="null"/>.</param>
    /// <param name="values">Current record values.</param>
    /// <param name="sdkAlreadyFlaggedRequired">
    /// Set to <see langword="true"/> when the SDK <see cref="FormValidator"/>
    /// already reported a missing-value error for this field so duplicate
    /// required errors are suppressed.
    /// </param>
    /// <returns>Validation errors produced by the product rules.</returns>
    public static IReadOnlyList<FormValidationError> Evaluate(
        FieldState state,
        IReadOnlyList<FormFieldValidationRule>? rules,
        IReadOnlyDictionary<string, object?> values,
        bool sdkAlreadyFlaggedRequired = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(values);

        if (!state.IsVisible || rules is null || rules.Count == 0)
        {
            return [];
        }

        var matchingRules = rules
            .Where(r => string.Equals(r.FieldId, state.FieldId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingRules.Count == 0)
        {
            return [];
        }

        var errors = new List<FormValidationError>();
        var isMissing = IsMissing(state);

        if (isMissing)
        {
            if (!sdkAlreadyFlaggedRequired)
            {
                var requiringRule = matchingRules.FirstOrDefault(r => IsRuleRequiringField(r, values));
                if (requiringRule is not null)
                {
                    errors.Add(new FormValidationError(
                        state.FieldId,
                        requiringRule.RequiredMessage ?? $"{state.Field.Label} is required."));
                }
            }

            return errors;
        }

        foreach (var rule in matchingRules)
        {
            if (!string.IsNullOrWhiteSpace(rule.When) &&
                !ExpressionEvaluator.EvaluateBoolean(rule.When, values))
            {
                continue;
            }

            EvaluatePattern(state, rule, errors);
            EvaluateRange(state, rule, errors);
            EvaluateLength(state, rule, errors);
        }

        return errors;
    }

    private static bool IsRuleRequiringField(
        FormFieldValidationRule rule,
        IReadOnlyDictionary<string, object?> values)
        => rule.Required ||
           (!string.IsNullOrWhiteSpace(rule.RequiredWhen) &&
            ExpressionEvaluator.EvaluateBoolean(rule.RequiredWhen, values));

    private static bool IsMissing(FieldState state)
    {
        if (FormFieldTypes.IsMedia(state.Field.Type) && state.Media.Count > 0)
        {
            return false;
        }

        return state.Value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            ICollection collection => collection.Count == 0,
            IEnumerable enumerable => !HasAnyElement(enumerable),
            _ => false,
        };
    }

    private static bool HasAnyElement(IEnumerable enumerable)
    {
        var enumerator = enumerable.GetEnumerator();
        try
        {
            return enumerator.MoveNext();
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    private static void EvaluatePattern(
        FieldState state,
        FormFieldValidationRule rule,
        List<FormValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern))
        {
            return;
        }

        var text = FieldValues.ToText(state.Value);
        try
        {
            if (!Regex.IsMatch(text, rule.Pattern, RegexOptions.CultureInvariant, RegexTimeout))
            {
                errors.Add(new FormValidationError(
                    state.FieldId,
                    rule.PatternMessage ?? $"{state.Field.Label} does not match the required format."));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            errors.Add(new FormValidationError(
                state.FieldId,
                rule.PatternMessage ?? $"{state.Field.Label} could not be validated against the required pattern."));
        }
    }

    private static void EvaluateRange(
        FieldState state,
        FormFieldValidationRule rule,
        List<FormValidationError> errors)
    {
        if (!rule.MinValue.HasValue && !rule.MaxValue.HasValue)
        {
            return;
        }

        if (!FieldValues.TryAsDouble(state.Value, out var numeric) ||
            double.IsNaN(numeric) ||
            double.IsInfinity(numeric))
        {
            errors.Add(new FormValidationError(
                state.FieldId,
                rule.RangeMessage ?? $"{state.Field.Label} must be a valid number."));
            return;
        }

        var belowMin = rule.MinValue.HasValue &&
            (rule.MinExclusive ? numeric <= rule.MinValue.Value : numeric < rule.MinValue.Value);
        var aboveMax = rule.MaxValue.HasValue &&
            (rule.MaxExclusive ? numeric >= rule.MaxValue.Value : numeric > rule.MaxValue.Value);

        if (belowMin || aboveMax)
        {
            errors.Add(new FormValidationError(
                state.FieldId,
                rule.RangeMessage ?? BuildRangeMessage(state.Field.Label, rule)));
        }
    }

    private static void EvaluateLength(
        FieldState state,
        FormFieldValidationRule rule,
        List<FormValidationError> errors)
    {
        if (!rule.MinLength.HasValue && !rule.MaxLength.HasValue)
        {
            return;
        }

        var length = FieldValues.ToText(state.Value).Length;
        var tooShort = rule.MinLength.HasValue && length < rule.MinLength.Value;
        var tooLong = rule.MaxLength.HasValue && length > rule.MaxLength.Value;

        if (tooShort || tooLong)
        {
            errors.Add(new FormValidationError(
                state.FieldId,
                rule.LengthMessage ?? BuildLengthMessage(state.Field.Label, rule)));
        }
    }

    private static string BuildRangeMessage(string label, FormFieldValidationRule rule)
    {
        if (rule.MinValue.HasValue && rule.MaxValue.HasValue)
        {
            var minText = rule.MinValue.Value.ToString(CultureInfo.InvariantCulture);
            var maxText = rule.MaxValue.Value.ToString(CultureInfo.InvariantCulture);
            return $"{label} must be between {minText} and {maxText}.";
        }

        if (rule.MinValue.HasValue)
        {
            var op = rule.MinExclusive ? "greater than" : "at least";
            return $"{label} must be {op} {rule.MinValue.Value.ToString(CultureInfo.InvariantCulture)}.";
        }

        var maxOp = rule.MaxExclusive ? "less than" : "at most";
        return $"{label} must be {maxOp} {rule.MaxValue!.Value.ToString(CultureInfo.InvariantCulture)}.";
    }

    private static string BuildLengthMessage(string label, FormFieldValidationRule rule)
    {
        if (rule.MinLength.HasValue && rule.MaxLength.HasValue)
        {
            return $"{label} must be between {rule.MinLength.Value} and {rule.MaxLength.Value} characters.";
        }

        if (rule.MinLength.HasValue)
        {
            return $"{label} must be at least {rule.MinLength.Value} characters.";
        }

        return $"{label} must be at most {rule.MaxLength!.Value} characters.";
    }
}
