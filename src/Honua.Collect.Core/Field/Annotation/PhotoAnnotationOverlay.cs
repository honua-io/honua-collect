using System.Text.Json;

namespace Honua.Collect.Core.Field.Annotation;

/// <summary>
/// The ordered set of markup elements drawn on one captured photo (BACKLOG C7).
/// The overlay is kept as product-owned evidence metadata on the attachment, so
/// the original image is never modified — the markup can be re-edited, and the
/// host renders it on top when displaying or burning it into an export.
/// </summary>
public sealed class PhotoAnnotationOverlay
{
    /// <summary>Evidence-metadata key the overlay is stored under on an attachment.</summary>
    public const string EvidenceKey = "photoAnnotations";

    private readonly List<PhotoAnnotation> _annotations;

    /// <summary>Creates an empty overlay.</summary>
    public PhotoAnnotationOverlay() => _annotations = [];

    /// <summary>Creates an overlay from existing annotations.</summary>
    /// <param name="annotations">Initial annotations.</param>
    public PhotoAnnotationOverlay(IEnumerable<PhotoAnnotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        _annotations = annotations.ToList();
    }

    /// <summary>The annotations, in draw order.</summary>
    public IReadOnlyList<PhotoAnnotation> Annotations => _annotations;

    /// <summary>Number of annotations.</summary>
    public int Count => _annotations.Count;

    /// <summary>Adds an annotation on top of the others.</summary>
    /// <param name="annotation">Annotation to add.</param>
    public void Add(PhotoAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        _annotations.Add(annotation);
    }

    /// <summary>Removes the most recently added annotation (undo).</summary>
    /// <returns><see langword="true"/> if one was removed.</returns>
    public bool Undo()
    {
        if (_annotations.Count == 0)
        {
            return false;
        }

        _annotations.RemoveAt(_annotations.Count - 1);
        return true;
    }

    /// <summary>Removes all annotations.</summary>
    public void Clear() => _annotations.Clear();

    /// <summary>
    /// Returns a copy of <paramref name="attachment"/> carrying this overlay in
    /// its evidence metadata. The original image path is untouched.
    /// </summary>
    /// <param name="attachment">Attachment to annotate.</param>
    /// <returns>An attachment with the overlay attached.</returns>
    public CapturedMediaAttachment ApplyTo(CapturedMediaAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return attachment.WithEvidenceMetadata(
            new Dictionary<string, object?> { [EvidenceKey] = _annotations.ToList() });
    }

    /// <summary>Reads the overlay previously attached to an attachment, if any.</summary>
    /// <param name="attachment">Attachment to read.</param>
    /// <returns>The overlay (empty when none).</returns>
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

    /// <summary>Serializes the overlay to JSON for persistence.</summary>
    /// <returns>JSON array of annotations.</returns>
    public string ToJson() => JsonSerializer.Serialize(_annotations);

    /// <summary>Deserializes an overlay from JSON.</summary>
    /// <param name="json">JSON produced by <see cref="ToJson"/>.</param>
    /// <returns>The overlay.</returns>
    public static PhotoAnnotationOverlay FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var annotations = JsonSerializer.Deserialize<List<PhotoAnnotation>>(json) ?? [];
        return new PhotoAnnotationOverlay(annotations);
    }
}
