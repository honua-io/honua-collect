namespace Honua.Collect.Core.Reports;

/// <summary>
/// Defines a per-record feature report (BACKLOG R1): the templated, human-
/// readable document Survey123 and Fulcrum generate for a single submission.
/// The renderer produces portable Markdown from this; converting Markdown to
/// PDF/Word is the downstream/host step.
/// </summary>
public sealed record ReportTemplate
{
    /// <summary>
    /// Title line, with <c>{fieldId}</c> placeholders substituted from the record
    /// (e.g. <c>"Pole inspection {poleId}"</c>). Falls back to the form name.
    /// </summary>
    public string? TitleTemplate { get; init; }

    /// <summary>Section ids to include, in order. <see langword="null"/> includes all sections.</summary>
    public IReadOnlyList<string>? SectionIds { get; init; }

    /// <summary>Whether to render fields that have no value. Defaults to <see langword="false"/>.</summary>
    public bool IncludeEmptyFields { get; init; }

    /// <summary>Whether to list media attachment file names. Defaults to <see langword="true"/>.</summary>
    public bool IncludeMedia { get; init; } = true;

    /// <summary>Whether to include a record metadata header (id, status). Defaults to <see langword="true"/>.</summary>
    public bool IncludeMetadata { get; init; } = true;

    /// <summary>The default template: all sections, non-empty fields, media and metadata.</summary>
    public static ReportTemplate Default { get; } = new();
}
