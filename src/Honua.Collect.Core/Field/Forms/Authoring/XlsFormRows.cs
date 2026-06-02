namespace Honua.Collect.Core.Field.Forms.Authoring;

/// <summary>
/// One row of an XLSForm <c>survey</c> sheet (BACKLOG F1). The host parses the
/// spreadsheet (xlsx/csv) into these rows; the importer is format-agnostic.
/// </summary>
public sealed record XlsFormSurveyRow
{
    /// <summary>XLSForm type token, e.g. <c>text</c>, <c>select_one colors</c>, <c>begin repeat</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Field/group name (becomes the field or section id).</summary>
    public string? Name { get; init; }

    /// <summary>Display label.</summary>
    public string? Label { get; init; }

    /// <summary>Required flag (<c>yes</c>/<c>true</c>/<c>1</c>).</summary>
    public string? Required { get; init; }

    /// <summary>Relevant expression, e.g. <c>${has_damage} = 'yes'</c>.</summary>
    public string? Relevant { get; init; }

    /// <summary>Calculation expression for <c>calculate</c> rows.</summary>
    public string? Calculation { get; init; }

    /// <summary>Hint / help text.</summary>
    public string? Hint { get; init; }
}

/// <summary>One row of an XLSForm <c>choices</c> sheet.</summary>
public sealed record XlsFormChoiceRow
{
    /// <summary>Choice list name referenced by <c>select_one</c>/<c>select_multiple</c>.</summary>
    public required string ListName { get; init; }

    /// <summary>Stored choice value.</summary>
    public required string Name { get; init; }

    /// <summary>Display label.</summary>
    public string? Label { get; init; }
}
