namespace Honua.Collect.Core.Vision;

/// <summary>
/// An axis-aligned bounding box in <em>normalized image coordinates</em>: the
/// origin (0,0) is the top-left of the image and (1,1) the bottom-right, so the
/// box is resolution-independent and survives downscaling/cropping that the
/// capture pipeline may apply before a model ever sees the bytes.
/// </summary>
/// <param name="X">Left edge, 0..1 across the image width.</param>
/// <param name="Y">Top edge, 0..1 down the image height.</param>
/// <param name="Width">Box width as a fraction of image width, 0..1.</param>
/// <param name="Height">Box height as a fraction of image height, 0..1.</param>
public readonly record struct NormalizedBoundingBox(double X, double Y, double Width, double Height)
{
    /// <summary>Centre x of the box (0..1).</summary>
    public double CenterX => X + (Width / 2.0);

    /// <summary>Centre y of the box (0..1).</summary>
    public double CenterY => Y + (Height / 2.0);

    /// <summary>Validates the box lies within the unit square and has non-negative extent.</summary>
    /// <returns>This box.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If any edge falls outside 0..1.</exception>
    public NormalizedBoundingBox Validated()
    {
        if (!IsFinite(X) || !IsFinite(Y) || !IsFinite(Width) || !IsFinite(Height)
            || Width < 0 || Height < 0
            || X < 0 || Y < 0 || X + Width > 1.000001 || Y + Height > 1.000001)
        {
            throw new ArgumentOutOfRangeException(
                nameof(NormalizedBoundingBox),
                this,
                "Bounding box must lie within the normalized unit square [0,1].");
        }

        return this;
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}

/// <summary>A keypoint in normalized image coordinates (e.g. a pose joint or feature anchor).</summary>
/// <param name="Name">Semantic name of the keypoint (e.g. "tip", "base").</param>
/// <param name="X">Normalized x, 0..1.</param>
/// <param name="Y">Normalized y, 0..1.</param>
/// <param name="Confidence">Per-keypoint confidence, 0..1.</param>
public readonly record struct DetectionKeypoint(string Name, double X, double Y, double Confidence);

/// <summary>
/// A single detected object/defect in a captured image: its class label, the
/// model's confidence, and where it is in the frame (normalized box, plus
/// optional instance mask and keypoints). Provider-agnostic — a future on-device
/// model and the test stub both produce these.
/// </summary>
/// <param name="Label">Detected class label (e.g. "utility-pole", "corrosion").</param>
/// <param name="Confidence">Model confidence in the range 0..1.</param>
/// <param name="BoundingBox">Location in normalized image coordinates.</param>
public sealed record Detection(string Label, double Confidence, NormalizedBoundingBox BoundingBox)
{
    /// <summary>
    /// Optional per-pixel instance mask as a run-length-agnostic flat coverage map.
    /// Each value is 0..1 coverage; <see cref="MaskWidth"/> columns per row. Null
    /// when the model produces boxes only.
    /// </summary>
    public IReadOnlyList<double>? Mask { get; init; }

    /// <summary>Mask column count; required and positive when <see cref="Mask"/> is set.</summary>
    public int MaskWidth { get; init; }

    /// <summary>Optional keypoints (pose/landmarks) in normalized image coordinates.</summary>
    public IReadOnlyList<DetectionKeypoint> Keypoints { get; init; } = [];
}

/// <summary>
/// The output of a detection pass over one image: the detections found and the
/// source image's pixel dimensions, which callers need to turn normalized boxes
/// back into pixel measurements for photogrammetry.
/// </summary>
/// <param name="Detections">Detections found, in the model's order.</param>
/// <param name="ImageWidth">Source image width in pixels.</param>
/// <param name="ImageHeight">Source image height in pixels.</param>
public sealed record DetectionResult(IReadOnlyList<Detection> Detections, int ImageWidth, int ImageHeight)
{
    /// <summary>An empty result for an image of the given pixel size.</summary>
    /// <param name="imageWidth">Image width in pixels.</param>
    /// <param name="imageHeight">Image height in pixels.</param>
    /// <returns>A result with no detections.</returns>
    public static DetectionResult Empty(int imageWidth, int imageHeight)
        => new([], imageWidth, imageHeight);
}
