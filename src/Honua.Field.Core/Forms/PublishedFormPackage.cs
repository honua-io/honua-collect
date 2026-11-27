// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using Honua.Field.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Forms;

/// <summary>A published server form version and the settings needed to capture a record.</summary>
public sealed class PublishedFormPackage
{
    private readonly PublishedXlsFormExpressions? _expressions;
    private readonly Dictionary<string, RepeatBounds> _repeatBounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object?> _defaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _choiceCodes = new(StringComparer.OrdinalIgnoreCase);

    private PublishedFormPackage(JsonElement package, string formId, int version, string envelopeJson)
    {
        Version = version;
        EnvelopeJson = envelopeJson;
        RequiresGeometry = package.Property("submitPolicy").Property("requiresGeometry").ValueKind != JsonValueKind.False;
        OfflineEnabled = package.Property("offlinePolicy").Property("enabled").ValueKind == JsonValueKind.True;
        var attachmentPolicy = package.Property("attachmentPolicy").Property("fields").Items()
            .ToDictionary(field => field.RequiredText("fieldId"), StringComparer.OrdinalIgnoreCase);
        var fields = package.Property("fields").Items()
            .ToDictionary(field => field.RequiredText("fieldId"), StringComparer.OrdinalIgnoreCase);
        if (package.Text("expressionLanguage") == "xlsform")
        {
            _expressions = new PublishedXlsFormExpressions(fields.Values
                .Where(field => field.Text("type")?.ToLowerInvariant() is "multichoice" or "multiplechoice")
                .Select(field => field.RequiredText("fieldId")), fields.Keys);
        }

        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sections = new List<FormSection>();
        foreach (var section in package.Property("sections").Items())
        {
            string sectionId = section.RequiredText("sectionId");
            var repeatable = section.Property("repeatable").ValueKind == JsonValueKind.True;
            if (repeatable)
            {
                _repeatBounds.Add(sectionId, new RepeatBounds(
                    section.Property("minInstances").Integer(),
                    section.Property("maxInstances").Integer()));
            }

            var fieldIds = section.Property("fieldIds").Items().Select(id => id.GetString()!).ToArray();
            if (fieldIds.Length == 0)
            {
                fieldIds = fields.Where(pair => string.Equals(pair.Value.Text("sectionId"), sectionId, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Key).ToArray();
            }

            sections.Add(new FormSection
            {
                SectionId = sectionId,
                Label = section.Text("label") ?? sectionId,
                Repeatable = repeatable,
                Fields = fieldIds.Where(assigned.Add).Select(id => MapField(fields[id], attachmentPolicy)).ToArray(),
            });
        }

        var remaining = fields.Where(pair => !assigned.Contains(pair.Key)).Select(pair => MapField(pair.Value, attachmentPolicy)).ToArray();
        if (remaining.Length > 0)
        {
            var sectionId = "fields";
            while (sections.Any(section => string.Equals(section.SectionId, sectionId, StringComparison.OrdinalIgnoreCase)))
            {
                sectionId += "_";
            }

            sections.Add(new FormSection { SectionId = sectionId, Label = "Fields", Fields = remaining });
        }

        Definition = new FormDefinition
        {
            FormId = formId,
            Name = package.Text("title") ?? formId,
            Version = Version.ToString(CultureInfo.InvariantCulture),
            Sections = sections,
        };
    }

    internal string EnvelopeJson { get; }

    internal FormSession OpenRecord(FieldRecord record) => FormSession.OpenPublished(Definition, record, _repeatBounds, _expressions, defaults: _defaults);

    /// <summary>The form definition for the capture runtime.</summary>
    public FormDefinition Definition { get; }

    /// <summary>The immutable package version selected by the server.</summary>
    public int Version { get; }

    /// <summary>Whether the package submission policy requires geometry.</summary>
    public bool RequiresGeometry { get; }

    /// <summary>Whether the server permits offline use of this version.</summary>
    public bool OfflineEnabled { get; }

    /// <summary>Starts a new record with the form's defaults and repeat bounds.</summary>
    public FormSession CreateSession(string recordId) =>
        FormSession.CreatePublished(Definition, recordId, _repeatBounds, _defaults, _expressions);

    /// <summary>Reopens a cached published version when it permits offline use.</summary>
    public static PublishedFormPackage? LoadCached(string formId, string cachePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        var package = Parse(formId, File.ReadAllText(cachePath));
        return package.OfflineEnabled ? package : null;
    }

    /// <summary>Resolves UI choice tokens to their original JSON values for submission.</summary>
    public object? GetValueForSubmission(string fieldId, object? value)
    {
        if (!_choiceCodes.TryGetValue(fieldId, out var codes))
        {
            return value;
        }

        object Resolve(string code) => codes.TryGetValue(code, out var raw) ? raw : code;
        return value switch
        {
            string code => Resolve(code),
            IEnumerable<string> selected => selected.Select(Resolve).ToArray(),
            JsonElement { ValueKind: JsonValueKind.String } json => Resolve(json.GetString()!),
            JsonElement { ValueKind: JsonValueKind.Array } json => json.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? Resolve(item.GetString()!) : item).ToArray(),
            _ => value,
        };
    }

