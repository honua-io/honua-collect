using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

/// <summary>
/// A circular geofence — a centre and a radius in metres — used to tell when a
/// field worker has reached an assignment's location (BACKLOG, #40). Distance is
/// the SDK's spherical (great-circle) calculation, so it is accurate for the
/// short ranges field work cares about.
/// </summary>
public sealed record Geofence
{
    /// <summary>Creates a geofence around a centre point.</summary>
    /// <param name="center">The centre of the fence.</param>
    /// <param name="radiusMeters">The radius in metres (must be positive).</param>
    public Geofence(FieldGeoPoint center, double radiusMeters)
    {
        ArgumentNullException.ThrowIfNull(center);
        if (radiusMeters <= 0 || double.IsNaN(radiusMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(radiusMeters), radiusMeters, "Radius must be positive.");
        }

        Center = center;
        RadiusMeters = radiusMeters;
    }

    /// <summary>The centre of the fence.</summary>
    public FieldGeoPoint Center { get; }

    /// <summary>The radius in metres.</summary>
    public double RadiusMeters { get; }

    /// <summary>Great-circle distance in metres from the centre to a point.</summary>
    /// <param name="point">The point to measure to.</param>
    /// <returns>Distance in metres.</returns>
    public double DistanceMeters(FieldGeoPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        // The SDK returns null only when a point lacks coordinates; treat that as
        // "infinitely far" so an uncomputable distance never reads as inside the fence.
        return SphericalFieldDistanceCalculator.Instance.CalculateDistanceMeters(Center, point)
            ?? double.PositiveInfinity;
    }

    /// <summary>Whether a point is within the fence (inclusive of the boundary).</summary>
    /// <param name="point">The point to test.</param>
    /// <returns><see langword="true"/> when inside or on the boundary.</returns>
    public bool Contains(FieldGeoPoint point) => DistanceMeters(point) <= RadiusMeters;
}
