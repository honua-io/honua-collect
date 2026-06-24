namespace Honua.Collect.Core.Field.Geometry;

/// <summary>What a candidate point snapped to.</summary>
public enum PlanarSnapKind
{
    /// <summary>Nothing within tolerance; the original point is kept.</summary>
    None,

    /// <summary>Snapped to an existing vertex.</summary>
    Vertex,

    /// <summary>Snapped to the foot of the perpendicular on an edge (segment).</summary>
    Edge,
}

/// <summary>
/// The outcome of a planar snap attempt, in the candidate features' CRS.
/// </summary>
/// <param name="Kind">What, if anything, was snapped to.</param>
/// <param name="Point">
/// The resulting coordinate: the snap target when <see cref="Kind"/> is
/// <see cref="PlanarSnapKind.Vertex"/> or <see cref="PlanarSnapKind.Edge"/>,
/// otherwise the original candidate.
/// </param>
/// <param name="Distance">
/// Distance from the original candidate to the snap target, in CRS units; positive
/// infinity when there is nothing to snap to.
/// </param>
/// <param name="FeatureIndex">
/// Index of the feature in the supplied set that owns the snap target, or -1 when
/// nothing snapped.
/// </param>
/// <param name="SegmentIndex">
/// For an edge snap, the index of the segment (its start-vertex index within the
/// feature) the foot lies on; -1 otherwise.
/// </param>
/// <param name="VertexIndex">
/// For a vertex snap, the index of the vertex within the feature; -1 otherwise.
/// </param>
public sealed record PlanarSnapResult(
    PlanarSnapKind Kind,
    PlanarPoint Point,
    double Distance,
    int FeatureIndex = -1,
    int SegmentIndex = -1,
    int VertexIndex = -1);

/// <summary>
/// An existing feature to snap or assist against: its ordered vertices and whether
/// they form a closed ring (polygon). Lines and points pass <see cref="Closed"/> as
/// <see langword="false"/>. Coordinates are in the layer's CRS.
/// </summary>
/// <param name="Vertices">The feature's vertices, in order.</param>
/// <param name="Closed">Whether the feature is a closed ring (polygon boundary).</param>
public sealed record PlanarFeature(IReadOnlyList<PlanarPoint> Vertices, bool Closed = false);

/// <summary>
/// Platform-neutral, CRS-neutral snap-to-feature and topology assists (BACKLOG G7).
/// </summary>
/// <remarks>
/// <para>
/// Everything here is pure planar geometry in the <em>layer's own CRS</em>: distances
/// and tolerances are Euclidean in that CRS's units (metres for a projected CRS).
/// Coordinates are never re-projected, so — unlike a lat/lon-only snapper — these
/// helpers are correct for projected data without the equirectangular approximation
/// that degrades away from the equator. Callers pass coordinates already in the
/// feature CRS; tolerances are in the same units.
/// </para>
/// <para>
/// Operations: nearest-vertex snap, nearest-edge snap (foot of the perpendicular),
/// nearest-of-many across a feature set, shared-vertex insertion onto a coincident
/// edge, close-ring snapping, self-intersection detection, and a minimum
/// segment-length gate. All are deterministic.
/// </para>
/// </remarks>
public static class PlanarTopology
{
    /// <summary>
    /// Snaps <paramref name="point"/> to the nearest vertex or edge of a single
    /// candidate <paramref name="feature"/> within <paramref name="tolerance"/>.
    /// Vertices win ties so captures prefer coincident nodes over mid-edge points.
    /// </summary>
    /// <param name="point">The candidate coordinate, in the feature CRS.</param>
    /// <param name="feature">The feature to snap to.</param>
    /// <param name="tolerance">Maximum snap distance, in CRS units (must be &gt; 0).</param>
    /// <returns>The snap result.</returns>
    public static PlanarSnapResult Snap(PlanarPoint point, PlanarFeature feature, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return SnapToFeature(point, feature, tolerance, featureIndex: 0);
    }

