using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

/// <summary>What a captured point snapped to.</summary>
public enum SnapKind
{
    /// <summary>Nothing within tolerance; the original point is kept.</summary>
    None,

    /// <summary>Snapped to an existing vertex.</summary>
    Vertex,

    /// <summary>Snapped to the nearest point on an edge.</summary>
    Edge,
}

/// <summary>The outcome of a snap attempt.</summary>
/// <param name="Kind">What was snapped to.</param>
/// <param name="Point">The resulting point (snapped, or the original when <see cref="SnapKind.None"/>).</param>
/// <param name="DistanceMeters">Distance from the original point to the snap target.</param>
public sealed record SnapResult(SnapKind Kind, FieldGeoPoint Point, double DistanceMeters);

/// <summary>
/// Snaps a captured point to the vertices and edges of nearby features
/// (BACKLOG G7 — snap-to-feature / topology assist). This keeps adjacent
/// geometries coincident — shared boundaries, connected lines — which is what
/// makes field-captured GIS data topologically clean. Distances use a local
/// equirectangular projection in metres, accurate at snapping tolerances.
/// </summary>
public static class GeoSnapping
{
    private const double MetersPerDegreeLat = 110_540.0;
    private const double MetersPerDegreeLonEquator = 111_320.0;

    /// <summary>
    /// Snaps <paramref name="point"/> to the nearest vertex or edge of a candidate
    /// geometry within <paramref name="toleranceMeters"/>. Vertices win ties so
    /// captures prefer coincident nodes over mid-edge points.
    /// </summary>
    /// <param name="point">Captured point.</param>
    /// <param name="candidate">Candidate geometry vertices, in order.</param>
    /// <param name="toleranceMeters">Maximum snap distance.</param>
    /// <param name="closed">Whether the candidate is a closed ring (polygon).</param>
    /// <returns>The snap result.</returns>
    public static SnapResult Snap(
        FieldGeoPoint point,
        IReadOnlyList<FieldGeoPoint> candidate,
        double toleranceMeters,
        bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(candidate);
        if (toleranceMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceMeters));
        }

        if (candidate.Count == 0)
        {
            return new SnapResult(SnapKind.None, point, double.PositiveInfinity);
        }

        var lat0Rad = point.Latitude * Math.PI / 180.0;
        var lonScale = MetersPerDegreeLonEquator * Math.Cos(lat0Rad);

        (double X, double Y) ToLocal(FieldGeoPoint p) =>
            ((p.Longitude - point.Longitude) * lonScale, (p.Latitude - point.Latitude) * MetersPerDegreeLat);

        FieldGeoPoint ToGeo(double x, double y) => new(
            point.Latitude + y / MetersPerDegreeLat,
            point.Longitude + x / lonScale);

        // Nearest vertex.
        var bestVertexDist = double.PositiveInfinity;
        (double X, double Y) bestVertex = default;
        foreach (var vertex in candidate)
        {
            var (vx, vy) = ToLocal(vertex);
            var d = Math.Sqrt((vx * vx) + (vy * vy));
            if (d < bestVertexDist)
            {
                bestVertexDist = d;
                bestVertex = (vx, vy);
            }
        }

        // Nearest point on any edge.
        var bestEdgeDist = double.PositiveInfinity;
        (double X, double Y) bestEdge = default;
        var segmentCount = closed ? candidate.Count : candidate.Count - 1;
        for (var i = 0; i < segmentCount; i++)
        {
            var a = ToLocal(candidate[i]);
            var b = ToLocal(candidate[(i + 1) % candidate.Count]);
            var (px, py, dist) = ClosestOnSegment(a, b);
            if (dist < bestEdgeDist)
            {
                bestEdgeDist = dist;
                bestEdge = (px, py);
            }
        }

        // Prefer a vertex when it is no farther than the edge (ties to vertex).
        if (bestVertexDist <= toleranceMeters && bestVertexDist <= bestEdgeDist + 1e-6)
        {
            return new SnapResult(SnapKind.Vertex, ToGeo(bestVertex.X, bestVertex.Y), bestVertexDist);
        }

        if (bestEdgeDist <= toleranceMeters)
        {
            return new SnapResult(SnapKind.Edge, ToGeo(bestEdge.X, bestEdge.Y), bestEdgeDist);
        }

        return new SnapResult(SnapKind.None, point, Math.Min(bestVertexDist, bestEdgeDist));
    }

    private static (double X, double Y, double Distance) ClosestOnSegment((double X, double Y) a, (double X, double Y) b)
    {
        // Origin (0,0) is the captured point; find the closest point on a→b.
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var lengthSq = (abx * abx) + (aby * aby);

        double t = 0;
        if (lengthSq > 0)
        {
            t = -((a.X * abx) + (a.Y * aby)) / lengthSq;
            t = Math.Clamp(t, 0, 1);
        }

        var px = a.X + (t * abx);
        var py = a.Y + (t * aby);
        return (px, py, Math.Sqrt((px * px) + (py * py)));
    }
}
