using System.Text.Json;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Migration;

/// <summary>
/// Imports a Survey123 / ArcGIS <em>feature-layer schema</em> (the JSON returned by
/// a FeatureServer layer's metadata endpoint, or an item's <c>layers[].fields</c>
/// block) into the SDK <see cref="FormDefinition"/> contract (epic #37 migration
/// guide). This is the lower-bound switching-cost reducer: an org already running
/// Survey123 against an ArcGIS feature layer can lift its field schema — names,
/// types, aliases, coded-value domains, nullability — into a Collect form without
/// re-authoring it.
/// </summary>
/// <remarks>
/// Survey123's authoring artifact is XLSForm (already handled by
/// <see cref="Field.Forms.Authoring.XlsFormImporter"/>); this importer targets the
/// <em>published feature layer</em> instead, which is what you have when the
/// original survey isn't on hand. Esri field types
/// (<c>esriFieldTypeString</c>, <c>…Integer</c>, <c>…Date</c>, …) map to
/// <see cref="FormFieldType"/>; a field carrying a coded-value
/// <c>domain</c> becomes a single-choice field whose <see cref="FieldChoice"/>s are
/// the coded values; the geometry type (<c>esriGeometryPoint</c>/…) is surfaced as
/// an optional location/shape field. The host parses the bytes; this maps the
/// decoded JSON so the logic stays platform-neutral and unit-testable.
/// </remarks>
public static class Survey123LayerImporter
{
    /// <summary>
    /// Maps a feature-layer schema JSON document into a form definition.
    /// </summary>
    /// <param name="layerSchemaJson">
    /// The layer schema JSON. Accepts either a single layer object
    /// (<c>{ "name": …, "fields": [...] }</c>) or a service document with a
    /// <c>layers</c> array, in which case the first layer is used.
    /// </param>
    /// <param name="formId">
    /// Form id to assign; defaults to the layer name (slugified) when omitted.
    /// </param>
    /// <returns>The mapped form definition and a report of any skipped fields.</returns>
    /// <exception cref="ArgumentException"><paramref name="layerSchemaJson"/> is blank.</exception>
    /// <exception cref="MigrationImportException">The JSON is not a recognizable layer schema.</exception>
    public static MigratedForm Import(string layerSchemaJson, string? formId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerSchemaJson);

        JsonElement layer;
        try
        {
            using var doc = JsonDocument.Parse(layerSchemaJson);
            layer = ResolveLayer(doc.RootElement).Clone();
        }
        catch (JsonException ex)
        {
            throw new MigrationImportException($"Survey123 layer schema is not valid JSON: {ex.Message}", ex);
        }

        if (layer.ValueKind != JsonValueKind.Object || !layer.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array)
        {
            throw new MigrationImportException("Survey123 layer schema has no 'fields' array.");
        }

        var layerName = layer.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()!
            : "Survey123 Import";

        var oidField = layer.TryGetProperty("objectIdField", out var oid) && oid.ValueKind == JsonValueKind.String
            ? oid.GetString()
            : "objectid";
        var globalIdField = layer.TryGetProperty("globalIdField", out var gid) && gid.ValueKind == JsonValueKind.String
            ? gid.GetString()
            : "globalid";

        var formFields = new List<FormField>();
        var skipped = new List<string>();

        foreach (var fieldElement in fields.EnumerateArray())
        {
            var name = fieldElement.TryGetProperty("name", out var fn) && fn.ValueKind == JsonValueKind.String
                ? fn.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // Esri system/identity columns aren't capture fields — drop them.
            if (IsSystemField(name, oidField, globalIdField))
            {
                skipped.Add($"{name} (system field)");
                continue;
            }

            var esriType = fieldElement.TryGetProperty("type", out var ft) && ft.ValueKind == JsonValueKind.String
                ? ft.GetString()
                : null;

            if (!TryMapEsriType(esriType, out var mapped))
            {
                skipped.Add($"{name} ({esriType ?? "unknown type"})");
                continue;
            }

            var label = fieldElement.TryGetProperty("alias", out var alias) && alias.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(alias.GetString())
                ? alias.GetString()!
                : name;

            // A coded-value domain turns a plain field into a single-choice list.
            var (type, choices) = ApplyDomain(fieldElement, mapped);

            // nullable:false (Esri default true) means the field is required.
            var required = fieldElement.TryGetProperty("nullable", out var nl)
                && nl.ValueKind == JsonValueKind.False;

            formFields.Add(new FormField
            {
                FieldId = name,
                Label = label,
                Type = type,
                SourceFieldName = name,
                Required = required,
                Choices = choices,
            });
        }

        // Geometry: surface the layer's geometry type as a capture field so the
        // imported form keeps a place to record the feature's location/shape.
        if (layer.TryGetProperty("geometryType", out var gt) && gt.ValueKind == JsonValueKind.String
            && TryMapGeometryType(gt.GetString(), out var geomType))
        {
            formFields.Insert(0, new FormField
            {
                FieldId = "location",
                Label = "Location",
                Type = geomType,
            });
        }

