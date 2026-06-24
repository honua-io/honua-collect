namespace Honua.Collect.Core.Imaging;

/// <summary>
/// Executes the C8 resize/compress budget (<see cref="ImageResizeOptions"/>) on raw
/// image bytes: decode, normalise EXIF orientation, downscale the longest edge to the
/// cap (aspect preserved via <see cref="ImageResizePlan"/>), and re-encode at the
/// target format/quality — a no-op that returns the source verbatim when it is already
/// within budget. Platform-neutral so it runs and is unit-tested in Core without a
/// device; the production binding is <see cref="SkiaImageResizer"/>.
/// </summary>
public interface IImageResizer
{
    /// <summary>
    /// Applies <paramref name="options"/> to <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The captured image bytes (JPEG/PNG/WebP, possibly EXIF-rotated).</param>
    /// <param name="options">The resize/compress budget.</param>
    /// <returns>The output bytes and dimensions.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="source"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="source"/> is empty.</exception>
    /// <exception cref="ImageDecodeException">If the bytes are not a decodable image.</exception>
    ImageResizeResult Resize(byte[] source, ImageResizeOptions options);
}

/// <summary>Thrown when image bytes cannot be decoded by an <see cref="IImageResizer"/>.</summary>
public sealed class ImageDecodeException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The error message.</param>
    public ImageDecodeException(string message) : base(message)
    {
    }
}
