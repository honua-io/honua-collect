using System.Text;
using System.Text.Json;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

/// <summary>
/// The kind of geometry being captured.
/// </summary>
public enum CapturedGeometryType
{
    /// <summary>A single position.</summary>
    Point,

    /// <summary>An ordered path of two or more vertices.</summary>
    Line,

    /// <summary>A closed ring of three or more vertices.</summary>
    Polygon,
}

/// <summary>
/// Stateful geometry capture for a record (BACKLOG G1/G2): tap-to-add a point,
/// or build a line/polygon vertex by vertex with undo and vertex editing. GPS
/// averaging (G3) feeds high-accuracy vertices via <see cref="GpsAverager"/>.
/// Produces RFC 7946 GeoJSON and writes the result onto the record.
/// </summary>
public sealed class GeometryCaptureSession
{
    /// <summary>Default snap tolerance, in metres, when snapping is enabled without an explicit tolerance.</summary>
    public const double DefaultSnapToleranceMeters = 5.0;

    private readonly List<FieldGeoPoint> _vertices = [];
    private readonly List<SnapTarget> _snapTargets = [];

    /// <summary>Creates a capture session for a geometry type.</summary>
    /// <param name="type">Kind of geometry to capture.</param>
    public GeometryCaptureSession(CapturedGeometryType type) => Type = type;

    /// <summary>The geometry type being captured.</summary>
    public CapturedGeometryType Type { get; }

    /// <summary>
    /// Whether incoming vertices snap to nearby feature vertices/edges (BACKLOG G7).
    /// Off by default so behaviour is unchanged unless explicitly enabled.
    /// </summary>
    public bool SnapEnabled { get; set; }

    /// <summary>
    /// Maximum distance, in metres, a captured vertex may be moved to snap to a
    /// target. Only used when <see cref="SnapEnabled"/> is <see langword="true"/>.
    /// </summary>
    public double SnapToleranceMeters { get; set; } = DefaultSnapToleranceMeters;

    /// <summary>The snap target geometries currently in scope.</summary>
    public IReadOnlyList<SnapTarget> SnapTargets => _snapTargets;

    /// <summary>The captured vertices, in order.</summary>
    public IReadOnlyList<FieldGeoPoint> Vertices => _vertices;

    /// <summary>Number of vertices captured.</summary>
    public int Count => _vertices.Count;

    /// <summary>
    /// Whether the geometry has enough vertices to be valid: 1 for a point, 2+
    /// for a line, 3+ for a polygon.
    /// </summary>
    public bool IsComplete => Type switch
    {
        CapturedGeometryType.Point => _vertices.Count == 1,
        CapturedGeometryType.Line => _vertices.Count >= 2,
        CapturedGeometryType.Polygon => _vertices.Count >= 3,
        _ => false,
    };

    /// <summary>
    /// Replaces the snap targets used when <see cref="SnapEnabled"/> is set
    /// (BACKLOG G7). Passing an empty set (or <see langword="null"/>) clears them.
    /// </summary>
    /// <param name="targets">Candidate features to snap captured vertices to.</param>
    public void SetSnapTargets(IEnumerable<SnapTarget>? targets)
    {
        _snapTargets.Clear();
        if (targets is not null)
        {
            _snapTargets.AddRange(targets.Where(t => t is not null && t.Vertices.Count > 0));
        }
    }

    /// <summary>
    /// Adds a vertex. For a point, the single vertex is replaced rather than
    /// appended. When <see cref="SnapEnabled"/> is set and the vertex lies within
    /// <see cref="SnapToleranceMeters"/> of a snap target, the snapped position is
    /// stored instead (BACKLOG G7).
    /// </summary>
    /// <param name="vertex">Vertex position.</param>
    /// <returns>The snap result describing whether (and how) the vertex was snapped.</returns>
    public SnapResult AddVertex(FieldGeoPoint vertex)
    {
        ArgumentNullException.ThrowIfNull(vertex);

        var snap = SnapEnabled && _snapTargets.Count > 0
            ? GeoSnapping.Snap(vertex, _snapTargets, SnapToleranceMeters)
            : new SnapResult(SnapKind.None, vertex, double.PositiveInfinity);

        if (Type == CapturedGeometryType.Point)
        {
            _vertices.Clear();
        }

        _vertices.Add(snap.Point);
        return snap;
    }

