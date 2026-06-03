using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;

namespace Honua.Collect.App.Capture;

/// <summary>
/// Downscales and re-encodes captured photos before they are stored and uploaded
/// (BACKLOG C8). Field photos off a modern camera are often 4–12 MP / several MB;
/// shrinking the longest edge and re-encoding as JPEG keeps offline storage and
/// sync payloads small without a third-party imaging dependency — it uses the
/// MAUI Graphics platform image services already in the app.
/// </summary>
public static class ImageCompressor
{
    /// <summary>Default longest-edge cap, in pixels.</summary>
    public const int DefaultMaxEdge = 1600;

    /// <summary>Default JPEG quality (0–1).</summary>
    public const float DefaultQuality = 0.75f;

    /// <summary>
    /// Produces a downscaled JPEG copy of <paramref name="sourcePath"/> and returns
    /// its path. If the source is already within <paramref name="maxEdge"/> it is
    /// re-encoded at the target quality (still typically smaller than the original).
    /// </summary>
    /// <param name="sourcePath">Path to the source image.</param>
    /// <param name="maxEdge">Longest-edge cap in pixels.</param>
    /// <param name="quality">JPEG quality (0–1).</param>
    /// <returns>The path to the compressed JPEG.</returns>
    public static async Task<string> CompressAsync(
        string sourcePath, int maxEdge = DefaultMaxEdge, float quality = DefaultQuality)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        using var input = File.OpenRead(sourcePath);
        var image = PlatformImage.FromStream(input);

        var longest = Math.Max(image.Width, image.Height);
        var output = longest > maxEdge ? image.Downsize(maxEdge) : image;

        var destination = CaptureFiles.NewPath(".jpg");
        using var stream = File.Create(destination);
        await output.SaveAsync(stream, ImageFormat.Jpeg, quality);
        return destination;
    }
}
