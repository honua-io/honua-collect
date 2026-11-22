// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Forms;

public sealed class FormPackageClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public FormPackageClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<FormDefinition> DownloadAsync(
        string formId,
        int? packageVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        if (packageVersion is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(packageVersion), "Package version must be positive.");
        }

        var escapedId = Uri.EscapeDataString(formId);
        var path = packageVersion is { } version
            ? $"/api/v1/forms/packages/{escapedId}/versions/{version.ToString(CultureInfo.InvariantCulture)}"
            : $"/api/v1/forms/packages/{escapedId}";

        using var response = await _httpClient
            .GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        return Map(json);
    }

    internal static FormDefinition Map(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FormPackageException("The form package response was empty.");
        }

        ThrowIfErrorEnvelope(json);

        FormPackageVersionEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<FormPackageVersionEnvelope>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new FormPackageException("The form package response was not valid JSON.", ex);
        }

        var document = envelope?.Package;
        var formId = FirstNonEmpty(document?.FormId, envelope?.FormId);
        if (string.IsNullOrWhiteSpace(formId))
        {
            throw new FormPackageException("The form package response did not contain a form definition.");
        }

        var title = FirstNonEmpty(document?.Title, envelope?.Title) ?? formId;
        var version = ExtractVersion(envelope?.Version) ?? ExtractVersion(document?.Version);
        var rawSections = document?.Sections ?? envelope?.Sections ?? [];
        var rawFields = document?.Fields ?? envelope?.Fields ?? [];

        var validFields = rawFields
            .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.FieldId))
            .ToList();

        var fieldsBySection = validFields
            .GroupBy(f => f.SectionId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var fieldsById = new Dictionary<string, FormFieldDto>(StringComparer.Ordinal);
        foreach (var field in validFields)
        {
            fieldsById[field.FieldId!] = field;
        }

        var sections = MapSections(rawSections, fieldsById, fieldsBySection);
        return new FormDefinition
        {
            FormId = formId,
            Name = title,
            Version = version,
            Sections = sections,
        };
    }

    private static void ThrowIfErrorEnvelope(string json)
    {
        try
        {
            using var probe = JsonDocument.Parse(json);
            if (probe.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (probe.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                int? code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var cv) ? cv : null;
                throw new FormPackageException(
                    $"The form server returned an error{(code is { } k ? $" ({k.ToString(CultureInfo.InvariantCulture)})" : string.Empty)}: " +
                    $"{message ?? "unspecified error"}.",
                    code);
            }
        }
        catch (JsonException ex)
        {
            throw new FormPackageException("The form package response was not valid JSON.", ex);
        }
    }

    private static List<FormSection> MapSections(
        List<FormSectionDto> rawSections,
        Dictionary<string, FormFieldDto> fieldsById,
        Dictionary<string, List<FormFieldDto>> fieldsBySection)
    {
        var sections = new List<FormSection>();
        foreach (var section in rawSections)
        {
            if (section is null || string.IsNullOrWhiteSpace(section.SectionId))
            {
                continue;
            }

            List<FormField> fields;
            if (section.FieldIds is { Count: > 0 } fieldIds)
            {
                fields = [];
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var fieldId in fieldIds)
                {
                    if (string.IsNullOrWhiteSpace(fieldId)
                        || !seen.Add(fieldId)
                        || !fieldsById.TryGetValue(fieldId, out var dto))
                    {
                        continue;
                    }

                    fields.Add(MapField(dto));
                }
            }
            else
            {
                fields = fieldsBySection.TryGetValue(section.SectionId, out var sectionFields)
                    ? sectionFields.Select(MapField).ToList()
                    : [];
            }

            sections.Add(new FormSection
            {
                SectionId = section.SectionId,
                Label = string.IsNullOrWhiteSpace(section.Label) ? section.SectionId : section.Label,
                Repeatable = section.Repeatable,
                Fields = fields,
            });
        }

        return sections;
    }

    private static FormField MapField(FormFieldDto dto)
    {
        var choices = (dto.Domain?.Choices ?? [])
            .Select(c => (Code: ExtractCode(c?.Code), Label: c?.Label))
            .Where(c => !string.IsNullOrWhiteSpace(c.Code))
            .Select(c => new FieldChoice { Value = c.Code!, Label = c.Label })
            .ToList();

        return new FormField
        {
            FieldId = dto.FieldId!,
            Label = string.IsNullOrWhiteSpace(dto.Label) ? dto.FieldId! : dto.Label,
            Type = MapType(dto.Type),
            Required = dto.Required,
            Choices = choices,
        };
    }

    private static string? ExtractCode(JsonElement? code)
    {
        if (code is not { } element)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string? ExtractVersion(JsonElement? version)
    {
        if (version is not { } element)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };
    }

    private static string? FirstNonEmpty(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first)
            ? first
            : (!string.IsNullOrWhiteSpace(second) ? second : null);

    private static FormFieldType MapType(string? type)
        => (type ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture) switch
        {
            "text" => FormFieldType.Text,
            "integer" or "decimal" or "number" => FormFieldType.Numeric,
            "date" => FormFieldType.Date,
            "time" => FormFieldType.Time,
            "datetime" => FormFieldType.DateTime,
            "yesno" or "boolean" => FormFieldType.YesNo,
            "select_one" => FormFieldType.SingleChoice,
            "select_multiple" => FormFieldType.MultipleChoice,
            "image" or "photo" => FormFieldType.Photo,
            "video" => FormFieldType.Video,
            "audio" => FormFieldType.Audio,
            "signature" => FormFieldType.Signature,
            "barcode" => FormFieldType.Barcode,
            "geopoint" or "location" => FormFieldType.Location,
            "calculate" => FormFieldType.Calculated,
            "file" => FormFieldType.File,
            _ => FormFieldType.Text,
        };

    private sealed record FormPackageVersionEnvelope
    {
        [JsonPropertyName("formId")]
        public string? FormId { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("version")]
        public JsonElement? Version { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("package")]
        public FormPackageDocumentDto? Package { get; init; }

        [JsonPropertyName("sections")]
        public List<FormSectionDto>? Sections { get; init; }

        [JsonPropertyName("fields")]
        public List<FormFieldDto>? Fields { get; init; }
    }

    private sealed record FormPackageDocumentDto
    {
        [JsonPropertyName("formId")]
        public string? FormId { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("version")]
        public JsonElement? Version { get; init; }

        [JsonPropertyName("sections")]
        public List<FormSectionDto>? Sections { get; init; }

        [JsonPropertyName("fields")]
        public List<FormFieldDto>? Fields { get; init; }
    }

    private sealed record FormSectionDto
    {
        [JsonPropertyName("sectionId")]
        public string? SectionId { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }

        [JsonPropertyName("repeatable")]
        public bool Repeatable { get; init; }

        [JsonPropertyName("minInstances")]
        public int? MinInstances { get; init; }

        [JsonPropertyName("maxInstances")]
        public int? MaxInstances { get; init; }

        [JsonPropertyName("fieldIds")]
        public List<string>? FieldIds { get; init; }
    }

    private sealed record FormFieldDto
    {
        [JsonPropertyName("fieldId")]
        public string? FieldId { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("required")]
        public bool Required { get; init; }

        [JsonPropertyName("sectionId")]
        public string? SectionId { get; init; }

        [JsonPropertyName("domain")]
        public FieldDomainDto? Domain { get; init; }
    }

    private sealed record FieldDomainDto
    {
        [JsonPropertyName("choices")]
        public List<FieldChoiceDto>? Choices { get; init; }
    }

    private sealed record FieldChoiceDto
    {
        [JsonPropertyName("code")]
        public JsonElement? Code { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }
    }
}

public sealed class FormPackageException : Exception
{
    public FormPackageException(string message)
        : base(message)
    {
    }

    public FormPackageException(string message, int? errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public int? ErrorCode { get; }

    public FormPackageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
