// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Honua.Collect.Core.Field.Annotation;

public sealed class PhotoAnnotationOverlay
{
    public const string EvidenceKey = "photoAnnotations";

    private readonly List<PhotoAnnotation> _annotations;

    public PhotoAnnotationOverlay() => _annotations = [];

    public PhotoAnnotationOverlay(IEnumerable<PhotoAnnotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        _annotations = annotations.ToList();
    }

    public IReadOnlyList<PhotoAnnotation> Annotations => _annotations;

    public int Count => _annotations.Count;

    public void Add(PhotoAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        _annotations.Add(annotation);
    }

    public bool Undo()
    {
        if (_annotations.Count == 0)
        {
            return false;
        }

        _annotations.RemoveAt(_annotations.Count - 1);
        return true;
    }

    public void Clear() => _annotations.Clear();

    public CapturedMediaAttachment ApplyTo(CapturedMediaAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return attachment.WithEvidenceMetadata(
            new Dictionary<string, object?> { [EvidenceKey] = _annotations.ToList() });
    }

    public static PhotoAnnotationOverlay From(CapturedMediaAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (attachment.EvidenceMetadata.TryGetValue(EvidenceKey, out var value) &&
            value is IEnumerable<PhotoAnnotation> annotations)
        {
            return new PhotoAnnotationOverlay(annotations);
        }

        return new PhotoAnnotationOverlay();
    }

    public string ToJson() => JsonSerializer.Serialize(_annotations);

    public static PhotoAnnotationOverlay FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var annotations = JsonSerializer.Deserialize<List<PhotoAnnotation>>(json) ?? [];
        return new PhotoAnnotationOverlay(annotations);
    }
}
