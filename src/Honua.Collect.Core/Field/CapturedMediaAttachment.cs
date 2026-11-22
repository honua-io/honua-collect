// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field;

public sealed record CapturedMediaAttachment
{
    public required string AttachmentId { get; init; }

    public string? FieldId { get; init; }

    public required string LocalPath { get; init; }

    public FieldMediaType MediaType { get; init; }

    public string? ContentType { get; init; }

    public long? SizeBytes { get; init; }

    public FieldGeoPoint? CaptureLocation { get; init; }

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool RequiresFaceBlur { get; init; }

    public IReadOnlyDictionary<string, object?> EvidenceMetadata { get; init; } =
        new Dictionary<string, object?>();

    public CapturedMediaAttachment WithEvidenceMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var merged = new Dictionary<string, object?>(EvidenceMetadata, StringComparer.Ordinal);
        foreach (var item in metadata)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);
            merged[item.Key] = item.Value;
        }

        return this with { EvidenceMetadata = merged };
    }

    public FieldMediaAttachment ToSdkAttachment()
    {
        return new FieldMediaAttachment
        {
            AttachmentId = AttachmentId,
            FieldId = FieldId,
            MediaType = MediaType,
            FileName = Path.GetFileName(LocalPath),
            ContentType = ContentType,
            SizeBytes = SizeBytes,
            CaptureLocation = CaptureLocation,
            CapturedAtUtc = CapturedAtUtc,
            RequiresFaceBlur = RequiresFaceBlur,
        };
    }
}
