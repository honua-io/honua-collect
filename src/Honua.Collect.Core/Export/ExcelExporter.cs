using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Export;

/// <summary>
/// Exports captured records to a Microsoft Excel <c>.xlsx</c> workbook (BACKLOG R2),
/// the structured tabular format spreadsheet users expect alongside CSV. A
/// <c>.xlsx</c> is an Open Packaging zip of SpreadsheetML XML parts; this writes the
/// minimal valid set (content types, package + workbook relationships, the workbook,
/// and a single worksheet) with no third-party rendering library — the same
/// dependency-free OpenXML-over-zip approach as <see cref="Reports.DocxReportWriter"/>.
/// </summary>
/// <remarks>
/// Cells are typed: numeric and boolean field values become real number/boolean cells
/// (so Excel sums/filters them), and everything else is written as an inline string,
/// reusing <see cref="RecordExporter.CellText"/> for non-scalar/media values so the
/// text matches the CSV/GeoJSON/KML exports. Like CSV, columns are driven by the
/// <see cref="FormDefinition"/> so every record exports a stable, complete column set.
/// </remarks>
public static class ExcelExporter
{
    private const string SpreadsheetMlNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly string[] FixedColumns = ["record_id", "status", "latitude", "longitude"];

    /// <summary>Exports records to an .xlsx workbook as a byte array.</summary>
    /// <param name="form">Form whose fields define the columns after the fixed record columns.</param>
    /// <param name="records">Records to export, one row each (plus a header row).</param>
    /// <param name="sheetName">The worksheet name (trimmed to Excel's 31-char limit).</param>
    /// <returns>The .xlsx file bytes.</returns>
    public static byte[] Export(FormDefinition form, IEnumerable<FieldRecord> records, string sheetName = "Records")
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);

        var fields = form.Sections.SelectMany(s => s.Fields).ToList();
        var headers = FixedColumns.Concat(fields.Select(f => f.FieldId)).ToList();

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "[Content_Types].xml", ContentTypes());
            AddEntry(zip, "_rels/.rels", RootRelationships());
            AddEntry(zip, "xl/workbook.xml", Workbook(sheetName));
            AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelationships());
            AddEntry(zip, "xl/worksheets/sheet1.xml", Worksheet(fields, headers, records));
        }

        return buffer.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string path, string content)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes);
    }

    private static string ContentTypes() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        </Types>
        """;

    private static string RootRelationships() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private static string WorkbookRelationships() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
        </Relationships>
        """;

    private static string Workbook(string sheetName)
    {
        // Excel caps sheet names at 31 chars and forbids a handful of characters;
        // sanitise to keep the workbook openable regardless of the caller's input.
        var safe = SanitizeSheetName(sheetName);

        var output = new StringBuilder();
        using var writer = XmlWriter.Create(output, Settings());
        writer.WriteStartElement(null, "workbook", SpreadsheetMlNamespace);
        writer.WriteAttributeString("xmlns", "r", null, RelationshipsNamespace);
        writer.WriteStartElement("sheets", SpreadsheetMlNamespace);
        writer.WriteStartElement("sheet", SpreadsheetMlNamespace);
        writer.WriteAttributeString("name", safe);
        writer.WriteAttributeString("sheetId", "1");
        writer.WriteAttributeString("r", "id", RelationshipsNamespace, "rId1");
        writer.WriteEndElement(); // sheet
        writer.WriteEndElement(); // sheets
        writer.WriteEndElement(); // workbook
        writer.Flush();
        return Declaration + output;
    }

    private static string Worksheet(IReadOnlyList<FormField> fields, IReadOnlyList<string> headers, IEnumerable<FieldRecord> records)
    {
        var output = new StringBuilder();
        using var writer = XmlWriter.Create(output, Settings());
        writer.WriteStartElement(null, "worksheet", SpreadsheetMlNamespace);
        writer.WriteStartElement("sheetData", SpreadsheetMlNamespace);

        var rowIndex = 1;
        WriteRow(writer, rowIndex++, headers.Select(h => Cell.Text(h)));

        foreach (var record in records)
        {
            var cells = new List<Cell>
            {
                Cell.Text(record.RecordId),
                Cell.Text(record.Status.ToString()),
                NumberOrBlank(record.Location?.Latitude),
                NumberOrBlank(record.Location?.Longitude),
            };

            foreach (var field in fields)
            {
                cells.Add(CellFor(record, field));
            }

            WriteRow(writer, rowIndex++, cells);
        }

        writer.WriteEndElement(); // sheetData
        writer.WriteEndElement(); // worksheet
        writer.Flush();
        return Declaration + output;
    }

    private static void WriteRow(XmlWriter writer, int rowIndex, IEnumerable<Cell> cells)
    {
        writer.WriteStartElement("row", SpreadsheetMlNamespace);
        writer.WriteAttributeString("r", rowIndex.ToString(CultureInfo.InvariantCulture));

        var column = 0;
        foreach (var cell in cells)
        {
            var reference = $"{ColumnName(column++)}{rowIndex}";
            cell.Write(writer, reference);
        }

        writer.WriteEndElement(); // row
    }

    /// <summary>
    /// Builds a typed cell from a record's field value, mirroring the type handling in
    /// <see cref="RecordExporter"/>'s GeoJSON writer: numbers and booleans keep their
    /// type; everything else (text, choices, dates, media, collections) becomes an
    /// inline string via the shared <see cref="RecordExporter.CellText"/>.
    /// </summary>
    private static Cell CellFor(FieldRecord record, FormField field)
    {
        if (record.Values.TryGetValue(field.FieldId, out var value) && value is not null && !IsMediaField(field.Type))
        {
            switch (value)
            {
                case bool b:
                    return Cell.Boolean(b);
                case JsonElement { ValueKind: JsonValueKind.Number } element:
                    return Cell.Number(element.GetDouble());
                case JsonElement { ValueKind: JsonValueKind.True }:
                    return Cell.Boolean(true);
                case JsonElement { ValueKind: JsonValueKind.False }:
                    return Cell.Boolean(false);
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    return Cell.Number(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                case float or double or decimal:
                    return Cell.Number(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                case string or IEnumerable:
                    break; // strings and collections fall through to shared text formatting
            }
        }

        return Cell.Text(RecordExporter.CellText(record, field));
    }

    private static Cell NumberOrBlank(double? value)
        => value is { } v ? Cell.Number(v) : Cell.Text(string.Empty);

    private static bool IsMediaField(FormFieldType type)
        => type is FormFieldType.Photo or FormFieldType.Video or FormFieldType.Audio
            or FormFieldType.Signature or FormFieldType.Sketch or FormFieldType.File;

    private static string SanitizeSheetName(string name)
    {
        var cleaned = new string(name.Select(c => "[]:*?/\\".Contains(c, StringComparison.Ordinal) ? ' ' : c).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "Records";
        }

        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }

    /// <summary>Excel column letters: 0 -> A, 25 -> Z, 26 -> AA, ...</summary>
    private static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var i = index; ; i = i / 26 - 1)
        {
            name = (char)('A' + i % 26) + name;
            if (i < 26)
            {
                break;
            }
        }

        return name;
    }

    // XmlWriter over a StringBuilder always declares utf-16; we encode the result as
    // UTF-8 bytes, so suppress its declaration and prepend a matching UTF-8 one.
    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    private static XmlWriterSettings Settings()
        => new() { OmitXmlDeclaration = true, ConformanceLevel = ConformanceLevel.Fragment, Indent = false };

    /// <summary>A single worksheet cell: its SpreadsheetML type and serialized value.</summary>
    private readonly struct Cell
    {
        private readonly string? _type;     // null = numeric (the default), "str", "b"
        private readonly string _value;
        private readonly bool _inline;       // true => write as an inlineStr (<is><t>)

        private Cell(string? type, string value, bool inline)
        {
            _type = type;
            _value = value;
            _inline = inline;
        }

        public static Cell Number(double value)
            => new(null, value.ToString("R", CultureInfo.InvariantCulture), inline: false);

        public static Cell Boolean(bool value)
            => new("b", value ? "1" : "0", inline: false);

        public static Cell Text(string value)
            // Neutralize spreadsheet formula injection (CWE-1236) on every string
            // cell, matching the CSV exporter: an untrusted value starting with
            // = + - @ or a control char is otherwise evaluated as a formula in Excel.
            => new("inlineStr", RecordExporter.NeutralizeFormula(value), inline: true);

        public void Write(XmlWriter writer, string reference)
        {
            writer.WriteStartElement("c", SpreadsheetMlNamespace);
            writer.WriteAttributeString("r", reference);
            if (_type is not null)
            {
                writer.WriteAttributeString("t", _type);
            }

            if (_inline)
            {
                writer.WriteStartElement("is", SpreadsheetMlNamespace);
                writer.WriteStartElement("t", SpreadsheetMlNamespace);
                writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                writer.WriteString(_value);
                writer.WriteEndElement(); // t
                writer.WriteEndElement(); // is
            }
            else
            {
                writer.WriteElementString("v", SpreadsheetMlNamespace, _value);
            }

            writer.WriteEndElement(); // c
        }
    }
}
