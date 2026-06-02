using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Forms;

/// <summary>
/// Downloads a server-hosted form package and maps it to the SDK
/// <see cref="FormDefinition"/> the app renders. The server publishes a form
/// package as JSON over the FormServer REST endpoint; this client fetches it and
/// translates the package's flat field list + section descriptors into the
/// nested SDK contract.
/// </summary>
/// <remarks>
/// <para>
/// The package JSON shape is:
/// <code>
/// {
///   "formId": "...", "title": "...", "version": "1",
///   "sections": [ { "sectionId": "main", "label": "Main", "repeatable": false,
///                   "minInstances": null, "maxInstances": null,
///                   "fieldIds": ["a", "b"] } ],
///   "fields":   [ { "fieldId": "a", "label": "Name", "type": "text",
///                   "required": true, "sectionId": "main",
///                   "domain": { "choices": [ { "code": "new", "label": "New" } ] } } ]
/// }
/// </code>
/// </para>
/// <para>
/// Field <c>type</c> tokens are mapped to <see cref="FormFieldType"/> using the
/// same token-mapping approach as <c>XlsFormImporter</c>; unknown tokens fall
/// back to <see cref="FormFieldType.Text"/>. Fields are grouped under their
/// section by <c>sectionId</c>, preserving section order and the field order in
/// which fields appear in the package.
/// </para>
/// <para>
/// Usage: <c>var form = await client.DownloadAsync(serviceId, formId);</c> then
/// <c>FormSession.CreateForNewRecord(form, recordId)</c> to start a capture
/// session the UI binds to.
/// </para>
/// </remarks>
public sealed class FormPackageClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    /// <summary>Creates a client over an injected <see cref="HttpClient"/>.</summary>
    /// <param name="httpClient">
    /// Transport used for the package request. Its <see cref="HttpClient.BaseAddress"/>
    /// is expected to point at the server root, since the request path is relative.
    /// </param>
    public FormPackageClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <summary>
    /// Downloads the form package for a service and maps it to a
    /// <see cref="FormDefinition"/>.
    /// </summary>
    /// <param name="serviceId">Hosting service identifier.</param>
    /// <param name="formId">Form identifier within the service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mapped SDK form definition.</returns>
    /// <remarks>
    /// GETs <c>/rest/services/{serviceId}/FormServer/{formId}?f=json</c>.
    /// </remarks>
    public async Task<FormDefinition> DownloadAsync(
        string serviceId,
        string formId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);

        var path = $"/rest/services/{Uri.EscapeDataString(serviceId)}/FormServer/{Uri.EscapeDataString(formId)}?f=json";

        using var response = await _httpClient
            .GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        return Map(json);
    }

    /// <summary>
    /// Maps a form-package JSON document to a <see cref="FormDefinition"/>.
    /// Exposed internally so the mapping can be unit-tested without HTTP.
    /// </summary>
    /// <param name="json">The form-package JSON payload.</param>
    /// <returns>The mapped SDK form definition.</returns>
    internal static FormDefinition Map(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FormPackageException("The form package response was empty.");
        }

        FormPackageDto? package;
        try
        {
            package = JsonSerializer.Deserialize<FormPackageDto>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new FormPackageException("The form package response was not valid JSON.", ex);
        }

        if (package is null || string.IsNullOrWhiteSpace(package.FormId))
        {
            throw new FormPackageException("The form package response did not contain a form definition.");
        }

        // Group fields by section id (case-sensitive, matching the package keys),
        // preserving the order fields appear in the package.
        var fieldsBySection = (package.Fields ?? [])
            .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.FieldId))
            .GroupBy(f => f.SectionId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var sections = new List<FormSection>();
        foreach (var section in package.Sections ?? [])
        {
            if (section is null || string.IsNullOrWhiteSpace(section.SectionId))
            {
                continue;
            }

            var fields = fieldsBySection.TryGetValue(section.SectionId, out var sectionFields)
                ? sectionFields.Select(MapField).ToList()
                : [];

            sections.Add(new FormSection
            {
                SectionId = section.SectionId,
                Label = string.IsNullOrWhiteSpace(section.Label) ? section.SectionId : section.Label,
                Repeatable = section.Repeatable,
                Fields = fields,
            });
        }

        return new FormDefinition
        {
            FormId = package.FormId,
            Name = string.IsNullOrWhiteSpace(package.Title) ? package.FormId : package.Title,
            Version = string.IsNullOrWhiteSpace(package.Version) ? null : package.Version,
            Sections = sections,
        };
    }

    private static FormField MapField(FormFieldDto dto)
    {
        var choices = (dto.Domain?.Choices ?? [])
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Code))
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

    private sealed record FormPackageDto
    {
        [JsonPropertyName("formId")]
        public string? FormId { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }

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
        public string? Code { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }
    }
}

/// <summary>
/// Raised when a downloaded form package is empty, malformed, or missing a form
/// definition.
/// </summary>
public sealed class FormPackageException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Describes the failure.</param>
    public FormPackageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    /// <param name="message">Describes the failure.</param>
    /// <param name="innerException">Underlying cause.</param>
    public FormPackageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
