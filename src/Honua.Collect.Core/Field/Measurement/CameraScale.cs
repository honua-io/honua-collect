namespace Honua.Collect.Core.Field.Measurement;

/// <summary>
/// Photogrammetric measurement from camera intrinsics (epic #43, the
/// camera-instrinsics path alongside <see cref="ReferenceScale"/>'s known-object
/// path). Given the camera's focal length, the imaging plane resolution, and the
/// distance to the subject, this converts a pixel measurement of a subject lying
/// roughly perpendicular to the optical axis into a real-world length using the
/// thin-lens similar-triangles relation
/// <c>real = pixel · distance / focalLengthInPixels</c>.
/// </summary>
/// <remarks>
/// Like <see cref="ReferenceScale"/> this is "photogrammetry-lite": it assumes a
/// fronto-parallel subject at a single known distance, with no lens-distortion
/// correction. The focal length is taken in pixels; build one from a physical
/// focal length and sensor geometry with <see cref="FromSensor"/>.
/// </remarks>
public sealed class CameraScale
{
    private CameraScale(double focalLengthPixels, double distanceMeters)
    {
        FocalLengthPixels = focalLengthPixels;
        DistanceMeters = distanceMeters;
    }

    /// <summary>Focal length expressed in pixels of the captured image.</summary>
    public double FocalLengthPixels { get; }

    /// <summary>Distance from the camera to the subject plane, in metres.</summary>
    public double DistanceMeters { get; }

    /// <summary>Real-world metres represented by one pixel at the subject distance.</summary>
    public double MetersPerPixel => DistanceMeters / FocalLengthPixels;

    /// <summary>
    /// Builds a scale directly from a focal length already expressed in pixels.
    /// </summary>
    /// <param name="focalLengthPixels">Focal length in pixels.</param>
    /// <param name="distanceMeters">Distance to the subject plane, in metres.</param>
    /// <returns>The derived scale.</returns>
    public static CameraScale FromFocalLengthPixels(double focalLengthPixels, double distanceMeters)
    {
        Require(focalLengthPixels, nameof(focalLengthPixels));
        Require(distanceMeters, nameof(distanceMeters));
        return new CameraScale(focalLengthPixels, distanceMeters);
    }

    /// <summary>
    /// Builds a scale from physical sensor geometry: the lens focal length and the
    /// sensor's physical width are combined with the image's pixel width to recover
    /// the focal length in pixels (<c>f_px = f_mm · imageWidthPixels / sensorWidthMm</c>).
    /// </summary>
    /// <param name="focalLengthMillimeters">Lens focal length in millimetres.</param>
    /// <param name="sensorWidthMillimeters">Sensor width in millimetres.</param>
    /// <param name="imageWidthPixels">Captured image width in pixels.</param>
    /// <param name="distanceMeters">Distance to the subject plane, in metres.</param>
    /// <returns>The derived scale.</returns>
    public static CameraScale FromSensor(
        double focalLengthMillimeters,
        double sensorWidthMillimeters,
        double imageWidthPixels,
        double distanceMeters)
    {
        Require(focalLengthMillimeters, nameof(focalLengthMillimeters));
        Require(sensorWidthMillimeters, nameof(sensorWidthMillimeters));
        Require(imageWidthPixels, nameof(imageWidthPixels));
        var focalLengthPixels = focalLengthMillimeters * imageWidthPixels / sensorWidthMillimeters;
        return FromFocalLengthPixels(focalLengthPixels, distanceMeters);
    }

    /// <summary>Converts a pixel length to real-world metres at the subject distance.</summary>
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

    /// <summary>Real-world distance in metres between two image points at the subject distance.</summary>
    /// <param name="a">First point (pixels).</param>
    /// <param name="b">Second point (pixels).</param>
    /// <returns>The distance in metres.</returns>
    public double DistanceBetween(PixelPoint a, PixelPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return LengthMeters(Math.Sqrt((dx * dx) + (dy * dy)));
    }

    private static void Require(double value, string name)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "Camera parameters must be positive and finite.");
        }
    }
}
