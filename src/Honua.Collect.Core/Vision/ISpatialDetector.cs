namespace Honua.Collect.Core.Vision;

/// <summary>Options controlling a detection pass.</summary>
public sealed record SpatialDetectionOptions
{
    /// <summary>
    /// Drop detections below this confidence (0..1) before returning. The
    /// downstream mapping applies its own field-level gate too; this is a
    /// model-side cutoff. Defaults to 0 (return everything the model emits).
    /// </summary>
    public double MinConfidence { get; init; }

    /// <summary>
    /// Optional allow-list of class labels to keep; when set, other labels are
    /// dropped. Empty/null means keep all labels.
    /// </summary>
    public IReadOnlyCollection<string>? Labels { get; init; }

    /// <summary>Maximum number of detections to return; 0 means unbounded.</summary>
    public int MaxDetections { get; init; }
}

/// <summary>
/// The spatial computer-vision seam (epic #43): detect, classify, count and
/// locate assets/defects in a captured field image. The actual model is
/// device- and vertical-bound and ships in a platform package that implements
/// this interface; the seam, the result types, and the
/// detection→record/measurement logic live here in Core so they are
/// platform-neutral and unit-testable without a model or a device.
/// </summary>
public interface ISpatialDetector
{
    /// <summary>A stable identifier for the model/provider behind this detector.</summary>
    string ModelId { get; }

    /// <summary>
    /// Runs detection over an in-memory image. Implementations must honour
    /// <paramref name="options"/> (confidence floor, label filter, cap) so callers
    /// get a consistent contract regardless of model. The returned boxes are in
    /// normalized image coordinates and the result carries the source pixel size.
    /// </summary>
    /// <param name="imageBytes">Encoded image bytes (e.g. JPEG/PNG).</param>
    /// <param name="options">Detection options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detections found and the source image dimensions.</returns>
    Task<DetectionResult> DetectAsync(
        ReadOnlyMemory<byte> imageBytes,
        SpatialDetectionOptions? options = null,
        CancellationToken cancellationToken = default);
}
