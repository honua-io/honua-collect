using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Ai;

/// <summary>
/// Configuration for <see cref="AnthropicPhotoToFieldsProvider"/>.
/// </summary>
public sealed record AnthropicPhotoToFieldsOptions
{
    /// <summary>The Anthropic API key (sent as the <c>x-api-key</c> header). Required.</summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Vision-capable Claude model id. Defaults to <c>claude-opus-4-8</c>.
    /// </summary>
    public string Model { get; init; } = "claude-opus-4-8";

    /// <summary>Maximum response tokens. Defaults to 1024 — the structured output is small.</summary>
    public int MaxTokens { get; init; } = 1024;
}

/// <summary>
/// A real <see cref="IPhotoToFieldsProvider"/> backed by the Anthropic Messages API
/// (vision). It base64-encodes a captured photo, sends it alongside a prompt that
/// lists the target form fields, and asks Claude — via a strict tool-use contract —
/// for a JSON object of <c>{fieldId: {value, confidence}}</c>, which it maps to a
/// <see cref="FieldExtractionResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is BACKLOG A2, the Pro capture differentiator. Entitlement gating is enforced
/// by <see cref="AiCaptureService"/> when the result is applied; this provider only
/// performs extraction.
/// </para>
/// <para>
/// The provider is robust: a non-success HTTP status, a rate-limit, a malformed body,
/// or a network failure never throws out of <see cref="ExtractAsync"/>. Instead it
/// returns an empty result whose <see cref="FieldExtractionResult.Unmapped"/> carries
/// a short diagnostic, so the capture flow degrades to a manual entry rather than
/// crashing.
/// </para>
/// </remarks>
public sealed class AnthropicPhotoToFieldsProvider : IPhotoToFieldsProvider
{
    internal const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
    internal const string AnthropicVersion = "2023-06-01";
    internal const string ToolName = "provide_fields";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AnthropicPhotoToFieldsOptions _options;