    internal static PublishedFormPackage Parse(string formId, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var envelope = document.RootElement;
            var package = envelope.Property("package");
            if (!string.Equals(formId, envelope.Text("formId"), StringComparison.OrdinalIgnoreCase)
                || envelope.Text("status") != "published"
                || envelope.Property("version").Integer() is not > 0
                || package.Text("schemaVersion") != "honua.form-package.v1"
                || !string.Equals(formId, package.Text("formId"), StringComparison.OrdinalIgnoreCase))
            {
                throw new FormPackageException("The server response did not contain the requested published form version.");
            }

            return new PublishedFormPackage(package, envelope.RequiredText("formId"), envelope.Property("version").GetInt32(), json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            throw new FormPackageException("The published form package is malformed.", ex);
        }
    }

    private FormField MapField(JsonElement field, IReadOnlyDictionary<string, JsonElement> attachmentPolicy)
    {
        var fieldId = field.RequiredText("fieldId");
        var domain = field.Property("domain");
        var codes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var choices = new List<FieldChoice>();
        foreach (var choice in domain.Property("choices").Items())
        {
            var code = choice.Property("code");
            var token = code.ValueKind == JsonValueKind.String ? code.GetString()! : code.GetRawText();
            if (codes.ContainsKey(token))
            {
                token = code.GetRawText();
            }

            codes.Add(token, code.Clone());
            choices.Add(new FieldChoice { Value = token, Label = choice.Text("label") ?? token });
        }

        if (codes.Count > 0)
        {
            _choiceCodes.Add(fieldId, codes);
        }

        var defaultValue = field.Property("defaultValue");
        if (defaultValue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            string? ChoiceToken(JsonElement value) => codes.FirstOrDefault(pair => JsonElement.DeepEquals(pair.Value, value)).Key;
            _defaults[fieldId] = codes.Count == 0 ? ToValue(defaultValue)
                : defaultValue.ValueKind == JsonValueKind.Array ? defaultValue.Items().Select(ChoiceToken).OfType<string>().ToArray()
                : ChoiceToken(defaultValue);
        }

        var policy = attachmentPolicy.GetValueOrDefault(fieldId);
        var required = field.Property("required").ValueKind == JsonValueKind.True || policy.Property("required").ValueKind == JsonValueKind.True;
        string? Expression(string property) => _expressions is null
            ? field.Text(property)
            : _expressions.Compile(field.Text(property), fieldId);
        var calculatedExpression = Expression("calculatedExpression");
        var mediaTypes = policy.Property("allowedContentTypes").Items()
            .Select(item => item.GetString()).OfType<string>().ToArray();
        var type = calculatedExpression is not null ? FormFieldType.Calculated : field.Text("type")?.ToLowerInvariant() switch
        {
            "choice" or "singlechoice" => FormFieldType.SingleChoice,
            "multichoice" or "multiplechoice" => FormFieldType.MultipleChoice,
            "attachment" or "media" => ResolveMediaType(mediaTypes),
            "geometry" => FormFieldType.Location,
            var token => FormPackageClient.MapType(token),
        };

        return new FormField
        {
            FieldId = fieldId,
            Label = field.Text("label") ?? fieldId,
            HelpText = field.Text("hint"),
            SourceFieldName = field.Text("targetField"),
            Type = type,
            Required = required,
            Choices = choices,
            VisibilityRule = MapVisibility(field.Property("visibility")),
            RelevanceExpression = Expression("relevanceExpression"),
            CalculatedExpression = calculatedExpression,
            Validation = new FieldValidationRule
            {
                MinNumericValue = domain.Property("min").Number(),
                MaxNumericValue = domain.Property("max").Number(),
                MinMediaCount = FormFieldTypes.IsMedia(type) && required ? 1 : null,
                MaxMediaCount = policy.Property("maxCount").Integer(),
                ConstraintExpression = Expression("constraintExpression"),
                ConstraintMessage = field.Text("constraintMessage"),
            },
        };
    }

    private static FormFieldType ResolveMediaType(string[] types)
    {
        if (types.Length > 0 && types.All(type => type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
        {
            return FormFieldType.Photo;
        }

        if (types.Length > 0 && types.All(type => type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
        {
            return FormFieldType.Video;
        }

        return types.Length > 0 && types.All(type => type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            ? FormFieldType.Audio
            : FormFieldType.File;
    }

    private static FieldVisibilityRule? MapVisibility(JsonElement rule)
    {
        var op = rule.Text("operator")?.ToLowerInvariant() switch
        {
            "equals" => ComparisonOperator.Equals,
            "notequals" => ComparisonOperator.NotEquals,
            "gt" => ComparisonOperator.GreaterThan,
            "lt" => ComparisonOperator.LessThan,
            _ => (ComparisonOperator?)null,
        };

        return op is { } supported && rule.Text("dependsOnFieldId") is { } fieldId
            ? new FieldVisibilityRule
            {
                DependsOnFieldId = fieldId,
                Operator = supported,
                MatchValue = ToValue(rule.Property("value")),
            }
            : null;
    }

    private static object? ToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var number) => number,
        JsonValueKind.Number => value.GetDouble(),
        _ => value.Clone(),
    };
}

internal static class PublishedFormJson
{
    internal static JsonElement Property(this JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : default;

    internal static IEnumerable<JsonElement> Items(this JsonElement node) =>
        node.ValueKind == JsonValueKind.Array ? node.EnumerateArray() : [];

    internal static string? Text(this JsonElement node, string name) =>
        node.Property(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    internal static string RequiredText(this JsonElement node, string name) =>
        node.Text(name) is { Length: > 0 } value ? value : throw new FormPackageException($"The published form package is missing {name}.");

    internal static int? Integer(this JsonElement node) =>
        node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var value) ? value : null;

    internal static double? Number(this JsonElement node) =>
        node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out var value) ? value : null;
}
