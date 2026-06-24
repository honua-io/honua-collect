namespace Honua.Collect.Core.Imaging;

/// <summary>
/// The target output format for a resized/re-encoded image.
/// </summary>
public enum ImageEncodeFormat
{
    /// <summary>Lossy JPEG — the default for photos; honours <see cref="ImageResizeOptions.Quality"/>.</summary>
    Jpeg = 0,

    /// <summary>Lossless PNG — quality is ignored.</summary>
    Png = 1,

    /// <summary>WebP — honours <see cref="ImageResizeOptions.Quality"/>.</summary>
    Webp = 2,
}

/// <summary>
/// The platform-neutral budget for downscaling and re-encoding a captured photo
/// before it is stored and uploaded (BACKLOG C8). Pairs the longest-edge cap
/// (whose maths lives in <see cref="ImageResizePlan"/>) with the output codec and
/// quality, so a field photo off a modern camera (4–12 MP / several MB) is shrunk
/// to a sync-friendly payload. An <see cref="IImageResizer"/> consumes these to do
/// the actual byte-level work.
/// </summary>
public sealed record ImageResizeOptions
{
    /// <summary>Default longest-edge cap, in pixels.</summary>
    public const int DefaultMaxEdge = 1600;

    /// <summary>Default JPEG/WebP quality (1–100).</summary>
    public const int DefaultQuality = 75;

    /// <summary>
    /// Longest-edge cap in pixels. The longest source edge is downscaled to this
    /// value (aspect preserved); a source already within the cap is not upscaled.
    /// Must be positive.
    /// </summary>
    public int MaxEdge { get; init; } = DefaultMaxEdge;

    /// <summary>
    /// Output encode quality in the range 1–100 (higher is better/larger). Applies
    /// to <see cref="ImageEncodeFormat.Jpeg"/> and <see cref="ImageEncodeFormat.Webp"/>;
    /// ignored for <see cref="ImageEncodeFormat.Png"/>.
    /// </summary>
    public int Quality { get; init; } = DefaultQuality;

    /// <summary>The output codec. Defaults to <see cref="ImageEncodeFormat.Jpeg"/>.</summary>
    public ImageEncodeFormat Format { get; init; } = ImageEncodeFormat.Jpeg;

    /// <summary>
    /// When <see langword="true"/> (the default) a source that is already within the
    /// dimension budget AND already in the target format is returned byte-for-byte
    /// unchanged — avoiding a needless decode/re-encode that would only add JPEG
    /// generation loss. Set <see langword="false"/> to always re-encode (e.g. to
    /// strip metadata or force the quality ceiling).
    /// </summary>
    public bool SkipWhenWithinBudget { get; init; } = true;

    /// <summary>Throws if any option is out of its valid range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">If <see cref="MaxEdge"/> or <see cref="Quality"/> is invalid.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEdge);
        ArgumentOutOfRangeException.ThrowIfLessThan(Quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Quality, 100);
    }
}