    /// <summary>
    /// Snaps <paramref name="point"/> to the nearest vertex or edge across several
    /// candidate <paramref name="features"/>, returning the closest hit within
    /// <paramref name="tolerance"/>. A vertex hit wins ties against an equally-close
    /// edge hit.
    /// </summary>
    /// <param name="point">The candidate coordinate, in the feature CRS.</param>
    /// <param name="features">The features to snap against.</param>
    /// <param name="tolerance">Maximum snap distance, in CRS units (must be &gt; 0).</param>
    /// <returns>The best snap result, or <see cref="PlanarSnapKind.None"/> when nothing is in tolerance.</returns>
    public static PlanarSnapResult Snap(
        PlanarPoint point,
        IReadOnlyList<PlanarFeature> features,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(features);
        ValidateTolerance(tolerance);

        var best = new PlanarSnapResult(PlanarSnapKind.None, point, double.PositiveInfinity);
        for (var f = 0; f < features.Count; f++)
        {
            var feature = features[f];
            if (feature is null || feature.Vertices.Count == 0)
            {
                continue;
            }

            var candidate = SnapToFeature(point, feature, tolerance, f);
            if (candidate.Kind == PlanarSnapKind.None)
            {
                continue;
            }

            // Keep the closest hit; on an effective tie prefer a vertex snap.
            if (candidate.Distance < best.Distance - Epsilon ||
                (candidate.Distance <= best.Distance + Epsilon &&
                 candidate.Kind == PlanarSnapKind.Vertex && best.Kind != PlanarSnapKind.Vertex))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Inserts <paramref name="point"/> as a shared vertex on the segment of
    /// <paramref name="feature"/> whose foot-of-perpendicular it falls within
    /// <paramref name="tolerance"/> of, returning the feature's new vertex list. This
    /// is the topology assist that makes a new vertex coincident with a neighbour's
    /// edge (splitting that edge) so the boundary stays shared. When the point is
    /// closest to an existing vertex (within tolerance) no insertion is made —
    /// the caller should snap to that vertex instead. Returns <see langword="null"/>
    /// when nothing is within tolerance.
    /// </summary>
    /// <param name="point">The new coordinate, in the feature CRS.</param>
    /// <param name="feature">The feature whose edge should gain the shared vertex.</param>
    /// <param name="tolerance">Maximum distance from an edge, in CRS units (must be &gt; 0).</param>
    /// <returns>The feature's vertices with the point inserted, or <see langword="null"/>.</returns>
    public static IReadOnlyList<PlanarPoint>? InsertSharedVertex(
        PlanarPoint point,
        PlanarFeature feature,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(feature);
        var snap = SnapToFeature(point, feature, tolerance, featureIndex: 0);

        // Only split an edge; a vertex-class hit means the node already exists.
        if (snap.Kind != PlanarSnapKind.Edge)
        {
            return null;
        }

        var vertices = new List<PlanarPoint>(feature.Vertices);
        // SegmentIndex is the start-vertex index; the foot is inserted just after it.
        vertices.Insert(snap.SegmentIndex + 1, snap.Point);
        return vertices;
    }

    /// <summary>
    /// Close-ring snapping: when an open vertex list's last vertex is within
    /// <paramref name="tolerance"/> of its first, returns the ring closed by
    /// replacing that last vertex with an exact copy of the first (so the ring is
    /// numerically closed). Returns the input unchanged when it is already closed, has
    /// fewer than three distinct vertices, or its ends are farther apart than the
    /// tolerance.
    /// </summary>
    /// <param name="vertices">The ring's vertices, in order, in the layer CRS.</param>
    /// <param name="tolerance">Maximum end-to-end gap to close, in CRS units (must be &gt; 0).</param>
    /// <param name="closed">Set to whether the returned ring is closed.</param>
    /// <returns>The (possibly) closed vertex list.</returns>
    public static IReadOnlyList<PlanarPoint> CloseRing(
        IReadOnlyList<PlanarPoint> vertices,
        double tolerance,
        out bool closed)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ValidateTolerance(tolerance);

        closed = false;
        if (vertices.Count < 3)
        {
            return vertices;
        }

        var first = vertices[0];
        var last = vertices[^1];

        // Already coincident endpoints: report closed, leave untouched.
        if (first.DistanceSquaredTo(last) <= Epsilon * Epsilon)
        {
            closed = true;
            return vertices;
        }

        if (first.DistanceTo(last) > tolerance)
        {
            return vertices;
        }

        var ring = new List<PlanarPoint>(vertices) { [^1] = first };
        closed = true;
        return ring;
    }

    /// <summary>
    /// Whether the polyline/ring defined by <paramref name="vertices"/> is simple
    /// (has no self-intersection). Adjacent segments may share their common endpoint;
    /// for a closed ring the first and last segments may share the ring's closing
    /// point. Any other crossing or overlap counts as a self-intersection.
    /// </summary>
    /// <param name="vertices">The ordered vertices, in the layer CRS.</param>
    /// <param name="closed">Whether the vertices form a closed ring.</param>
    /// <returns><see langword="true"/> when the geometry intersects itself.</returns>
    public static bool HasSelfIntersection(IReadOnlyList<PlanarPoint> vertices, bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(vertices);

        var ring = closed ? Closed(vertices) : vertices;
        var segmentCount = ring.Count - 1;
        if (segmentCount < 2)
        {
            return false;
        }

        for (var i = 0; i < segmentCount; i++)
        {
            var a1 = ring[i];
            var a2 = ring[i + 1];
            for (var j = i + 1; j < segmentCount; j++)
            {
                // Skip the shared endpoint between adjacent segments.
                var adjacent = j == i + 1;

                // For a closed ring, the first and last segments legitimately share
                // the closing vertex — treat them as adjacent too.
                var wrapAdjacent = closed && i == 0 && j == segmentCount - 1;

                if (SegmentsIntersect(a1, a2, ring[j], ring[j + 1], adjacent || wrapAdjacent))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The index of the first segment shorter than <paramref name="minLength"/>, or
    /// -1 when every segment meets the minimum. Use this to reject near-duplicate
    /// vertices (a sliver edge) before committing a capture.
    /// </summary>
    /// <param name="vertices">The ordered vertices, in the layer CRS.</param>
    /// <param name="minLength">Minimum acceptable segment length, in CRS units (must be &gt;= 0).</param>
    /// <param name="closed">Whether the vertices form a closed ring (includes the closing segment).</param>
    /// <returns>The offending segment's start-vertex index, or -1.</returns>
    public static int FirstSegmentShorterThan(
        IReadOnlyList<PlanarPoint> vertices,
        double minLength,
        bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        if (minLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minLength), "Minimum length must be non-negative.");
        }

        var ring = closed ? Closed(vertices) : vertices;
        var minSq = minLength * minLength;
        for (var i = 0; i < ring.Count - 1; i++)
        {
            if (ring[i].DistanceSquaredTo(ring[i + 1]) < minSq)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether every segment of the geometry is at least <paramref name="minLength"/>
    /// long. Convenience over <see cref="FirstSegmentShorterThan"/>.
    /// </summary>
    /// <param name="vertices">The ordered vertices, in the layer CRS.</param>
    /// <param name="minLength">Minimum acceptable segment length, in CRS units.</param>
    /// <param name="closed">Whether the vertices form a closed ring.</param>
    /// <returns><see langword="true"/> when all segments meet the minimum.</returns>
    public static bool MeetsMinimumSegmentLength(
        IReadOnlyList<PlanarPoint> vertices,
        double minLength,
        bool closed = false) =>
        FirstSegmentShorterThan(vertices, minLength, closed) < 0;

    private const double Epsilon = 1e-9;

    private static PlanarSnapResult SnapToFeature(
        PlanarPoint point,
        PlanarFeature feature,
        double tolerance,
        int featureIndex)
    {
        ValidateTolerance(tolerance);
        var vertices = feature.Vertices;
        if (vertices.Count == 0)
        {
            return new PlanarSnapResult(PlanarSnapKind.None, point, double.PositiveInfinity);
        }

        // Nearest vertex.
        var bestVertexDistSq = double.PositiveInfinity;
        var bestVertexIndex = -1;
        for (var i = 0; i < vertices.Count; i++)
        {
            var d = point.DistanceSquaredTo(vertices[i]);
            if (d < bestVertexDistSq)
            {
                bestVertexDistSq = d;
                bestVertexIndex = i;
            }
        }

        // Nearest point on any edge.
        var bestEdgeDistSq = double.PositiveInfinity;
        var bestFoot = point;
        var bestSegment = -1;
        var segmentCount = feature.Closed ? vertices.Count : vertices.Count - 1;
        for (var i = 0; i < segmentCount; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            var (foot, distSq) = ClosestOnSegment(point, a, b);
            if (distSq < bestEdgeDistSq)
            {
                bestEdgeDistSq = distSq;
                bestFoot = foot;
                bestSegment = i;
            }
        }

        var tolSq = tolerance * tolerance;
        var vertexDist = Math.Sqrt(bestVertexDistSq);
        var edgeDist = Math.Sqrt(bestEdgeDistSq);

        // Prefer a vertex when it is no farther than the edge (ties to vertex).
        if (bestVertexDistSq <= tolSq && vertexDist <= edgeDist + Epsilon)
        {
            return new PlanarSnapResult(
                PlanarSnapKind.Vertex, vertices[bestVertexIndex], vertexDist,
                featureIndex, SegmentIndex: -1, VertexIndex: bestVertexIndex);
        }

        if (bestEdgeDistSq <= tolSq)
        {
            return new PlanarSnapResult(
                PlanarSnapKind.Edge, bestFoot, edgeDist,
                featureIndex, SegmentIndex: bestSegment, VertexIndex: -1);
        }

        return new PlanarSnapResult(PlanarSnapKind.None, point, Math.Min(vertexDist, edgeDist));
    }

    private static (PlanarPoint Foot, double DistanceSquared) ClosestOnSegment(
        PlanarPoint p, PlanarPoint a, PlanarPoint b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var lengthSq = (abx * abx) + (aby * aby);

        double t = 0;
        if (lengthSq > 0)
        {
            t = (((p.X - a.X) * abx) + ((p.Y - a.Y) * aby)) / lengthSq;
            t = Math.Clamp(t, 0, 1);
        }

        var foot = new PlanarPoint(a.X + (t * abx), a.Y + (t * aby));
        return (foot, p.DistanceSquaredTo(foot));
    }

    private static bool SegmentsIntersect(
        PlanarPoint p1, PlanarPoint p2, PlanarPoint p3, PlanarPoint p4, bool adjacent)
    {
        var d1 = Orientation(p3, p4, p1);
        var d2 = Orientation(p3, p4, p2);
        var d3 = Orientation(p1, p2, p3);
        var d4 = Orientation(p1, p2, p4);

        // Proper crossing: each segment straddles the other's line.
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return true;
        }

        // Collinear-overlap / touch cases. Adjacent segments are allowed to touch at
        // their shared endpoint, but a non-trivial overlap or a touch elsewhere is an
        // intersection.
        if (IsZero(d1) && OnSegment(p3, p4, p1))
        {
            return !(adjacent && Coincident(p1, p3) || adjacent && Coincident(p1, p4));
        }

        if (IsZero(d2) && OnSegment(p3, p4, p2))
        {
            return !(adjacent && Coincident(p2, p3) || adjacent && Coincident(p2, p4));
        }

        if (IsZero(d3) && OnSegment(p1, p2, p3))
        {
            return !(adjacent && Coincident(p3, p1) || adjacent && Coincident(p3, p2));
        }

        if (IsZero(d4) && OnSegment(p1, p2, p4))
        {
            return !(adjacent && Coincident(p4, p1) || adjacent && Coincident(p4, p2));
        }

        return false;
    }

    // Cross product of (b-a) x (c-a): >0 left turn, <0 right turn, 0 collinear.
    private static double Orientation(PlanarPoint a, PlanarPoint b, PlanarPoint c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    // Whether c lies within the bounding box of segment a→b (assumes collinearity).
    private static bool OnSegment(PlanarPoint a, PlanarPoint b, PlanarPoint c) =>
        c.X >= Math.Min(a.X, b.X) - Epsilon && c.X <= Math.Max(a.X, b.X) + Epsilon &&
        c.Y >= Math.Min(a.Y, b.Y) - Epsilon && c.Y <= Math.Max(a.Y, b.Y) + Epsilon;

    private static bool Coincident(PlanarPoint a, PlanarPoint b) =>
        a.DistanceSquaredTo(b) <= Epsilon * Epsilon;

    private static bool IsZero(double v) => Math.Abs(v) <= Epsilon;

    private static IReadOnlyList<PlanarPoint> Closed(IReadOnlyList<PlanarPoint> vertices)
    {
        if (vertices.Count == 0)
        {
            return vertices;
        }

        if (Coincident(vertices[0], vertices[^1]))
        {
            return vertices;
        }

        var ring = new List<PlanarPoint>(vertices.Count + 1);
        ring.AddRange(vertices);
        ring.Add(vertices[0]);
        return ring;
    }

    private static void ValidateTolerance(double tolerance)
    {
        if (tolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be positive.");
        }
    }
}
