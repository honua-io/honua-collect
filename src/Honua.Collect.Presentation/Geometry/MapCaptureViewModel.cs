using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Geometry;

/// <summary>
/// View-model for the map geometry-capture surface (BACKLOG G1/G2): tap-to-add
/// vertices for a point, line, or polygon, with undo/clear and a live
/// completeness indicator. Backed by the runtime <see cref="GeometryCaptureSession"/>,
/// so GPS averaging and GeoJSON output come for free.
/// </summary>
public sealed class MapCaptureViewModel : ObservableObject
{
    private readonly GeometryCaptureSession _session;

    /// <summary>Creates the map capture view-model for a geometry type.</summary>
    /// <param name="type">Geometry type to capture.</param>
    public MapCaptureViewModel(CapturedGeometryType type)
    {
        _session = new GeometryCaptureSession(type);
        Vertices = [];
        UndoCommand = new RelayCommand(Undo, () => Vertices.Count > 0);
        ClearCommand = new RelayCommand(Clear, () => Vertices.Count > 0);
    }

    /// <summary>The geometry type being captured.</summary>
    public CapturedGeometryType GeometryType => _session.Type;

    /// <summary>Captured vertices for map rendering.</summary>
    public ObservableCollection<FieldGeoPoint> Vertices { get; }

    /// <summary>Whether the geometry has enough vertices to be valid.</summary>
    public bool IsComplete => _session.IsComplete;

    /// <summary>Removes the last vertex (undo).</summary>
    public ICommand UndoCommand { get; }

    /// <summary>Clears all vertices.</summary>
    public ICommand ClearCommand { get; }

    /// <summary>Adds a vertex at a tapped/located position.</summary>
    /// <param name="vertex">Vertex position.</param>
    public void AddVertex(FieldGeoPoint vertex)
    {
        _session.AddVertex(vertex);
        SyncVertices();
    }

    /// <summary>Adds the averaged position from a GPS averager (high-accuracy capture).</summary>
    /// <param name="averager">A GPS averager with samples.</param>
    public void AddAveragedVertex(GpsAverager averager)
    {
        _session.AddAveragedVertex(averager);
        SyncVertices();
    }

    /// <summary>Writes the captured geometry onto a record.</summary>
    /// <param name="record">Record to write to.</param>
    /// <param name="fieldId">Field id to store line/polygon GeoJSON under.</param>
    public void ApplyTo(FieldRecord record, string fieldId) => _session.ApplyTo(record, fieldId);

    /// <summary>The captured geometry as GeoJSON (only valid when complete).</summary>
    /// <returns>GeoJSON geometry text.</returns>
    public string ToGeoJson() => _session.ToGeoJson();

    private void Undo()
    {
        _session.Undo();
        SyncVertices();
    }

    private void Clear()
    {
        _session.Clear();
        SyncVertices();
    }

    private void SyncVertices()
    {
        Vertices.Clear();
        foreach (var vertex in _session.Vertices)
        {
            Vertices.Add(vertex);
        }

        OnPropertyChanged(nameof(IsComplete));
        (UndoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
