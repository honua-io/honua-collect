using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Export;

/// <summary>
/// Bulk-exports captured <see cref="FieldRecord"/> values to portable formats
/// (BACKLOG R2). Columns/properties are driven by the <see cref="FormDefinition"/>
/// so every record exports a stable, complete column set even when individual
/// records leave fields blank.
/// </summary>
/// <remarks>
/// This is a Pro capability (<c>CollectFeature.ReportsAndExports</c>); the
/// entitlement check is enforced at the app layer, not here, so the exporter
/// stays a pure, testable function.
/// </remarks>
public static class RecordExporter
{
    private static readonly string[] FixedColumns = ["record_id", "status", "latitude", "longitude"];

    /// <summary>Exports records in the requested format.</summary>
    /// <param name="form">Form whose fields define the columns.</param>
    /// <param name="records">Records to export.</param>
    /// <param name="format">Target format.</param>
    /// <returns>The serialized export as a string.</returns>
    public static string Export(FormDefinition form, IEnumerable<FieldRecord> records, ExportFormat format) => format switch
    {
        ExportFormat.Csv => ToCsv(form, records),
        ExportFormat.GeoJson => ToGeoJson(form, records),
        ExportFormat.Kml => ToKml(form, records),
        ExportFormat.Xlsx => throw new ArgumentException(
            $"{nameof(ExportFormat.Xlsx)} is a binary format; call {nameof(ExportBinary)} or {nameof(ExcelExporter)}.{nameof(ExcelExporter.Export)}.",
            nameof(format)),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format."),
    };

    /// <summary>
    /// Whether a format produces binary bytes (<see cref="ExportBinary"/>) rather than
    /// the text returned by <see cref="Export(FormDefinition, IEnumerable{FieldRecord}, ExportFormat)"/>.
    /// </summary>
    /// <param name="format">The format to classify.</param>
    /// <returns><see langword="true"/> for binary formats such as <see cref="ExportFormat.Xlsx"/>.</returns>
    public static bool IsBinary(ExportFormat format) => format is ExportFormat.Xlsx;

    /// <summary>Exports records to a binary format's file bytes.</summary>
    /// <param name="form">Form whose fields define the columns.</param>
    /// <param name="records">Records to export.</param>
    /// <param name="format">A binary target format (currently <see cref="ExportFormat.Xlsx"/>).</param>
    /// <returns>The serialized export as a byte array.</returns>
    public static byte[] ExportBinary(FormDefinition form, IEnumerable<FieldRecord> records, ExportFormat format) => format switch
    {
        ExportFormat.Xlsx => ExcelExporter.Export(form, records),
        _ => throw new ArgumentException($"{format} is not a binary export format; call {nameof(Export)}.", nameof(format)),
    };

    /// <summary>Exports records to CSV with one row per record.</summary>
    /// <param name="form">Form whose fields define the columns.</param>
    /// <param name="records">Records to export.</param>
    /// <returns>CSV text including a header row.</returns>
    public static string ToCsv(FormDefinition form, IEnumerable<FieldRecord> records)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(records);

        var fields = form.Sections.SelectMany(s => s.Fields).ToList();
        var builder = new StringBuilder();

        var header = FixedColumns.Concat(fields.Select(f => f.FieldId));
        builder.AppendLine(string.Join(',', header.Select(EscapeCsv)));