    /// <summary>Adds the averaged position from a GPS averager as a vertex.</summary>
    /// <param name="averager">A GPS averager with at least one sample.</param>
    public void AddAveragedVertex(GpsAverager averager)
    {
        ArgumentNullException.ThrowIfNull(averager);
        AddVertex(averager.Average());
    }

    /// <summary>Removes the most recently added vertex.</summary>
    /// <returns><see langword="true"/> if a vertex was removed.</returns>
    public bool Undo()
    {
        if (_vertices.Count == 0)
        {
            return false;
        }

        _vertices.RemoveAt(_vertices.Count - 1);
        return true;
    }

    /// <summary>Moves an existing vertex (drag-to-edit).</summary>
    /// <param name="index">Zero-based vertex index.</param>
    /// <param name="vertex">New position.</param>
    public void MoveVertex(int index, FieldGeoPoint vertex)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        if (index < 0 || index >= _vertices.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _vertices[index] = vertex;
    }

    /// <summary>Clears all vertices.</summary>
    public void Clear() => _vertices.Clear();

    /// <summary>Serializes the captured geometry as RFC 7946 GeoJSON.</summary>
    /// <returns>GeoJSON geometry text.</returns>
    public string ToGeoJson()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException($"{Type} geometry is incomplete ({Count} vertex/vertices).");
        }

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            switch (Type)
            {
                case CapturedGeometryType.Point:
                    writer.WriteString("type", "Point");
                    WritePosition(writer, "coordinates", _vertices[0]);
                    break;
                case CapturedGeometryType.Line:
                    writer.WriteString("type", "LineString");
                    writer.WriteStartArray("coordinates");
                    foreach (var v in _vertices)
                    {
                        WritePositionArray(writer, v);
                    }

                    writer.WriteEndArray();
                    break;
                case CapturedGeometryType.Polygon:
                    writer.WriteString("type", "Polygon");
                    writer.WriteStartArray("coordinates");
                    writer.WriteStartArray(); // single exterior ring
                    foreach (var v in _vertices)
                    {
                        WritePositionArray(writer, v);
                    }

                    WritePositionArray(writer, _vertices[0]); // close the ring
                    writer.WriteEndArray();
                    writer.WriteEndArray();
                    break;
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Writes the captured geometry onto a record: a point sets
    /// <see cref="FieldRecord.Location"/>; a line/polygon stores GeoJSON under the
    /// given field id (and the first vertex as the record location for mapping).
    /// </summary>
    /// <param name="record">Record to write to.</param>
    /// <param name="fieldId">Field id to store line/polygon GeoJSON under.</param>
    public void ApplyTo(FieldRecord record, string fieldId)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);

        if (!IsComplete)
        {
            throw new InvalidOperationException($"{Type} geometry is incomplete ({Count} vertex/vertices).");
        }

        if (Type == CapturedGeometryType.Point)
        {
            record.Location = _vertices[0];
            return;
        }

        record.Values[fieldId] = ToGeoJson();
        record.Location ??= _vertices[0];
    }

    private static void WritePosition(Utf8JsonWriter writer, string property, FieldGeoPoint point)
    {
        writer.WriteStartArray(property);
        writer.WriteNumberValue(point.Longitude);
        writer.WriteNumberValue(point.Latitude);
        writer.WriteEndArray();
    }

    private static void WritePositionArray(Utf8JsonWriter writer, FieldGeoPoint point)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(point.Longitude);
        writer.WriteNumberValue(point.Latitude);
        writer.WriteEndArray();
    }
}
