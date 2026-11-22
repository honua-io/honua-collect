// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

public enum CapturedGeometryType
{
    Point,

    Line,

    Polygon,
}

public sealed class GeometryCaptureSession
{
    public const double DefaultSnapToleranceMeters = 5.0;

    private readonly List<FieldGeoPoint> _vertices = [];
    private readonly List<SnapTarget> _snapTargets = [];

    public GeometryCaptureSession(CapturedGeometryType type) => Type = type;

    public CapturedGeometryType Type { get; }

    public bool SnapEnabled { get; set; }

    public double SnapToleranceMeters { get; set; } = DefaultSnapToleranceMeters;

    public IReadOnlyList<SnapTarget> SnapTargets => _snapTargets;

    public IReadOnlyList<FieldGeoPoint> Vertices => _vertices;

    public int Count => _vertices.Count;

    public bool IsComplete => Type switch
    {
        CapturedGeometryType.Point => _vertices.Count == 1,
        CapturedGeometryType.Line => _vertices.Count >= 2,
        CapturedGeometryType.Polygon => _vertices.Count >= 3,
        _ => false,
    };

    public void SetSnapTargets(IEnumerable<SnapTarget>? targets)
    {
        _snapTargets.Clear();
        if (targets is not null)
        {
            _snapTargets.AddRange(targets.Where(t => t is not null && t.Vertices.Count > 0));
        }
    }

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

    public void AddAveragedVertex(GpsAverager averager)
    {
        ArgumentNullException.ThrowIfNull(averager);
        AddVertex(averager.Average());
    }

    public bool Undo()
    {
        if (_vertices.Count == 0)
        {
            return false;
        }

        _vertices.RemoveAt(_vertices.Count - 1);
        return true;
    }

    public void MoveVertex(int index, FieldGeoPoint vertex)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        if (index < 0 || index >= _vertices.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _vertices[index] = vertex;
    }

    public void Clear() => _vertices.Clear();

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
