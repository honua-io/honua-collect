using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Vision;

/// <summary>
/// A projected (planar, metres) ground offset east/north of the camera, on a
/// local tangent plane. Kept as its own type so projected coordinates are never
/// funnelled through the WGS84-only <see cref="FieldGeoPoint"/>; the conversion to
/// geographic happens once, explicitly, in <see cref="GroundProjection"/>.
/// </summary>
/// <param name="EastMeters">Metres east of the camera (+east, −west).</param>
/// <param name="NorthMeters">Metres north of the camera (+north, −south).</param>
public readonly record struct GroundOffset(double EastMeters, double NorthMeters)
{
    /// <summary>Ground range (horizontal distance) from the camera, in metres.</summary>
    public double RangeMeters => Math.Sqrt((EastMeters * EastMeters) + (NorthMeters * NorthMeters));
}

/// <summary>
/// The camera pose at capture time, used to anchor an image-space detection to a
/// geographic ground location (epic #43 — "anchor CV results to geometry"). The
/// camera sits at <see cref="Position"/> looking along <see cref="BearingDegrees"/>
/// (compass degrees, 0 = north, clockwise), with a horizontal field of view of
/// <see cref="HorizontalFovDegrees"/>. Targets are assumed to lie on a flat
/// ground plane (the "ground-plane assumption"), so the horizontal angular offset
/// of an image point plus a known ground range yields a bearing and distance to
/// the target.
/// </summary>
/// <param name="Position">Camera geographic position (WGS84).</param>
/// <param name="BearingDegrees">Compass bearing the optical axis points, 0=N clockwise.</param>
/// <param name="HorizontalFovDegrees">Horizontal field of view in degrees (0..180 exclusive).</param>
public sealed record CapturePose(FieldGeoPoint Position, double BearingDegrees, double HorizontalFovDegrees)
{
    /// <summary>Validates the pose's angular fields are finite and in range.</summary>
    /// <returns>This pose.</returns>
    public CapturePose Validated()
    {
        ArgumentNullException.ThrowIfNull(Position);
        if (double.IsNaN(BearingDegrees) || double.IsInfinity(BearingDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(BearingDegrees), BearingDegrees, "Bearing must be finite.");
        }

        if (!(HorizontalFovDegrees > 0 && HorizontalFovDegrees < 180))
        {
            throw new ArgumentOutOfRangeException(
                nameof(HorizontalFovDegrees), HorizontalFovDegrees, "Horizontal FOV must be in (0,180) degrees.");
        }

        return this;
    }
}
