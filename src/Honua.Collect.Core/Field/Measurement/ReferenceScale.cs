namespace Honua.Collect.Core.Field.Measurement;

/// <summary>A point in image pixel space.</summary>
/// <param name="X">Horizontal pixel coordinate.</param>
/// <param name="Y">Vertical pixel coordinate.</param>
public readonly record struct PixelPoint(double X, double Y);

/// <summary>
/// Reference-scale measurement-from-image (BACKLOG, #43): once the operator marks a
/// feature of known real-world length in the photo (a ruler, a sign, a standard
/// pipe diameter), this converts further pixel measurements on that image into
/// real-world lengths and areas tied to the record. Assumes the measured features
/// lie roughly in the reference's plane — "photogrammetry-lite", not a full 3D
/// reconstruction.
/// </summary>
public sealed class ReferenceScale
{
    private ReferenceScale(double metersPerPixel) => MetersPerPixel = metersPerPixel;

    /// <summary>Real-world metres represented by one pixel.</summary>
    public double MetersPerPixel { get; }

    /// <summary>
    /// Builds a scale from a reference feature of known real length and its measured
    /// pixel length in the image.
    /// </summary>
    /// <param name="referenceRealMeters">The reference's true length in metres.</param>
    /// <param name="referencePixelLength">The reference's length in pixels in the image.</param>
    /// <returns>The derived scale.</returns>
    public static ReferenceScale FromReference(double referenceRealMeters, double referencePixelLength)
    {
        Require(referenceRealMeters, nameof(referenceRealMeters));
        Require(referencePixelLength, nameof(referencePixelLength));
        return new ReferenceScale(referenceRealMeters / referencePixelLength);
    }

    /// <summary>Converts a pixel length to real-world metres.</summary>
    /// <param name="pixelLength">A length measured in pixels.</param>
    /// <returns>The length in metres.</returns>
    public double LengthMeters(double pixelLength)
    {
        if (pixelLength < 0 || double.IsNaN(pixelLength))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelLength), pixelLength, "Pixel length must be non-negative.");
        }

        return pixelLength * MetersPerPixel;
    }

    /// <summary>Converts a pixel area to real-world square metres (scale applies squared).</summary>
    /// <param name="pixelArea">An area measured in square pixels.</param>
    /// <returns>The area in square metres.</returns>
    public double AreaSquareMeters(double pixelArea)
    {
        if (pixelArea < 0 || double.IsNaN(pixelArea))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelArea), pixelArea, "Pixel area must be non-negative.");
        }

        return pixelArea * MetersPerPixel * MetersPerPixel;
    }

    /// <summary>Real-world distance in metres between two image points.</summary>
    /// <param name="a">First point (pixels).</param>
    /// <param name="b">Second point (pixels).</param>
    /// <returns>The distance in metres.</returns>
    public double DistanceMeters(PixelPoint a, PixelPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return LengthMeters(Math.Sqrt((dx * dx) + (dy * dy)));
    }

    private static void Require(double value, string name)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "Reference measurements must be positive and finite.");
        }
    }
}
