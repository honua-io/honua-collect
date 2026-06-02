using System.Collections;
using System.Globalization;
using System.Text;
using Honua.Collect.Core.Editions;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Reports;

/// <summary>
/// Renders a per-record feature report (BACKLOG R1) to Markdown from a form, a
/// record, and a <see cref="ReportTemplate"/>. Repeatable sections render each
/// captured row; media fields list attachment names. This is a Pro capability
/// (<c>ReportsAndExports</c>), gated through <see cref="CollectEntitlements"/>.
/// </summary>
public sealed class RecordReportRenderer
{
    private readonly CollectEntitlements _entitlements;

    /// <summary>Creates a renderer for an entitlement context.</summary>
    /// <param name="entitlements">Edition entitlements; reports require Pro.</param>
    public RecordReportRenderer(CollectEntitlements entitlements)
        => _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));

    /// <summary>Renders a record to a Markdown report.</summary>
    /// <param name="form">Form definition.</param>
    /// <param name="record">Record to render.</param>
    /// <param name="template">Report template, or <see cref="ReportTemplate.Default"/> when null.</param>
    /// <returns>The Markdown report.</returns>
    public string RenderMarkdown(FormDefinition form, FieldRecord record, ReportTemplate? template = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(record);
        _entitlements.Require(CollectFeature.ReportsAndExports);

        var tpl = template ?? ReportTemplate.Default;
        var builder = new StringBuilder();

        builder.Append("# ").AppendLine(RenderTitle(form, record, tpl));
        builder.AppendLine();

        if (tpl.IncludeMetadata)
        {
            builder.Append("- **Record:** ").AppendLine(record.RecordId);
            builder.Append("- **Status:** ").AppendLine(record.Status.ToString());
            if (record.Location is { } loc)
            {
                builder.Append("- **Location:** ")
                    .AppendLine($"{loc.Latitude.ToString(CultureInfo.InvariantCulture)}, {loc.Longitude.ToString(CultureInfo.InvariantCulture)}");
            }

            builder.AppendLine();
        }

        var include = tpl.SectionIds is null ? null : new HashSet<string>(tpl.SectionIds, StringComparer.OrdinalIgnoreCase);

        foreach (var section in form.Sections)
        {
            if (include is not null && !include.Contains(section.SectionId))
            {
                continue;
            }

            RenderSection(builder, section, record, tpl);
        }

        return builder.ToString();
    }

    private void RenderSection(StringBuilder builder, FormSection section, FieldRecord record, ReportTemplate tpl)
    {
        builder.Append("## ").AppendLine(section.Label);

        if (section.Repeatable)
        {
            var rows = ReadRows(record, section.SectionId);
            if (rows.Count == 0)
            {
                builder.AppendLine("_No entries._").AppendLine();
                return;
            }

            for (var i = 0; i < rows.Count; i++)
            {
                builder.Append("### ").Append(section.Label).Append(' ').AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
                RenderFields(builder, section.Fields, rows[i], record, tpl);
            }

            return;
        }

        RenderFields(builder, section.Fields, record.Values, record, tpl);
        builder.AppendLine();
    }

    private void RenderFields(
        StringBuilder builder,
        IEnumerable<FormField> fields,
        IReadOnlyDictionary<string, object?> values,
        FieldRecord record,
        ReportTemplate tpl)
    {
        foreach (var field in fields)
        {
            if (IsMediaField(field.Type))
            {
                if (!tpl.IncludeMedia)
                {
                    continue;
                }

                var names = record.Media
                    .Where(m => string.IsNullOrWhiteSpace(m.FieldId) || string.Equals(m.FieldId, field.FieldId, StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.FileName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();

                if (names.Count == 0 && !tpl.IncludeEmptyFields)
                {
                    continue;
                }

                builder.Append("- **").Append(field.Label).Append(":** ")
                    .AppendLine(names.Count == 0 ? "_none_" : string.Join(", ", names));
                continue;
            }

            values.TryGetValue(field.FieldId, out var value);
            var text = FormatValue(value);

            if (string.IsNullOrEmpty(text) && !tpl.IncludeEmptyFields)
            {
                continue;
            }

            builder.Append("- **").Append(field.Label).Append(":** ")
                .AppendLine(string.IsNullOrEmpty(text) ? "_—_" : text);
        }
    }

    private static string RenderTitle(FormDefinition form, FieldRecord record, ReportTemplate tpl)
    {
        if (string.IsNullOrWhiteSpace(tpl.TitleTemplate))
        {
            return form.Name;
        }

        var result = new StringBuilder();
        var template = tpl.TitleTemplate;
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
            result.Append(FormatValue(value));
            i = close + 1;
        }

        return result.ToString();
    }

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "Yes" : "No",
        string s => s,
        IEnumerable enumerable and not string => string.Join(", ", enumerable.Cast<object?>().Select(FormatValue)),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadRows(FieldRecord record, string sectionId)
    {
        if (!record.Values.TryGetValue(sectionId, out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            IEnumerable<IReadOnlyDictionary<string, object?>> typed => typed.ToList(),
            IEnumerable<IDictionary<string, object?>> dicts =>
                dicts.Select(d => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(d, StringComparer.OrdinalIgnoreCase)).ToList(),
            _ => [],
        };
    }

    private static bool IsMediaField(FormFieldType type)
        => type is FormFieldType.Photo or FormFieldType.Video or FormFieldType.Audio
            or FormFieldType.Signature or FormFieldType.Sketch or FormFieldType.File;
}
