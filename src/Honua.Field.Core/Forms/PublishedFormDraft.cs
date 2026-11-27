// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Field.Core.Field;
using Honua.Field.Core.Field.Annotation;
using Honua.Field.Core.Field.Capture;
using Honua.Field.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Forms;

/// <summary>Local editor state and the immutable package version required to resume it.</summary>
public sealed record PublishedFormDraft(string FormId, string RecordId, string ServerBaseUrl, string PackageJson, string EditorJson)
{
    /// <summary>Whether submission must use a fresh key after the prior attempt was rejected.</summary>
    public bool ReplacesRejectedSubmission { get; init; }

    /// <summary>Snapshots an inspection editor, including attachments and repeat rows.</summary>
    public static PublishedFormDraft Capture(PublishedFormPackage package, FormSession session, Uri server)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(server);
        var record = session.Record;
        if (!package.OfflineEnabled || record.Status is not (RecordStatus.Draft or RecordStatus.Submitted)
            || session.Form.FormId != package.Definition.FormId || session.Form.Version != package.Definition.Version)
        {
            throw new InvalidOperationException("This form session cannot be saved as an offline draft.");
        }

        var state = new DraftEditorState(record.CreatedAtUtc, record.AssignedUserId, record.Location,
            CaptureFields(session.Fields),
            session.RepeatGroups.ToDictionary(group => group.SectionId,
                group => group.Instances.Select(row => CaptureFields(row.Fields)).ToArray(), StringComparer.Ordinal));
        return new(record.FormId, record.RecordId, server.AbsoluteUri, package.EnvelopeJson,
            JsonSerializer.Serialize(state, DraftEditorJson.Default.DraftEditorState));
    }

    /// <summary>Reopens an independent session for the originally captured form version.</summary>
    public (PublishedFormPackage Package, FormSession Session) Open(string formId, string recordId, Uri server)
        => Open(formId, recordId, server, null);

    /// <summary>Reopens the draft and locates captured evidence under the current installation.</summary>
    public (PublishedFormPackage Package, FormSession Session) Open(string formId, string recordId, Uri server, string? mediaDirectory)
    {
        if (!string.Equals(FormId, formId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(RecordId, recordId, StringComparison.Ordinal)
            || new Uri(ServerBaseUrl) != server)
        {
            throw new InvalidOperationException("Reconnect to the server and form where this draft was captured.");
        }

        var package = PublishedFormPackage.Parse(FormId, PackageJson);
        if (!package.OfflineEnabled)
        {
            throw new FormPackageException("This form version does not permit offline drafts.");
        }

        var state = JsonSerializer.Deserialize(EditorJson, DraftEditorJson.Default.DraftEditorState)
            ?? throw new JsonException("Stored draft editor state is missing.");
        var record = new FieldRecord
        {
            FormId = FormId,
            RecordId = RecordId,
            CreatedAtUtc = state.CreatedAtUtc,
            AssignedUserId = state.AssignedUserId,
            Location = state.Location,
            Status = RecordStatus.Draft,
        };
        var session = package.OpenRecord(record);
        RestoreFields(session, state.Fields, mediaDirectory);
        foreach (var group in state.Repeats)
        {
            var repeat = session.GetRepeat(group.Key);
            foreach (var row in group.Value)
            {
                RestoreFields(repeat.AddRestoredInstance(), row, mediaDirectory);
            }
        }

        session.Validate();
        return (package, session);
    }

    private static DraftFieldState[] CaptureFields(IReadOnlyList<FieldState> fields) =>
        fields.Where(field => field.Field.Type != FormFieldType.Calculated)
            .Select(field => new DraftFieldState(field.FieldId, DraftValue.From(field.Value), field.Media.ToArray())).ToArray();

    private static void RestoreFields(ICaptureHost host, DraftFieldState[] fields, string? mediaDirectory)
    {
        foreach (var field in fields)
        {
            host.SetValue(field.FieldId, field.Value.Restore());
            foreach (var attachment in field.Media)
            {
                var path = CapturedMediaPath.Resolve(attachment.LocalPath, mediaDirectory);
                host.AddMedia(RestoreEvidenceMetadata(attachment with { LocalPath = path }));
            }
        }
    }

    private static CapturedMediaAttachment RestoreEvidenceMetadata(CapturedMediaAttachment attachment)
    {
        var json = DraftEditorJson.Default;
        var metadata = attachment.EvidenceMetadata;
        if (metadata.Count == 0)
        {
            return attachment;
        }

        var restored = new Dictionary<string, object?>(metadata, StringComparer.Ordinal);
        if (restored.TryGetValue(MediaCaptureMetadata.EvidenceKey, out var capture) &&
            capture is JsonElement { ValueKind: JsonValueKind.Object } captureJson)
        {
            restored[MediaCaptureMetadata.EvidenceKey] = captureJson.Deserialize(json.MediaCaptureMetadata);
        }

        if (restored.TryGetValue(PhotoAnnotationOverlay.EvidenceKey, out var annotations) &&
            annotations is JsonElement { ValueKind: JsonValueKind.Array } annotationsJson)
        {
            restored[PhotoAnnotationOverlay.EvidenceKey] = annotationsJson.Deserialize(json.ListPhotoAnnotation);
        }

        return attachment.WithEvidenceMetadata(restored);
    }
}

