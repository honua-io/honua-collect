using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Reports;

/// <summary>
/// Deferred seam for AI-drafted narrative reports (epic #39): given a form and a
/// record, drafts a human-editable prose summary (overview, findings, recommended
/// actions) that the renderer can fold into a report section. No live model is
/// wired today — the only shipped implementation is <see cref="NullReportNarrator"/>,
/// which returns nothing so callers degrade to the deterministic templated report.
/// A real LLM-backed provider (Anthropic, offline-AI) is the follow-up.
/// </summary>
public interface IReportNarrator
{
    /// <summary>
    /// Drafts a narrative for a record, or returns <see langword="null"/> when no
    /// narrative is available (the default until an AI provider is wired).
    /// </summary>
    /// <param name="form">Form the record was captured against.</param>
    /// <param name="record">Record to summarise.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A drafted narrative, or <see langword="null"/>.</returns>
    Task<ReportNarrative?> DraftAsync(FormDefinition form, FieldRecord record, CancellationToken ct = default);
}

/// <summary>
/// An AI-drafted narrative for a record: a short overview plus optional findings
/// and recommended actions. Human-editable; carried as plain text so it renders
/// through the existing Markdown/PDF/DOCX pipeline without new layout.
/// </summary>
/// <param name="Overview">A one- or two-paragraph summary of the record.</param>
/// <param name="Findings">Notable findings, one per item; may be empty.</param>
/// <param name="RecommendedActions">Suggested follow-ups, one per item; may be empty.</param>
public sealed record ReportNarrative(
    string Overview,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> RecommendedActions);

/// <summary>
/// The shipped, no-op <see cref="IReportNarrator"/>: always returns
/// <see langword="null"/> so reports stay fully deterministic and offline until a
/// real AI provider replaces it. Use <see cref="Instance"/>.
/// </summary>
public sealed class NullReportNarrator : IReportNarrator
{
    /// <summary>The shared instance.</summary>
    public static NullReportNarrator Instance { get; } = new();

    private NullReportNarrator()
    {
    }

    /// <inheritdoc />
    public Task<ReportNarrative?> DraftAsync(FormDefinition form, FieldRecord record, CancellationToken ct = default)
        => Task.FromResult<ReportNarrative?>(null);
}
