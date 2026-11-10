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
/// CSV/GeoJSON export text and the per-record report Markdown. The MAUI page
/// keeps only file-writing and share-sheet glue; all formatting and record
/// selection lives here so it is unit-testable without a device.
/// </summary>
public sealed class ExportViewModel : ObservableObject
{
    private readonly FormDefinition _form;
    private readonly IReadOnlyList<FieldRecord> _records;
    private readonly RecordReportRenderer _reportRenderer = new();

    /// <summary>Creates the export view-model over a form and its records.</summary>
    /// <param name="form">Form whose fields define the export columns/properties.</param>
    /// <param name="records">Captured records to export and report on.</param>
    public ExportViewModel(FormDefinition form, IEnumerable<FieldRecord> records)
    {
        _form = form ?? throw new ArgumentNullException(nameof(form));
        ArgumentNullException.ThrowIfNull(records);
        _records = records.ToList();
    }

    /// <summary>Number of records available to export.</summary>
    public int RecordCount => _records.Count;

    /// <summary>Whether there is at least one record to export or report on.</summary>
    public bool HasRecords => _records.Count > 0;

    /// <summary>A one-line status for the screen header.</summary>
    public string Header => $"{RecordCount} record(s) ready to export.";

    /// <summary>A suggested base file name (without extension) for shared exports.</summary>
    public string FileBaseName => string.IsNullOrWhiteSpace(_form.FormId) ? "records" : _form.FormId;

    /// <summary>
    /// Produces the CSV export for all records, delegating to the Core exporter.
    /// </summary>
    /// <returns>CSV text including a header row.</returns>
    public string ExportCsv() => RecordExporter.Export(_form, _records, ExportFormat.Csv);

    /// <summary>
    /// Produces the GeoJSON export for all records, delegating to the Core exporter.
    /// </summary>
    /// <returns>GeoJSON <c>FeatureCollection</c> text.</returns>
    public string ExportGeoJson() => RecordExporter.Export(_form, _records, ExportFormat.GeoJson);

    /// <summary>
    /// Renders the per-record Markdown report for the most recently created record.
    /// </summary>
    /// <param name="template">Report template, or the default when null.</param>
    /// <returns>The Markdown report.</returns>
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
