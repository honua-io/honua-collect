namespace Honua.Collect.Core.Imaging;

/// <summary>
/// The outcome of running an <see cref="IImageResizer"/> over a captured photo: the
/// (possibly unchanged) output bytes plus the dimensions and whether any work was
/// actually performed. Lets the caller report savings and decide what to store.
/// </summary>
/// <param name="Bytes">The output image bytes (downscaled/re-encoded, or the source verbatim).</param>
/// <param name="Width">Output width in pixels.</param>
/// <param name="Height">Output height in pixels.</param>
/// <param name="Format">The codec the output is encoded in.</param>
/// <param name="Resized">Whether the image was downscaled (vs. left at source dimensions).</param>
/// <param name="ReEncoded">
/// Whether the bytes were re-encoded. <see langword="false"/> means the source was
/// returned verbatim (the within-budget no-op).
/// </param>
public readonly record struct ImageResizeResult(
    byte[] Bytes,
    int Width,
    int Height,
    ImageEncodeFormat Format,
    bool Resized,
    bool ReEncoded)
{
    /// <summary>The size of the output, in bytes.</summary>
    public int ByteLength => Bytes.Length;
}
