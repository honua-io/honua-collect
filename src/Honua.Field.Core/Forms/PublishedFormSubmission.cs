// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Field.Core.Field.Forms;
using Honua.Field.Core.Sync;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Forms;

/// <summary>A durable file reference for one part of a form submission.</summary>
public sealed record SubmissionFile(string PartName, string LocalPath, string ContentType, string FileName);

/// <summary>The versioned request and evidence captured before the first upload.</summary>
public sealed record PublishedFormSubmission(string FormId, string RequestJson, IReadOnlyList<SubmissionFile> Files, string? ServerBaseUrl = null)
{
    /// <summary>Freezes the request, destination, and evidence references for replay.</summary>
    public static PublishedFormSubmission Capture(PublishedFormPackage package, FormSession session, Uri serverBaseUri,
        DateTimeOffset? submittedAt = null)
    {
        var files = new List<SubmissionFile>();
        var attachments = new List<FormAttachment>();
        var point = session.Record.Location;

        Dictionary<string, object?> ReadValues(IEnumerable<FieldState> fields)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                foreach (var attachment in field.Media)
                {
                    var partName = $"file{files.Count}";
                    var file = new SubmissionFile(partName, attachment.LocalPath,
                        attachment.ContentType ?? "application/octet-stream", Path.GetFileName(attachment.LocalPath));
                    files.Add(file);
                    attachments.Add(new FormAttachment(attachment.AttachmentId, field.FieldId, partName,
                        file.FileName, file.ContentType, attachment.SizeBytes));
                }

                if (field.Value is FieldGeoPoint location && field.Field.Type == FormFieldType.Location)
                {
                    point = location;
                }
                else if (field.Value is { } value && !FormFieldTypes.IsMedia(field.Field.Type)
                    && field.Field.Type is not (FormFieldType.Calculated or FormFieldType.Location)
                    && string.IsNullOrWhiteSpace(field.Field.CalculatedExpression))
                {
                    values[field.FieldId] = field.Field.Type == FormFieldType.Numeric && value is string number
                        && decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue)
                        ? numericValue : package.GetValueForSubmission(field.FieldId, value);
                }
            }

            return values;
        }

        var values = ReadValues(session.VisibleFields);
        foreach (var group in session.RepeatGroups)
        {
            values[group.SectionId] = group.Instances.Select(row => ReadValues(row.VisibleFields)).ToArray();
        }

        if (package.RequiresGeometry && point is null)
        {
            throw new InvalidOperationException("Capture a location before submitting this inspection.");
        }

        session.Record.Location = point;
        var geometry = point is null ? null : new SubmissionGeometry(point.Longitude, point.Latitude, new(4326));
        var request = new FormRequest(package.Version, "create", session.Record.RecordId,
            submittedAt ?? DateTimeOffset.UtcNow, values, geometry, attachments);
        return new(session.Form.FormId, JsonSerializer.Serialize(request, FormWireJson.Default.FormRequest),
            files, serverBaseUri.AbsoluteUri);
    }

    /// <summary>Uploads the frozen submission and checks its feature and attachment receipts.</summary>
    public async Task<FeatureSyncResult> UploadAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        if (http.BaseAddress is null || (ServerBaseUrl is not null && new Uri(ServerBaseUrl) != http.BaseAddress))
        {
            return FeatureSyncResult.Fail("Reconnect to the server where this inspection was captured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/forms/{Uri.EscapeDataString(FormId)}/submissions");
        var json = new StringContent(RequestJson, Encoding.UTF8, "application/json");
        request.Content = json;
        if (Files.Count > 0)
        {
            var parts = new MultipartFormDataContent();
            request.Content = parts;
            parts.Add(json, "submission");
            foreach (var file in Files)
            {
                var contentType = MediaTypeHeaderValue.Parse(file.ContentType);
                var content = new StreamContent(File.OpenRead(file.LocalPath));
                parts.Add(content, file.PartName, file.FileName);
                content.Headers.ContentType = contentType;
            }
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return FeatureSyncResult.Fail($"Form submission returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        }

        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        using var sent = JsonDocument.Parse(RequestJson);
        var root = result.RootElement;
        var expected = sent.RootElement.GetProperty("attachments").EnumerateArray()
            .Select(a => a.GetProperty("clientAttachmentId").GetString()).ToHashSet(StringComparer.Ordinal);
        if (root.GetProperty("status").GetString() != "accepted" || root.GetProperty("formId").GetString() != FormId
            || root.GetProperty("formVersion").GetInt32() != sent.RootElement.GetProperty("formVersion").GetInt32()
            || !root.GetProperty("editOutcome").GetProperty("succeeded").GetBoolean()
            || !root.GetProperty("targetFeatureId").TryGetInt64(out var objectId))
        {
            return FeatureSyncResult.Fail("The server did not confirm the requested form version and feature edit.");
        }

        foreach (var outcome in root.GetProperty("attachmentOutcomes").EnumerateArray())
        {
            if (outcome.GetProperty("status").GetString() != "accepted"
                || !outcome.GetProperty("attachmentId").TryGetInt64(out _)
                || !expected.Remove(outcome.GetProperty("clientAttachmentId").GetString()))
            {
                return FeatureSyncResult.Fail("The server did not confirm the captured evidence.");
            }
        }

        return expected.Count == 0 ? FeatureSyncResult.Ok(objectId)
            : FeatureSyncResult.Fail("The server omitted an attachment receipt.");
    }
}


internal sealed record FormRequest(int FormVersion, string Operation, string IdempotencyKey,
    DateTimeOffset SubmittedAt, Dictionary<string, object?> Values, SubmissionGeometry? Geometry, List<FormAttachment> Attachments);
internal sealed record SubmissionGeometry(double X, double Y, SpatialReference SpatialReference);
internal sealed record SpatialReference(int Wkid);
internal sealed record FormAttachment(string ClientAttachmentId, string FieldId, string PartName, string Filename,
    string ContentType, long? SizeBytes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FormRequest))]
[JsonSerializable(typeof(Dictionary<string, object?>[]))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeOnly))]
internal sealed partial class FormWireJson : JsonSerializerContext;
