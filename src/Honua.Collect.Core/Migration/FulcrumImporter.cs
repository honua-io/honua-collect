using System.Globalization;
using System.Text.Json;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Migration;

/// <summary>
/// Imports a Fulcrum export into Collect (epic #37 migration guide). Fulcrum is
/// cloud-only with no true on-prem story, so the switching motion is "lift my app
/// and its records out of Fulcrum into a self-hosted Collect". Two inputs are
/// handled, mirroring Fulcrum's own export surface:
/// <list type="bullet">
///   <item>the <em>app schema</em> JSON (the app's <c>elements</c> definition) →
///     a <see cref="FormDefinition"/>; and</item>
///   <item>a record <em>export</em> — GeoJSON (<c>FeatureCollection</c>) or CSV —
///     → <see cref="FieldRecord"/>s keyed to that form.</item>
/// </list>
/// The host reads the bytes; this maps the decoded content so the logic is
/// platform-neutral and unit-testable.
/// </summary>
public static class FulcrumImporter
{
    /// <summary>
    /// Maps a Fulcrum app-schema JSON document into a form definition. Accepts a
    /// bare app object (<c>{ "name": …, "elements": [...] }</c>) or a wrapper with a
    /// top-level <c>form</c>/<c>app</c> property.
    /// </summary>
    /// <param name="appSchemaJson">The Fulcrum app schema JSON.</param>
    /// <param name="formId">Form id; defaults to the app name (slugified).</param>
    /// <returns>The mapped form plus a report of any skipped elements.</returns>
    /// <exception cref="ArgumentException"><paramref name="appSchemaJson"/> is blank.</exception>
    /// <exception cref="MigrationImportException">The JSON is not a recognizable Fulcrum app.</exception>
    public static MigratedForm ImportForm(string appSchemaJson, string? formId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appSchemaJson);

        JsonElement app;
        try
        {
            using var doc = JsonDocument.Parse(appSchemaJson);
            app = ResolveApp(doc.RootElement).Clone();
        }
        catch (JsonException ex)
        {
            throw new MigrationImportException($"Fulcrum app schema is not valid JSON: {ex.Message}", ex);
        }

        if (app.ValueKind != JsonValueKind.Object || !app.TryGetProperty("elements", out var elements)
            || elements.ValueKind != JsonValueKind.Array)
        {
            throw new MigrationImportException("Fulcrum app schema has no 'elements' array.");
        }

        var appName = app.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()!
            : "Fulcrum Import";

        var skipped = new List<string>();
        var fields = new List<FormField>
        {
            // Every Fulcrum record carries a position; surface it as a capture field.
            new() { FieldId = "location", Label = "Location", Type = FormFieldType.Location },
        };

        MapElements(elements, fields, skipped);

        var resolvedFormId = string.IsNullOrWhiteSpace(formId) ? Slug.From(appName) : formId;
        var form = new FormDefinition
        {
            FormId = resolvedFormId,
            Name = appName,
            Description = "Imported from a Fulcrum app export.",
            Sections =
            [
                new FormSection { SectionId = "imported", Label = appName, Fields = fields },
            ],
        };

