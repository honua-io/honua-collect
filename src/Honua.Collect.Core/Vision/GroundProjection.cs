using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Vision;

/// <summary>
/// Projects an image-space detection onto a geographic ground location given the
/// <see cref="CapturePose"/> and a known ground range to the target (epic #43 —
/// anchor CV results to geometry). The horizontal offset of the image point
/// within the camera's field of view gives the angular bearing from the optical
/// axis; combined with the ground range under the flat-ground assumption, this
/// yields a planar east/north offset which is then converted to a WGS84 point on
/// a local equirectangular tangent plane.
/// </summary>
/// <remarks>
/// CRS handling: the offset is computed entirely in projected metres
/// (<see cref="GroundOffset"/>) using a rectilinear pinhole model, and converted
/// to lat/lon only at the final step. The same metres-per-degree constants the
/// snapping/averaging helpers use are applied, with longitude scaled by the
/// cosine of the camera latitude so the plane is metric near the camera.
/// </remarks>
public static class GroundProjection
{
    // Match Honua.Collect.Core.Field.Geometry.GeoSnapping's local-plane constants.
    private const double MetersPerDegreeLat = 110_540.0;
    private const double MetersPerDegreeLonEquator = 111_320.0;

    /// <summary>
    /// Computes the ground-plane east/north offset of a normalized image x-position
    /// at a known ground range, relative to the camera. Uses a rectilinear pinhole
    /// model: the half-FOV sets the focal length so that the image edges map to
    /// ±half-FOV, and the angular offset of the point is
    /// <c>atan((2·x−1)·tan(halfFov))</c>.
    /// </summary>
    /// <param name="pose">Capture pose.</param>
    /// <param name="normalizedX">Image x in 0..1 (0=left edge, 0.5=centre, 1=right edge).</param>
    /// <param name="groundRangeMeters">Horizontal distance from camera to target, in metres.</param>
    /// <returns>The planar offset east/north of the camera.</returns>
    public static GroundOffset OffsetFor(CapturePose pose, double normalizedX, double groundRangeMeters)
    {
        ArgumentNullException.ThrowIfNull(pose);
        pose.Validated();
        if (normalizedX < 0 || normalizedX > 1 || double.IsNaN(normalizedX))
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedX), normalizedX, "Normalized x must be in [0,1].");
        }

        if (groundRangeMeters < 0 || double.IsNaN(groundRangeMeters) || double.IsInfinity(groundRangeMeters))
        {
            throw new ArgumentOutOfRangeException(
                nameof(groundRangeMeters), groundRangeMeters, "Ground range must be non-negative and finite.");
        }

        var halfFov = pose.HorizontalFovDegrees * Math.PI / 360.0; // half-FOV in radians
        // Rectilinear: a point at normalized offset u=(2x−1) from centre subtends
        // angle atan(u · tan(halfFov)) from the optical axis (+ = right of axis).
        var u = (2.0 * normalizedX) - 1.0;
        var angleOffset = Math.Atan(u * Math.Tan(halfFov));

        var targetBearingRad = (pose.BearingDegrees * Math.PI / 180.0) + angleOffset;
        var east = groundRangeMeters * Math.Sin(targetBearingRad);
        var north = groundRangeMeters * Math.Cos(targetBearingRad);
        return new GroundOffset(east, north);
    }

    /// <summary>
    /// Applies a planar ground offset (metres east/north) to a camera position and
    /// returns the WGS84 ground point, using a local equirectangular tangent plane.
    /// </summary>
    /// <param name="camera">Camera geographic position.</param>
    /// <param name="offset">Planar east/north offset in metres.</param>
    /// <returns>The geographic ground point.</returns>
    public static FieldGeoPoint ToGeographic(FieldGeoPoint camera, GroundOffset offset)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var lonScale = MetersPerDegreeLonEquator * Math.Cos(camera.Latitude * Math.PI / 180.0);
        var latitude = camera.Latitude + (offset.NorthMeters / MetersPerDegreeLat);
        var longitude = camera.Longitude + (offset.EastMeters / lonScale);
        return new FieldGeoPoint(latitude, longitude);
    }

    /// <summary>
    /// Projects a normalized image point onto a geographic ground location: the
    /// composition of <see cref="OffsetFor"/> and <see cref="ToGeographic"/>.
    /// </summary>
    /// <param name="pose">Capture pose.</param>
    /// <param name="normalizedX">Image x in 0..1.</param>
    /// <param name="groundRangeMeters">Horizontal distance from camera to target, in metres.</param>
    /// <returns>The geographic ground point of the target.</returns>
    public static FieldGeoPoint ProjectToGround(CapturePose pose, double normalizedX, double groundRangeMeters)
    {
        ArgumentNullException.ThrowIfNull(pose);
        var offset = OffsetFor(pose, normalizedX, groundRangeMeters);
        return ToGeographic(pose.Position, offset);
    }
}
