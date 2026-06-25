using Honua.Collect.Core.Field.Geometry;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// A rectangular spatial extent expressed in a <em>layer's own CRS units</em>
/// (metres for a projected CRS, degrees for a geographic one), for selective
/// sync by area (BACKLOG S2). Unlike <see cref="SyncAreaBounds"/> — which is
/// fixed to WGS84 decimal degrees — this carries no lat/lon assumption and tests
/// projected <see cref="PlanarPoint"/> coordinates directly.
/// </summary>
/// <remarks>
/// This is the #302 lesson made structural: the original bbox regression silently
/// corrupted projected easting/northing by routing them through a WGS84
/// lat/lon type. A layer in, say, a State Plane or UTM CRS must be filtered in
/// those same units, so the extent is compared against a <see cref="PlanarPoint"/>
/// the caller produced from the record in the layer CRS — no re-projection happens
/// here. The min/max ordinates are normalised at construction so the box is
/// orientation-independent.
/// </remarks>
public sealed record SyncExtent
{
    /// <summary>Creates an extent from two opposite corners in the layer CRS units.</summary>
    /// <param name="minX">One X (easting/longitude) ordinate.</param>
    /// <param name="minY">One Y (northing/latitude) ordinate.</param>
    /// <param name="maxX">The other X ordinate.</param>
    /// <param name="maxY">The other Y ordinate.</param>
    public SyncExtent(double minX, double minY, double maxX, double maxY)
    {
        MinX = Math.Min(minX, maxX);
        MinY = Math.Min(minY, maxY);
        MaxX = Math.Max(minX, maxX);
        MaxY = Math.Max(minY, maxY);
    }

    /// <summary>Western/lower X edge, in CRS units.</summary>
    public double MinX { get; }

    /// <summary>Southern/lower Y edge, in CRS units.</summary>
    public double MinY { get; }

    /// <summary>Eastern/upper X edge, in CRS units.</summary>
    public double MaxX { get; }

    /// <summary>Northern/upper Y edge, in CRS units.</summary>
    public double MaxY { get; }

    /// <summary>Whether a projected point falls within (or on the edge of) this extent.</summary>
    /// <param name="point">A point in the same CRS as the extent.</param>
    /// <returns><see langword="true"/> when the point is inside.</returns>
    public bool Contains(PlanarPoint point)
        => point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;
}
