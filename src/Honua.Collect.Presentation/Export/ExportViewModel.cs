using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Export;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Reports;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Export;

/// <summary>
/// View-model for the bulk reporting + export screen (BACKLOG R1/R2). It is the
/// platform-neutral seam over the Core <see cref="RecordExporter"/> and
/// <see cref="RecordReportRenderer"/>: it exposes the record count, produces the
/// CSV/GeoJSON export text and the per-record report Markdown, and reports
/// whether the current edition unlocks these Pro capabilities. The MAUI page
/// keeps only file-writing and share-sheet glue; all formatting and record
/// selection lives here so it is unit-testable without a device.
/// </summary>
public sealed class ExportViewModel : ObservableObject
{
    private readonly FormDefinition _form;
    private readonly IReadOnlyList<FieldRecord> _records;
    private readonly CollectEntitlements _entitlements;
    private readonly RecordReportRenderer _reportRenderer;

    /// <summary>Creates the export view-model over a form, its records, and an edition.</summary>
    /// <param name="form">Form whose fields define the export columns/properties.</param>
    /// <param name="records">Captured records to export and report on.</param>
    /// <param name="edition">The licensing edition in effect; reports/exports require Pro.</param>
    public ExportViewModel(FormDefinition form, IEnumerable<FieldRecord> records, CollectEdition edition)
    {
        _form = form ?? throw new ArgumentNullException(nameof(form));
        ArgumentNullException.ThrowIfNull(records);
        _records = records.ToList();
        _entitlements = new CollectEntitlements(edition);
        _reportRenderer = new RecordReportRenderer(_entitlements);
    }

    /// <summary>Number of records available to export.</summary>
    public int RecordCount => _records.Count;

    /// <summary>Whether reports and exports are unlocked by the current edition.</summary>
    public bool IsEntitled => _entitlements.Allows(CollectFeature.ReportsAndExports);

    /// <summary>Whether there is at least one record to export or report on.</summary>
    public bool HasRecords => _records.Count > 0;

    /// <summary>A one-line status for the screen header.</summary>
    public string Header => IsEntitled
        ? $"{RecordCount} record(s) ready to export."
        : "Reporting and bulk export require the Pro edition.";

    /// <summary>A suggested base file name (without extension) for shared exports.</summary>
    public string FileBaseName => string.IsNullOrWhiteSpace(_form.FormId) ? "records" : _form.FormId;

    /// <summary>
    /// Produces the CSV export for all records, delegating to the Core exporter.
    /// </summary>
    /// <returns>CSV text including a header row.</returns>
    /// <exception cref="FeatureNotEntitledException">When the edition is not Pro.</exception>
    public string ExportCsv()
    {
        _entitlements.Require(CollectFeature.ReportsAndExports);
        return RecordExporter.Export(_form, _records, ExportFormat.Csv);
    }

    /// <summary>
    /// Produces the GeoJSON export for all records, delegating to the Core exporter.
    /// </summary>
    /// <returns>GeoJSON <c>FeatureCollection</c> text.</returns>
    /// <exception cref="FeatureNotEntitledException">When the edition is not Pro.</exception>
    public string ExportGeoJson()
    {
        _entitlements.Require(CollectFeature.ReportsAndExports);
        return RecordExporter.Export(_form, _records, ExportFormat.GeoJson);
    }

    /// <summary>
    /// Renders the per-record Markdown report for the most recently created record.
    /// </summary>
    /// <param name="template">Report template, or the default when null.</param>
    /// <returns>The Markdown report.</returns>
    /// <exception cref="FeatureNotEntitledException">When the edition is not Pro.</exception>
    /// <exception cref="InvalidOperationException">When there are no records.</exception>
    public string RenderLatestReport(ReportTemplate? template = null)
    {
        if (LatestRecord is not { } record)
        {
            throw new InvalidOperationException("There are no records to report on.");
        }

        return _reportRenderer.RenderMarkdown(_form, record, template);
    }

    /// <summary>The most recently created record, or null when none.</summary>
    public FieldRecord? LatestRecord =>
        _records.Count == 0 ? null : _records.MaxBy(r => r.CreatedAtUtc);
}