    /// <summary>Creates the provider.</summary>
    /// <param name="httpClient">
    /// HTTP client used for the API call. Injected so the caller owns its lifetime and
    /// so tests can supply a stub handler. The provider does not mutate the client's
    /// default headers — auth and version headers are set per request.
    /// </param>
    /// <param name="options">API key, model id, and limits.</param>
    public AnthropicPhotoToFieldsProvider(HttpClient httpClient, AnthropicPhotoToFieldsOptions options)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new ArgumentException("An Anthropic API key is required.", nameof(options));
        }
    }

    /// <inheritdoc />
    public async Task<FieldExtractionResult> ExtractAsync(
        string photoPath,
        FormDefinition form,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(photoPath);
        ArgumentNullException.ThrowIfNull(form);

        byte[] imageBytes;
        try
        {
            imageBytes = await File.ReadAllBytesAsync(photoPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed($"Could not read photo '{photoPath}': {ex.Message}");
        }

        var targets = TargetFields(form);
        if (targets.Count == 0)
        {
            return Failed("The form has no extractable fields.");
        }

        var mediaType = MediaTypeForPath(photoPath);
        var request = BuildRequest(Convert.ToBase64String(imageBytes), mediaType, form, targets);

        AnthropicMessageResponse? response;
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint)
            {
                Content = JsonContent.Create(request, options: SerializerOptions),
            };
            httpRequest.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
            httpRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            // Ensure content-type is exactly application/json (no charset) as the API expects.
            if (httpRequest.Content is not null)
            {
                httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            using var httpResponse = await _http
                .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(httpResponse, cancellationToken).ConfigureAwait(false);
                return Failed($"Anthropic API returned {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}: {Trim(body)}");
            }

            response = await httpResponse.Content
                .ReadFromJsonAsync<AnthropicMessageResponse>(SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed($"Anthropic request failed: {ex.Message}");
        }

        return ParseResponse(response, targets);
    }

    private static IReadOnlyList<FormField> TargetFields(FormDefinition form)
    {
        // Extract only data-bearing fields the model can read from an image; media,
        // signature, and computed fields are not values the model should invent.
        static bool IsExtractable(FormFieldType type)
            => !FormFieldTypes.IsMedia(type) && type is not (FormFieldType.Calculated or FormFieldType.RecordLink);

        return (form.Sections ?? [])
            .SelectMany(s => s?.Fields ?? [])
            .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.FieldId) && IsExtractable(f.Type))
            .GroupBy(f => f.FieldId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private AnthropicMessageRequest BuildRequest(
        string base64Image,
        string mediaType,
        FormDefinition form,
        IReadOnlyList<FormField> targets)
    {
        var prompt = new StringBuilder();
        prompt.Append("You extract structured field values from a photo to pre-fill a data-collection form named \"")
            .Append(form.Name ?? form.FormId)
            .AppendLine("\".");
        prompt.AppendLine("Read the image and fill in as many of the target fields below as you can see directly in the photo.");
        prompt.AppendLine("Only return a field when the photo gives clear evidence; omit fields you cannot determine.");
        prompt.AppendLine("Give each returned field a confidence from 0.0 (guess) to 1.0 (certain).");
        prompt.AppendLine();
        prompt.AppendLine("Target fields (id | label | type):");
        foreach (var field in targets)
        {
            prompt.Append("- ").Append(field.FieldId)
                .Append(" | ").Append(field.Label ?? field.FieldId)
                .Append(" | ").Append(field.Type.ToString());
            var choices = (field.Choices ?? [])
                .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Value))
                .Select(c => c.Value)
                .ToList();
            if (choices.Count > 0)
            {
                prompt.Append(" | choices: ").Append(string.Join(", ", choices));
            }

            prompt.AppendLine();
        }

        prompt.AppendLine();
        prompt.Append("Call the ").Append(ToolName)
            .AppendLine(" tool with the fields you extracted. Use the exact field ids above as keys.");

        var toolSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                fields = new
                {
                    type = "array",
                    description = "Extracted field values.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            field_id = new { type = "string", description = "Target field id." },
                            value = new { type = "string", description = "Extracted value as text." },
                            confidence = new { type = "number", description = "Confidence 0..1." },
                        },
                        required = new[] { "field_id", "value", "confidence" },
                    },
                },
            },
            required = new[] { "fields" },
        });

        return new AnthropicMessageRequest
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            Tools =
            [
                new AnthropicTool
                {
                    Name = ToolName,
                    Description = "Report the field values extracted from the photo.",
                    InputSchema = toolSchema,
                },
            ],
            ToolChoice = new AnthropicToolChoice { Type = "tool", Name = ToolName },
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content =
                    [
                        new AnthropicContentBlock
                        {
                            Type = "image",
                            Source = new AnthropicImageSource
                            {
                                Type = "base64",
                                MediaType = mediaType,
                                Data = base64Image,
                            },
                        },
                        new AnthropicContentBlock { Type = "text", Text = prompt.ToString() },
                    ],
                },
            ],
        };
    }

    private static FieldExtractionResult ParseResponse(
        AnthropicMessageResponse? response,
        IReadOnlyList<FormField> targets)
    {
        if (response?.Content is null || response.Content.Count == 0)
        {
            return Failed("Anthropic response contained no content.");
        }

        var toolUse = response.Content.FirstOrDefault(c =>
            string.Equals(c.Type, "tool_use", StringComparison.Ordinal) &&
            string.Equals(c.Name, ToolName, StringComparison.Ordinal));

        if (toolUse?.Input is not { } input || input.ValueKind != JsonValueKind.Object)
        {
            return Failed("Anthropic response did not include the expected tool output.");
        }

        if (!input.TryGetProperty("fields", out var fieldsElement) || fieldsElement.ValueKind != JsonValueKind.Array)
        {
            return new FieldExtractionResult { Fields = [] };
        }

        var byId = targets.ToDictionary(f => f.FieldId, StringComparer.OrdinalIgnoreCase);
        var extracted = new List<ExtractedField>();
        foreach (var item in fieldsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("field_id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;

            var fieldId = idEl.GetString();
            if (string.IsNullOrWhiteSpace(fieldId) || !byId.TryGetValue(fieldId, out var target)) continue;

            var value = item.TryGetProperty("value", out var valEl) ? ConvertValue(valEl, target.Type) : null;
            var confidence = item.TryGetProperty("confidence", out var confEl) ? ReadConfidence(confEl) : 0.0;

            // Use the canonical field id casing from the form.
            extracted.Add(new ExtractedField(target.FieldId, value, confidence));
        }

        return new FieldExtractionResult { Fields = extracted };
    }

    private static object? ConvertValue(JsonElement element, FormFieldType type)
    {
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        switch (type)
        {
            case FormFieldType.Numeric:
                if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                {
                    return number == Math.Floor(number) && !double.IsInfinity(number) ? (object)(long)number : number;
                }

                return text;
            case FormFieldType.YesNo:
                if (bool.TryParse(text, out var flag)) return flag;
                return text.Trim().ToLowerInvariant() switch
                {
                    "yes" or "y" or "1" => true,
                    "no" or "n" or "0" => false,
                    _ => text,
                };
            default:
                return text;
        }
    }

    private static double ReadConfidence(JsonElement element)
    {
        double raw = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0.0,
        };

        return Math.Clamp(raw, 0.0, 1.0);
    }

    private static string MediaTypeForPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".jpg" or ".jpeg" or _ => "image/jpeg",
        };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Trim(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= 300 ? value : value[..300];
    }

    private static FieldExtractionResult Failed(string diagnostic)
        => new() { Fields = [], Unmapped = diagnostic };

    // ---- Wire types (Anthropic Messages API) ----

    private sealed record AnthropicMessageRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("max_tokens")]
        public required int MaxTokens { get; init; }

        [JsonPropertyName("messages")]
        public required IReadOnlyList<AnthropicMessage> Messages { get; init; }

        [JsonPropertyName("tools")]
        public IReadOnlyList<AnthropicTool>? Tools { get; init; }

        [JsonPropertyName("tool_choice")]
        public AnthropicToolChoice? ToolChoice { get; init; }
    }

    private sealed record AnthropicMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required IReadOnlyList<AnthropicContentBlock> Content { get; init; }
    }

    private sealed record AnthropicTool
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("input_schema")]
        public required JsonElement InputSchema { get; init; }
    }

    private sealed record AnthropicToolChoice
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed record AnthropicImageSource
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("media_type")]
        public string? MediaType { get; init; }

        [JsonPropertyName("data")]
        public string? Data { get; init; }
    }

    private sealed record AnthropicContentBlock
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("source")]
        public AnthropicImageSource? Source { get; init; }

        // Present on tool_use blocks in responses.
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("input")]
        public JsonElement? Input { get; init; }
    }

    private sealed record AnthropicMessageResponse
    {
        [JsonPropertyName("content")]
        public IReadOnlyList<AnthropicContentBlock>? Content { get; init; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; init; }
    }
}