        var resolvedFormId = string.IsNullOrWhiteSpace(formId) ? Slug.From(layerName) : formId;
        var form = new FormDefinition
        {
            FormId = resolvedFormId,
            Name = layerName,
            Description = "Imported from a Survey123 / ArcGIS feature layer.",
            Sections =
            [
                new FormSection
                {
                    SectionId = "imported",
                    Label = layerName,
                    Fields = formFields,
                },
            ],
        };

        return new MigratedForm(form, [], skipped);
    }

    private static JsonElement ResolveLayer(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("layers", out var layers)
            && layers.ValueKind == JsonValueKind.Array
            && layers.GetArrayLength() > 0)
        {
            return layers[0];
        }

        return root;
    }

    private static (FormFieldType Type, IReadOnlyList<FieldChoice> Choices) ApplyDomain(
        JsonElement field, FormFieldType fallback)
    {
        if (!field.TryGetProperty("domain", out var domain) || domain.ValueKind != JsonValueKind.Object)
        {
            return (fallback, []);
        }

        var domainType = domain.TryGetProperty("type", out var dt) && dt.ValueKind == JsonValueKind.String
            ? dt.GetString()
            : null;

        // Coded-value domains enumerate the legal values → single choice.
        if (string.Equals(domainType, "codedValue", StringComparison.OrdinalIgnoreCase)
            && domain.TryGetProperty("codedValues", out var coded) && coded.ValueKind == JsonValueKind.Array)
        {
            var choices = new List<FieldChoice>();
            foreach (var cv in coded.EnumerateArray())
            {
                var code = cv.TryGetProperty("code", out var c) ? StringifyCode(c) : null;
                if (code is null)
                {
                    continue;
                }

                var name = cv.TryGetProperty("name", out var cn) && cn.ValueKind == JsonValueKind.String
                    ? cn.GetString()!
                    : code;
                choices.Add(new FieldChoice { Value = code, Label = name });
            }

            if (choices.Count > 0)
            {
                return (FormFieldType.SingleChoice, choices);
            }
        }

        // Range domains are still numeric (the range is a validation concern we
        // don't carry here); keep the fallback type.
        return (fallback, []);
    }

    private static string? StringifyCode(JsonElement code) => code.ValueKind switch
    {
        JsonValueKind.String => code.GetString(),
        JsonValueKind.Number => code.GetRawText(),
        _ => null,
    };

    private static bool IsSystemField(string name, string? oidField, string? globalIdField)
    {
        if (string.Equals(name, oidField, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, globalIdField, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Esri editor-tracking / shape bookkeeping columns.
        return name.ToLowerInvariant() switch
        {
            "objectid" or "fid" or "globalid" or "shape" or "shape_length" or "shape_area"
                or "shape__length" or "shape__area"
                or "created_user" or "created_date" or "last_edited_user" or "last_edited_date"
                or "creationdate" or "creator" or "editdate" or "editor" => true,
            _ => false,
        };
    }

    private static bool TryMapEsriType(string? esriType, out FormFieldType type)
    {
        type = esriType switch
        {
            "esriFieldTypeString" or "esriFieldTypeXML" => FormFieldType.Text,
            "esriFieldTypeSmallInteger" or "esriFieldTypeInteger"
                or "esriFieldTypeBigInteger"
                or "esriFieldTypeSingle" or "esriFieldTypeDouble" => FormFieldType.Numeric,
            // esriFieldTypeDate (a timestamp) and esriFieldTypeTimestampOffset (a
            // timestamp with a zone) both carry a time component → DateTime. A
            // date-only field has no time component, so map it to the dedicated
            // Date type rather than widening it to a DateTime the user would have
            // to fill a spurious time on.
            "esriFieldTypeDate" or "esriFieldTypeTimestampOffset" => FormFieldType.DateTime,
            "esriFieldTypeDateOnly" => FormFieldType.Date,
            "esriFieldTypeTimeOnly" => FormFieldType.Time,
            "esriFieldTypeGUID" or "esriFieldTypeGlobalID" => FormFieldType.Text,
            "esriFieldTypeBlob" or "esriFieldTypeRaster" => FormFieldType.File,
            _ => (FormFieldType)(-1),
        };

        return (int)type >= 0;
    }

    private static bool TryMapGeometryType(string? geometryType, out FormFieldType type)
    {
        type = geometryType switch
        {
            "esriGeometryPoint" or "esriGeometryMultipoint" => FormFieldType.Location,
            "esriGeometryPolyline" => FormFieldType.GeoTrace,
            "esriGeometryPolygon" or "esriGeometryEnvelope" => FormFieldType.GeoShape,
            _ => (FormFieldType)(-1),
        };

        return (int)type >= 0;
    }
}
