// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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
        DateTimeOffset? submittedAt = null, string? idempotencyKey = null)
    {
        var files = new List<SubmissionFile>();
        var attachments = new List<FormAttachment>();
        var point = session.Fields.Any(field => field.Field.Type == FormFieldType.Location)
            ? null : session.Record.Location;

        Dictionary<string, object?> ReadValues(IEnumerable<FieldState> fields, bool isRepeat = false)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (isRepeat && (field.Media.Count > 0 || field.Value is FieldGeoPoint))
                {
                    throw new InvalidOperationException("This server form supports location and evidence on the inspection only.");
                }

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
            values[group.SectionId] = group.Instances.Select(row => ReadValues(row.VisibleFields, isRepeat: true)).ToArray();
        }

        if (package.RequiresGeometry && point is null)
        {
            throw new InvalidOperationException("Capture a location before submitting this inspection.");
        }

        session.Record.Location = point;
        var geometry = point is null ? null : new SubmissionGeometry(point.Longitude, point.Latitude, new(4326));
        var request = new FormRequest(package.Version, "create", idempotencyKey ?? session.Record.RecordId,
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

        using var sent = JsonDocument.Parse(RequestJson);
        var expected = sent.RootElement.GetProperty("attachments").EnumerateArray()
            .Select(a => a.GetProperty("clientAttachmentId").GetString()).ToHashSet(StringComparer.Ordinal);
        var parts = Files.Select(file => new PublishedFormPart(
            file.PartName, file.FileName, file.ContentType, () => File.OpenRead(file.LocalPath))).ToArray();

        PublishedFormSubmissionReceipt receipt;
        try
        {
            var submission = new Honua.Sdk.Field.Forms.PublishedFormSubmission
            {
                FormId = FormId,
                RequestJson = RequestJson,
                Files = parts,
                CreateUploadDeadline = bytes => UploadDeadline.Start(bytes, cancellationToken)
            };
            receipt = await new PublishedFormSubmissionClient(http).SubmitAsync(submission, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FeatureSyncResult.Fail("The form submission timed out before the server receipt was confirmed.");
        }
        catch (JsonException)
        {
            return FeatureSyncResult.Fail("The server returned an unreadable or mismatched submission receipt.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is { } statusCode)
        {
            return FeatureSyncResult.Fail($"Form submission returned HTTP {(int)statusCode}.", (int)statusCode);
        }

        var responseCode = (int)receipt.HttpStatusCode;
        var success = responseCode is >= 200 and <= 299;
        if (receipt.Status == "rejected" && receipt.TargetFeatureId is null && receipt.EditOutcome is null
            && receipt.Retry?.Retryable is false)
        {
            var message = receipt.ValidationIssues?.Select(issue => issue?.Message)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            return FeatureSyncResult.Fail(message ?? "Correct the rejected inspection before submitting it again.",
                success ? null : responseCode) with { CanCorrectSubmission = true };
        }

        if (!success)
        {
            return FeatureSyncResult.Fail($"Form submission returned HTTP {responseCode}.", responseCode);
        }

        if (receipt.Status != "accepted" || receipt.EditOutcome?.Succeeded != true
            || receipt.TargetFeatureId is not { } objectId)
        {
            return FeatureSyncResult.Fail("The server did not confirm the requested form version and feature edit.");
        }

        if (receipt.AttachmentOutcomes is null)
        {
            return FeatureSyncResult.Fail("The server omitted the attachment receipts.");
        }

        foreach (var outcome in receipt.AttachmentOutcomes)
        {
            if (outcome?.Status != "accepted" || outcome.AttachmentId is null
                || !expected.Remove(outcome.ClientAttachmentId))
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
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeOnly))]
internal sealed partial class FormWireJson : JsonSerializerContext;