        return new MigratedForm(form, [], skipped);
    }

    /// <summary>
    /// Maps a Fulcrum GeoJSON record export into records keyed to the given form.
    /// Each feature's <c>geometry</c> (a point) becomes the record location and its
    /// <c>properties</c> become record values (filtered to the form's fields).
    /// </summary>
    /// <param name="form">The form the records belong to (drives which columns are kept).</param>
    /// <param name="geoJson">A GeoJSON <c>FeatureCollection</c> (or single <c>Feature</c>).</param>
    /// <returns>The recovered records and any skipped features.</returns>
    public static MigratedForm ImportGeoJsonRecords(FormDefinition form, string geoJson)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(geoJson);

        var fieldIds = FieldIds(form);
        var records = new List<FieldRecord>();
        var skipped = new List<string>();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(geoJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new MigrationImportException($"Fulcrum GeoJSON export is not valid JSON: {ex.Message}", ex);
        }

        var features = root.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray()
            : Single(root); // tolerate a bare Feature

        var index = 0;
        foreach (var feature in features)
        {
            index++;
            if (feature.ValueKind != JsonValueKind.Object
                || !feature.TryGetProperty("properties", out var props)
                || props.ValueKind != JsonValueKind.Object)
            {
                skipped.Add($"feature #{index} (no properties)");
                continue;
            }

            var recordId = ReadString(props, "fulcrum_id")
                ?? ReadString(props, "_record_id")
                ?? Guid.NewGuid().ToString("n");

            var record = new FieldRecord
            {
                RecordId = recordId,
                FormId = form.FormId,
                Status = RecordStatus.Submitted,
                Location = ReadPoint(feature),
            };

            foreach (var prop in props.EnumerateObject())
            {
                if (IsFulcrumSystemColumn(prop.Name) || !fieldIds.Contains(prop.Name))
                {
                    continue;
                }

                record.Values[prop.Name] = ConvertJsonValue(prop.Value);
            }

            records.Add(record);
        }

        return new MigratedForm(form, records, skipped);
    }

    /// <summary>
    /// Maps a Fulcrum CSV record export into records keyed to the given form. The
    /// first row is the header; <c>latitude</c>/<c>longitude</c> columns (Fulcrum's
    /// position export) become the record location; remaining columns matching form
    /// fields become values.
    /// </summary>
    /// <param name="form">The form the records belong to.</param>
    /// <param name="csv">The Fulcrum CSV export text.</param>
    /// <returns>The recovered records and any skipped rows.</returns>
    public static MigratedForm ImportCsvRecords(FormDefinition form, string csv)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(csv);

        var rows = Csv.Parse(csv);
        if (rows.Count < 2)
        {
            return new MigratedForm(form, [], rows.Count == 0 ? ["empty CSV"] : []);
        }

        var header = rows[0];
        var fieldIds = FieldIds(form);
        var records = new List<FieldRecord>();
        var skipped = new List<string>();

        var latIdx = IndexOf(header, "latitude", "lat", "_latitude");
        var lonIdx = IndexOf(header, "longitude", "lon", "lng", "_longitude");
        var idIdx = IndexOf(header, "fulcrum_id", "_record_id");

        for (var r = 1; r < rows.Count; r++)
        {
            var cells = rows[r];
            if (cells.Count == 0 || cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var recordId = idIdx >= 0 && idIdx < cells.Count && !string.IsNullOrWhiteSpace(cells[idIdx])
                ? cells[idIdx]
                : Guid.NewGuid().ToString("n");

            var record = new FieldRecord
            {
                RecordId = recordId,
                FormId = form.FormId,
                Status = RecordStatus.Submitted,
                Location = ReadCsvPoint(cells, latIdx, lonIdx),
            };

            for (var c = 0; c < header.Count && c < cells.Count; c++)
            {
                var column = header[c];
                if (IsFulcrumSystemColumn(column) || !fieldIds.Contains(column))
                {
                    continue;
                }

                var raw = cells[c];
                if (!string.IsNullOrEmpty(raw))
                {
                    record.Values[column] = raw;
                }
            }

            records.Add(record);
        }

        return new MigratedForm(form, records, skipped);
    }

    private static void MapElements(JsonElement elements, List<FormField> fields, List<string> skipped)
    {
        foreach (var element in elements.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var dataName = ReadString(element, "data_name") ?? ReadString(element, "key");
            var elementType = ReadString(element, "type");

            // Section/Repeatable elements nest their own children; flatten them so
            // the contained capture fields survive the import (the repeat structure
            // itself is dropped — noted in skipped).
            if (string.Equals(elementType, "Section", StringComparison.OrdinalIgnoreCase)
                || string.Equals(elementType, "Repeatable", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(dataName))
                {
                    skipped.Add($"{dataName} ({elementType} flattened)");
                }

                if (element.TryGetProperty("elements", out var children) && children.ValueKind == JsonValueKind.Array)
                {
                    MapElements(children, fields, skipped);
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(dataName))
            {
                continue;
            }

            if (!TryMapFulcrumType(elementType, out var fieldType))
            {
                skipped.Add($"{dataName} ({elementType ?? "unknown type"})");
                continue;
            }

            var label = ReadString(element, "label") ?? dataName;
            var required = element.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True;

            fields.Add(new FormField
            {
                FieldId = dataName,
                Label = label,
                Type = fieldType,
                SourceFieldName = dataName,
                Required = required,
                Choices = ReadChoices(element),
            });
        }
    }

    private static IReadOnlyList<FieldChoice> ReadChoices(JsonElement element)
    {
        if (!element.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<FieldChoice>();
        foreach (var choice in choices.EnumerateArray())
        {
            var value = ReadString(choice, "value");
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var label = ReadString(choice, "label") ?? value;
            result.Add(new FieldChoice { Value = value, Label = label });
        }

        return result;
    }

    private static bool TryMapFulcrumType(string? type, out FormFieldType fieldType)
    {
        fieldType = type switch
        {
            "TextField" => FormFieldType.Text,
            "TextArea" or "TextAreaField" => FormFieldType.Text,
            "CalculatedField" => FormFieldType.Calculated,
            "ChoiceField" => FormFieldType.SingleChoice,
            "ClassificationField" => FormFieldType.Classification,
            "YesNoField" => FormFieldType.YesNo,
            "DateTimeField" or "DateField" => FormFieldType.DateTime,
            "TimeField" => FormFieldType.Time,
            "PhotoField" => FormFieldType.Photo,
            "VideoField" => FormFieldType.Video,
            "AudioField" => FormFieldType.Audio,
            "SignatureField" => FormFieldType.Signature,
            "BarcodeField" => FormFieldType.Barcode,
            "AddressField" => FormFieldType.Address,
            "HyperlinkField" => FormFieldType.Hyperlink,
            "RecordLinkField" => FormFieldType.RecordLink,
            // Fulcrum has no dedicated numeric type; numbers are TextField with a
            // numeric format. Map an explicit IntegerField/DecimalField if present.
            "IntegerField" or "DecimalField" or "NumericField" => FormFieldType.Numeric,
            _ => (FormFieldType)(-1),
        };

        return (int)fieldType >= 0;
    }

    private static JsonElement ResolveApp(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("form", out var form) && form.ValueKind == JsonValueKind.Object)
            {
                return form;
            }

            if (root.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.Object)
            {
                return app;
            }
        }

        return root;
    }

    private static HashSet<string> FieldIds(FormDefinition form)
        => form.Sections.SelectMany(s => s.Fields).Select(f => f.FieldId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsFulcrumSystemColumn(string name) => name.ToLowerInvariant() switch
    {
        "fulcrum_id" or "_record_id" or "_project" or "_assigned_to" or "_status"
            or "_latitude" or "_longitude" or "latitude" or "longitude"
            or "_created_at" or "_updated_at" or "_created_by" or "_updated_by"
            or "_version" or "_changeset_id" or "_geometry" or "geometry" => true,
        _ => false,
    };

    private static FieldGeoPoint? ReadPoint(JsonElement feature)
    {
        if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object
            || !geometry.TryGetProperty("coordinates", out var coords) || coords.ValueKind != JsonValueKind.Array
            || coords.GetArrayLength() < 2)
        {
            return null;
        }

        // GeoJSON order is [longitude, latitude].
        if (coords[0].TryGetDouble(out var lon) && coords[1].TryGetDouble(out var lat))
        {
            return new FieldGeoPoint(lat, lon);
        }

        return null;
    }

    private static FieldGeoPoint? ReadCsvPoint(IReadOnlyList<string> cells, int latIdx, int lonIdx)
    {
        if (latIdx < 0 || lonIdx < 0 || latIdx >= cells.Count || lonIdx >= cells.Count)
        {
            return null;
        }

        if (double.TryParse(cells[latIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(cells[lonIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
        {
            return new FieldGeoPoint(lat, lon);
        }

        return null;
    }

    private static int IndexOf(IReadOnlyList<string> header, params string[] candidates)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (candidates.Any(c => string.Equals(header[i], c, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }

    private static string? ReadString(JsonElement obj, string property)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var v)
            && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static object? ConvertJsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        // Box each numeric branch separately so an integer doesn't widen to double
        // through the ternary's common type (keeps 62 a long, not 62.0).
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : (object)value.GetDouble(),
        JsonValueKind.Array => value.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
            .Where(s => s is not null)
            .ToArray(),
        _ => value.GetRawText(),
    };

    private static IEnumerable<JsonElement> Single(JsonElement element)
    {
        yield return element;
    }
}