        foreach (var record in records)
        {
            var cells = new List<string>
            {
                record.RecordId,
                record.Status.ToString(),
                record.Location?.Latitude.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                record.Location?.Longitude.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            };

            foreach (var field in fields)
            {
                cells.Add(CellText(record, field));
            }

            builder.AppendLine(string.Join(',', cells.Select(EscapeCsv)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Exports records to CSV under a field mapping (epic #39): only the mapped
    /// columns are emitted, in the mapping's order, under the mapped headers. Sources
    /// may be form field ids or the fixed record columns
    /// (<c>record_id</c>/<c>status</c>/<c>latitude</c>/<c>longitude</c>).
    /// </summary>
    /// <param name="form">Form whose fields are addressable as mapping sources.</param>
    /// <param name="records">Records to export.</param>
    /// <param name="mapping">The column mapping; must list at least one column.</param>
    /// <returns>CSV text including the mapped header row.</returns>
    public static string ToMappedCsv(FormDefinition form, IEnumerable<FieldRecord> records, ExportFieldMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Columns.Count == 0)
        {
            throw new ArgumentException("A field mapping must define at least one column.", nameof(mapping));
        }

        var fieldsById = form.Sections.SelectMany(s => s.Fields)
            .GroupBy(f => f.FieldId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', mapping.Columns.Select(c => EscapeCsv(c.ResolvedHeader))));

        foreach (var record in records)
        {
            var cells = mapping.Columns.Select(c => MappedCell(record, c.Source, fieldsById));
            builder.AppendLine(string.Join(',', cells.Select(EscapeCsv)));
        }

        return builder.ToString();
    }

    private static string MappedCell(FieldRecord record, string source, IReadOnlyDictionary<string, FormField> fieldsById)
    {
        switch (source.ToLowerInvariant())
        {
            case "record_id":
                return record.RecordId;
            case "status":
                return record.Status.ToString();
            case "latitude":
                return record.Location?.Latitude.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            case "longitude":
                return record.Location?.Longitude.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return fieldsById.TryGetValue(source, out var field) ? CellText(record, field) : string.Empty;
    }

    /// <summary>Exports records to an RFC 7946 GeoJSON <c>FeatureCollection</c>.</summary>
    /// <param name="form">Form whose fields define the feature properties.</param>
    /// <param name="records">Records to export.</param>
    /// <returns>GeoJSON text.</returns>
    public static string ToGeoJson(FormDefinition form, IEnumerable<FieldRecord> records)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(records);

        var fields = form.Sections.SelectMany(s => s.Fields).ToList();
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteStartArray("features");

            foreach (var record in records)
            {
                WriteFeature(writer, record, fields);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Exports records to an OGC KML 2.2 document of placemarks.</summary>
    /// <param name="form">Form whose fields define the placemark extended data.</param>
    /// <param name="records">Records to export.</param>
    /// <returns>KML text. Records with a location carry a <c>Point</c>; all carry their values as <c>ExtendedData</c>.</returns>
    public static string ToKml(FormDefinition form, IEnumerable<FieldRecord> records)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(records);

        var fields = form.Sections.SelectMany(s => s.Fields).ToList();
        var builder = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 };
        using (var writer = XmlWriter.Create(builder, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("kml", "http://www.opengis.net/kml/2.2");
            writer.WriteStartElement("Document");

            foreach (var record in records)
            {
                WritePlacemark(writer, record, fields);
            }

            writer.WriteEndElement(); // Document
            writer.WriteEndElement(); // kml
            writer.WriteEndDocument();
        }

        return builder.ToString();
    }

    private static void WritePlacemark(XmlWriter writer, FieldRecord record, IReadOnlyList<FormField> fields)
    {
        writer.WriteStartElement("Placemark");
        writer.WriteElementString("name", record.RecordId);

        writer.WriteStartElement("ExtendedData");
        WriteKmlData(writer, "status", record.Status.ToString());
        foreach (var field in fields)
        {
            WriteKmlData(writer, field.FieldId, CellText(record, field));
        }

        writer.WriteEndElement(); // ExtendedData

        // KML coordinates are lon,lat[,alt] — same axis order as GeoJSON.
        if (record.Location is { } location)
        {
            writer.WriteStartElement("Point");
            writer.WriteElementString(
                "coordinates",
                string.Create(CultureInfo.InvariantCulture, $"{location.Longitude},{location.Latitude}"));
            writer.WriteEndElement(); // Point
        }

        writer.WriteEndElement(); // Placemark
    }

    private static void WriteKmlData(XmlWriter writer, string name, string value)
    {
        writer.WriteStartElement("Data");
        writer.WriteAttributeString("name", name);
        writer.WriteElementString("value", value);
        writer.WriteEndElement(); // Data
    }

    private static void WriteFeature(Utf8JsonWriter writer, FieldRecord record, IReadOnlyList<FormField> fields)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "Feature");

        // Geometry: a Point when the record has a location, otherwise null.
        if (record.Location is { } location)
        {
            writer.WriteStartObject("geometry");
            writer.WriteString("type", "Point");
            writer.WriteStartArray("coordinates");
            writer.WriteNumberValue(location.Longitude);
            writer.WriteNumberValue(location.Latitude);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("geometry");
        }

        writer.WriteStartObject("properties");
        writer.WriteString("record_id", record.RecordId);
        writer.WriteString("status", record.Status.ToString());

        foreach (var field in fields)
        {
            writer.WritePropertyName(field.FieldId);
            WriteJsonValue(writer, record, field);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, FieldRecord record, FormField field)
    {
        if (IsMediaField(field.Type))
        {
            writer.WriteStartArray();
            foreach (var media in MediaFor(record, field))
            {
                writer.WriteStringValue(media.FileName);
            }

            writer.WriteEndArray();
            return;
        }

        if (!record.Values.TryGetValue(field.FieldId, out var value) || value is null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (value)
        {
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case float or double or decimal:
                writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case IEnumerable enumerable:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    writer.WriteStringValue(ToText(item));
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(ToText(value));
                break;
        }
    }

    /// <summary>Renders a field's value to its canonical export string (shared across exporters).</summary>
    internal static string CellText(FieldRecord record, FormField field)
    {
        if (IsMediaField(field.Type))
        {
            return string.Join(';', MediaFor(record, field).Select(m => m.FileName));
        }

        if (!record.Values.TryGetValue(field.FieldId, out var value) || value is null)
        {
            return string.Empty;
        }

        return value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            string[] arr => string.Join(';', arr),
            IEnumerable enumerable and not string => string.Join(';', enumerable.Cast<object?>().Select(ToText)),
            _ => ToText(value),
        };
    }

    private static IEnumerable<FieldMediaAttachment> MediaFor(FieldRecord record, FormField field)
        => record.Media.Where(m =>
            string.IsNullOrWhiteSpace(m.FieldId) ||
            string.Equals(m.FieldId, field.FieldId, StringComparison.OrdinalIgnoreCase));

    private static bool IsMediaField(FormFieldType type)
        => type is FormFieldType.Photo or FormFieldType.Video or FormFieldType.Audio
            or FormFieldType.Signature or FormFieldType.Sketch or FormFieldType.File;

    private static string ToText(object? value) => value switch
    {
        null => string.Empty,
        JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny(['"', ',', '\n', '\r']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
