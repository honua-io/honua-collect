// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Export;

public static class RecordExporter
{
    private static readonly string[] FixedColumns = ["record_id", "status", "latitude", "longitude"];

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

    public static bool IsBinary(ExportFormat format) => format is ExportFormat.Xlsx;

    public static byte[] ExportBinary(FormDefinition form, IEnumerable<FieldRecord> records, ExportFormat format) => format switch
    {
        ExportFormat.Xlsx => ExcelExporter.Export(form, records),
        _ => throw new ArgumentException($"{format} is not a binary export format; call {nameof(Export)}.", nameof(format)),
    };

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
        if (FormFieldTypes.IsMedia(field.Type))
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

    internal static string CellText(FieldRecord record, FormField field)
    {
        if (FormFieldTypes.IsMedia(field.Type))
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

    private static string ToText(object? value) => value switch
    {
        null => string.Empty,
        JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string EscapeCsv(string value)
    {
        var safe = NeutralizeFormula(value);
        if (safe.IndexOfAny(['"', ',', '\n', '\r']) < 0)
        {
            return safe;
        }

        return $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    internal static string NeutralizeFormula(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var first = value[0];
        if (first is not ('=' or '+' or '-' or '@' or '\t' or '\r' or '\n'))
        {
            return value;
        }

        // A leading + or - on a well-formed number (e.g. a negative longitude
        // "-122.6") is legitimate data, not a formula, so leave it intact —
        // otherwise every negative coordinate/measurement would be corrupted.
        // = @ and leading control chars are always neutralized.
        if (first is '+' or '-'
            && double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        {
            return value;
        }

        return "'" + value;
    }
}
