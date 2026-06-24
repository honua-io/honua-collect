using Honua.Collect.Core.Editions;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Reports;

/// <summary>The binary format a bulk report item is rendered to.</summary>
public enum ReportOutputFormat
{
    /// <summary>Portable Markdown (UTF-8).</summary>
    Markdown,

    /// <summary>PDF, via <see cref="PdfReportWriter"/>.</summary>
    Pdf,

    /// <summary>Microsoft Word <c>.docx</c>, via <see cref="DocxReportWriter"/>.</summary>
    Docx,
}

/// <summary>
/// One rendered report in a bulk run: the source record id, the suggested file
/// name (template-driven, sanitised, de-duplicated within the manifest) and the
/// rendered bytes. The host writes these to disk or zips them.
/// </summary>
/// <param name="RecordId">The record this report was rendered from.</param>
/// <param name="FileName">A safe, unique file name including the format extension.</param>
/// <param name="Content">The rendered report bytes (UTF-8 for Markdown).</param>
public sealed record ReportManifestEntry(string RecordId, string FileName, byte[] Content);

/// <summary>
/// The result of a bulk report run: one <see cref="ReportManifestEntry"/> per input
/// record, in input order. Deterministic and offline — a faithful fan-out of the
/// single-record <see cref="RecordReportRenderer"/> over a record set.
/// </summary>
/// <param name="Format">The format every entry was rendered to.</param>
/// <param name="Entries">The rendered reports, one per input record.</param>
public sealed record ReportManifest(ReportOutputFormat Format, IReadOnlyList<ReportManifestEntry> Entries)
{
    /// <summary>Number of reports produced.</summary>
    public int Count => Entries.Count;
}

/// <summary>
/// Generates reports for a <em>set</em> of records (epic #39): iterates the existing
/// single-record <see cref="RecordReportRenderer"/> across a whole form's submissions
/// or a filtered selection, applying one <see cref="ReportTemplate"/> to every record,
/// and returns a <see cref="ReportManifest"/> of named outputs. Deterministic and
/// offline; the entitlement check rides through the wrapped renderer.
/// </summary>
public sealed class BulkReportGenerator
{
    private readonly RecordReportRenderer _renderer;

    /// <summary>Creates a bulk generator over a single-record renderer.</summary>
    /// <param name="renderer">The renderer used for each record; carries the entitlement gate.</param>
    public BulkReportGenerator(RecordReportRenderer renderer)
        => _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    /// <summary>Convenience: builds the generator from entitlements.</summary>
    /// <param name="entitlements">Edition entitlements; reports require Pro.</param>
    public BulkReportGenerator(IEntitlements entitlements)
        : this(new RecordReportRenderer(entitlements))
    {
    }

    /// <summary>Renders a report for every record into a manifest.</summary>
    /// <param name="form">Form definition shared by the records.</param>
    /// <param name="records">Records to render; an empty set yields an empty manifest.</param>
    /// <param name="template">Template applied to every record, or the default when null.</param>
    /// <param name="format">Output format for every report.</param>
    /// <param name="fileNameTemplate">
    /// File-name stem with <c>{fieldId}</c> placeholders (e.g. <c>"pole-{poleId}"</c>);
    /// falls back to the record id. The format extension is appended automatically.
    /// </param>
    /// <returns>A manifest with one entry per input record, in input order.</returns>
    public ReportManifest Generate(
        FormDefinition form,
        IEnumerable<FieldRecord> records,
        ReportTemplate? template = null,
        ReportOutputFormat format = ReportOutputFormat.Markdown,
        string? fileNameTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(records);

        var tpl = template ?? ReportTemplate.Default;
        var entries = new List<ReportManifestEntry>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extension = Extension(format);

        foreach (var record in records)
        {
            var markdown = _renderer.RenderMarkdown(form, record, tpl);
            var content = Render(markdown, format);

            var stem = BuildStem(record, fileNameTemplate);
            var fileName = Uniquify(stem + extension, usedNames);
            entries.Add(new ReportManifestEntry(record.RecordId, fileName, content));
        }

        return new ReportManifest(format, entries);
    }

    private static byte[] Render(string markdown, ReportOutputFormat format) => format switch
    {
        ReportOutputFormat.Markdown => System.Text.Encoding.UTF8.GetBytes(markdown),
        ReportOutputFormat.Pdf => PdfReportWriter.Write(markdown),
        ReportOutputFormat.Docx => DocxReportWriter.Write(markdown),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported report format."),
    };

    private static string Extension(ReportOutputFormat format) => format switch
    {
        ReportOutputFormat.Markdown => ".md",
        ReportOutputFormat.Pdf => ".pdf",
        ReportOutputFormat.Docx => ".docx",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported report format."),
    };

    private static string BuildStem(FieldRecord record, string? fileNameTemplate)
    {
        var raw = string.IsNullOrWhiteSpace(fileNameTemplate)
            ? record.RecordId
            : Substitute(fileNameTemplate, record);

        var stem = Sanitize(raw);
        return string.IsNullOrEmpty(stem) ? Sanitize(record.RecordId) : stem;
    }

    private static string Substitute(string template, FieldRecord record)
    {
        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0)
            {
                result.Append(template, i, template.Length - i);
                break;
            }

            var close = template.IndexOf('}', open);
            if (close < 0)
            {
                result.Append(template, i, template.Length - i);
                break;
            }

            result.Append(template, i, open - i);
            var key = template[(open + 1)..close];
            record.Values.TryGetValue(key, out var value);
            result.Append(value?.ToString() ?? string.Empty);
            i = close + 1;
        }

        return result.ToString();
    }

    private static string Sanitize(string value)
    {
        var chars = value.Trim().Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var collapsed = new string(chars).Trim('-');
        return collapsed.Length == 0 ? string.Empty : collapsed;
    }

    private static string Uniquify(string fileName, HashSet<string> used)
    {
        if (used.Add(fileName))
        {
            return fileName;
        }

        var dot = fileName.LastIndexOf('.');
        var stem = dot < 0 ? fileName : fileName[..dot];
        var ext = dot < 0 ? string.Empty : fileName[dot..];

        for (var n = 2; ; n++)
        {
            var candidate = $"{stem}-{n}{ext}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
