// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Forms.Validation;

/// <summary>
/// Product-level data-quality validation rule for a form field: static or
/// conditional requiredness, regular-expression pattern matching, numeric range
/// bounds, and text length bounds. Evaluated live by <see cref="FormSession"/>
/// alongside the SDK's built-in <see cref="Honua.Sdk.Field.Forms.FormValidator"/>.
/// </summary>
public sealed record FormFieldValidationRule
{
    /// <summary>Identifier of the target field.</summary>
    public required string FieldId { get; init; }

    /// <summary>
    /// Marks the field unconditionally required (in addition to
    /// <see cref="Honua.Sdk.Field.Forms.FormField.Required"/>).
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Optional boolean expression (for example <c>${hasDamage} = true</c>) that
    /// makes the field required when it evaluates to <see langword="true"/>.
    /// </summary>
    public string? RequiredWhen { get; init; }

    /// <summary>Custom error message when the field is required and missing.</summary>
    public string? RequiredMessage { get; init; }

    /// <summary>
    /// Optional guard expression. When supplied, pattern, range, and length
    /// checks only run while this expression evaluates to <see langword="true"/>.
    /// </summary>
    public string? When { get; init; }

    /// <summary>Optional regular-expression pattern the field's text value must match.</summary>
    public string? Pattern { get; init; }

    /// <summary>Custom error message when <see cref="Pattern"/> fails.</summary>
    public string? PatternMessage { get; init; }

    /// <summary>Optional lower numeric bound.</summary>
    public double? MinValue { get; init; }

    /// <summary>Optional upper numeric bound.</summary>
    public double? MaxValue { get; init; }

    /// <summary>When <see langword="true"/>, values equal to <see cref="MinValue"/> fail.</summary>
    public bool MinExclusive { get; init; }

    /// <summary>When <see langword="true"/>, values equal to <see cref="MaxValue"/> fail.</summary>
    public bool MaxExclusive { get; init; }

    /// <summary>Custom error message when <see cref="MinValue"/> or <see cref="MaxValue"/> fails.</summary>
    public string? RangeMessage { get; init; }

    /// <summary>Optional minimum text length (inclusive).</summary>
    public int? MinLength { get; init; }

    /// <summary>Optional maximum text length (inclusive).</summary>
    public int? MaxLength { get; init; }

    /// <summary>Custom error message when <see cref="MinLength"/> or <see cref="MaxLength"/> fails.</summary>
    public string? LengthMessage { get; init; }
}