internal sealed record DraftEditorState(DateTimeOffset CreatedAtUtc, string? AssignedUserId, FieldGeoPoint? Location,
    DraftFieldState[] Fields, Dictionary<string, DraftFieldState[][]> Repeats);
internal sealed record DraftFieldState(string FieldId, DraftValue Value, CapturedMediaAttachment[] Media);

internal sealed record DraftValue(string Kind, JsonElement Data)
{
    internal static DraftValue From(object? value)
    {
        var json = DraftEditorJson.Default;
        return value switch
        {
            int number => new("int", JsonSerializer.SerializeToElement(number, json.Int32)),
            long number => new("long", JsonSerializer.SerializeToElement(number, json.Int64)),
            decimal number => new("decimal", JsonSerializer.SerializeToElement(number, json.Decimal)),
            double number => new("double", JsonSerializer.SerializeToElement(number, json.Double)),
            DateTime date => new("datetime", JsonSerializer.SerializeToElement(date, json.DateTime)),
            DateTimeOffset date => new("datetimeoffset", JsonSerializer.SerializeToElement(date, json.DateTimeOffset)),
            DateOnly date => new("date", JsonSerializer.SerializeToElement(date, json.DateOnly)),
            TimeOnly time => new("time", JsonSerializer.SerializeToElement(time, json.TimeOnly)),
            TimeSpan time => new("timespan", JsonSerializer.SerializeToElement(time, json.TimeSpan)),
            FieldGeoPoint point => new("point", JsonSerializer.SerializeToElement(point, json.FieldGeoPoint)),
            IEnumerable<string> codes => new("choices", JsonSerializer.SerializeToElement(codes.ToArray(), json.StringArray)),
            _ => new("value", JsonSerializer.SerializeToElement(value, json.Object)),
        };
    }

    internal object? Restore()
    {
        var json = DraftEditorJson.Default;
        return Kind switch
        {
            "int" => Data.GetInt32(),
            "long" => Data.GetInt64(),
            "decimal" => Data.GetDecimal(),
            "double" => Data.GetDouble(),
            "datetime" => Data.GetDateTime(),
            "datetimeoffset" => Data.GetDateTimeOffset(),
            "date" => Data.Deserialize(json.DateOnly),
            "time" => Data.Deserialize(json.TimeOnly),
            "timespan" => Data.Deserialize(json.TimeSpan),
            "point" => Data.Deserialize(json.FieldGeoPoint),
            "choices" => Data.Deserialize(json.StringArray),
            _ => Data.ValueKind switch
            {
                JsonValueKind.String => Data.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => Data,
            },
        };
    }
}

[JsonSerializable(typeof(DraftEditorState))]
[JsonSerializable(typeof(MediaCaptureMetadata))]
[JsonSerializable(typeof(List<PhotoAnnotation>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeOnly))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
internal sealed partial class DraftEditorJson : JsonSerializerContext;
