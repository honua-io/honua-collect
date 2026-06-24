namespace Honua.Collect.Core.Vision;

/// <summary>
/// A deterministic <see cref="ISpatialDetector"/> for tests and offline/no-model
/// builds. It does no real inference: it replays a scripted set of detections
/// and applies the same <see cref="SpatialDetectionOptions"/> filtering
/// (confidence floor, label allow-list, cap) that a real model must honour, so
/// the detection→record/measurement pipeline can be exercised end-to-end without
/// a model, a device, or the network. Output depends only on the inputs, never
/// on the image bytes' content, so results are reproducible.
/// </summary>
public sealed class StubSpatialDetector : ISpatialDetector
{
    private readonly IReadOnlyList<Detection> _scripted;
    private readonly int _imageWidth;
    private readonly int _imageHeight;

    /// <summary>Creates a stub that replays the given detections for a fixed image size.</summary>
    /// <param name="scripted">Detections to replay (their boxes are validated).</param>
    /// <param name="imageWidth">Image width in pixels to report. Defaults to 1000.</param>
    /// <param name="imageHeight">Image height in pixels to report. Defaults to 1000.</param>
    /// <param name="modelId">Model id to report. Defaults to "stub".</param>
    public StubSpatialDetector(
        IEnumerable<Detection> scripted,
        int imageWidth = 1000,
        int imageHeight = 1000,
        string modelId = "stub")
    {
        ArgumentNullException.ThrowIfNull(scripted);
        if (imageWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        }

        if (imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageHeight));
        }

        _scripted = scripted.Select(d => d with { BoundingBox = d.BoundingBox.Validated() }).ToList();
        _imageWidth = imageWidth;
        _imageHeight = imageHeight;
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
    }

    /// <inheritdoc/>
    public string ModelId { get; }

    /// <inheritdoc/>
    public Task<DetectionResult> DetectAsync(
        ReadOnlyMemory<byte> imageBytes,
        SpatialDetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var opts = options ?? new SpatialDetectionOptions();
        IEnumerable<Detection> filtered = _scripted;

        if (opts.MinConfidence > 0)
        {
            filtered = filtered.Where(d => d.Confidence >= opts.MinConfidence);
        }

        if (opts.Labels is { Count: > 0 })
        {
            var allow = new HashSet<string>(opts.Labels, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(d => allow.Contains(d.Label));
        }

        if (opts.MaxDetections > 0)
        {
            filtered = filtered.Take(opts.MaxDetections);
        }

        var result = new DetectionResult(filtered.ToList(), _imageWidth, _imageHeight);
        return Task.FromResult(result);
    }
}
