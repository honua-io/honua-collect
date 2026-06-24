namespace Honua.Collect.Core.Field.Geometry;

/// <summary>
/// A 2D coordinate in a layer's own coordinate reference system (CRS), in that
/// CRS's native units (metres for a projected CRS, degrees for a geographic one).
/// </summary>
/// <remarks>
/// This type is deliberately CRS-neutral: it carries no WGS84 assumption and is
/// never re-projected internally. Topology operations (snap, foot-of-perpendicular,
/// self-intersection, segment length) are pure planar geometry, so they are correct
/// in whatever CRS the caller's features are already expressed in. Funnelling
/// projected coordinates through a lat/lon type — as the #302 bbox regression did —
/// silently corrupts them; keeping the math in <see cref="PlanarPoint"/> avoids that.
/// </remarks>
/// <param name="X">The easting / longitude ordinate, in the CRS's units.</param>
/// <param name="Y">The northing / latitude ordinate, in the CRS's units.</param>
public readonly record struct PlanarPoint(double X, double Y)
{
    /// <summary>The squared Euclidean distance to <paramref name="other"/>, in CRS units².</summary>
    /// <param name="other">The other point.</param>
    /// <returns>The squared distance (avoids a square root when only comparing).</returns>
    public double DistanceSquaredTo(PlanarPoint other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>The Euclidean distance to <paramref name="other"/>, in CRS units.</summary>
    /// <param name="other">The other point.</param>
    /// <returns>The distance.</returns>
    public double DistanceTo(PlanarPoint other) => Math.Sqrt(DistanceSquaredTo(other));
}
