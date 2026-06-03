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

    private GpsAverager? _averaging;

    /// <summary>The geometry type being captured.</summary>
    public CapturedGeometryType GeometryType => _session.Type;

    /// <summary>Captured vertices for map rendering.</summary>
    public ObservableCollection<FieldGeoPoint> Vertices { get; }

    /// <summary>
    /// Whether captured vertices snap to nearby feature vertices/edges (BACKLOG G7).
    /// Off by default; toggling this is what the snap UI button binds to.
    /// </summary>
    public bool SnapEnabled
    {
        get => _session.SnapEnabled;
        set
        {
            if (_session.SnapEnabled == value)
            {
                return;
            }

            _session.SnapEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Snap tolerance in metres (used when <see cref="SnapEnabled"/> is set).</summary>
    public double SnapToleranceMeters
    {
        get => _session.SnapToleranceMeters;
        set
        {
            if (_session.SnapToleranceMeters.Equals(value))
            {
                return;
            }

            _session.SnapToleranceMeters = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Whether a GPS averaging run is currently collecting samples.</summary>
    public bool IsAveraging => _averaging is not null;

    /// <summary>Number of GPS samples collected in the current averaging run.</summary>
    public int AveragingSampleCount => _averaging?.SampleCount ?? 0;

    /// <summary>Whether the geometry has enough vertices to be valid.</summary>
    public bool IsComplete => _session.IsComplete;

    /// <summary>Removes the last vertex (undo).</summary>
    public ICommand UndoCommand { get; }

    /// <summary>Clears all vertices.</summary>
    public ICommand ClearCommand { get; }

    /// <summary>
    /// Replaces the snap targets used when <see cref="SnapEnabled"/> is set
    /// (BACKLOG G7). The page supplies nearby features here.
    /// </summary>
    /// <param name="targets">Candidate features to snap to.</param>
    public void SetSnapTargets(IEnumerable<SnapTarget>? targets) => _session.SetSnapTargets(targets);

    /// <summary>Adds a vertex at a tapped/located position (snapped when enabled).</summary>
    /// <param name="vertex">Vertex position.</param>
    /// <returns>The snap result describing whether the vertex was snapped.</returns>
    public SnapResult AddVertex(FieldGeoPoint vertex)
    {
        var snap = _session.AddVertex(vertex);
        SyncVertices();
        return snap;
    }

    /// <summary>Adds the averaged position from a pre-filled GPS averager (high-accuracy capture).</summary>
    /// <param name="averager">A GPS averager with samples.</param>
    public void AddAveragedVertex(GpsAverager averager)
    {
        _session.AddAveragedVertex(averager);
        SyncVertices();
    }

    /// <summary>
    /// Averages a batch of GPS fixes into a single high-accuracy vertex and commits
    /// it (BACKLOG G3). This is the device-free entry point: tests and the page can
    /// pass a captured sequence of fixes directly.
    /// </summary>
    /// <param name="fixes">GPS fixes to average; must contain at least one.</param>
    public void AddAveragedVertex(IEnumerable<FieldGeoPoint> fixes)
    {
        ArgumentNullException.ThrowIfNull(fixes);

        var averager = new GpsAverager();
        foreach (var fix in fixes)
        {
            averager.Add(fix);
        }

        if (!averager.HasSamples)
        {
            throw new ArgumentException("At least one GPS fix is required to average a vertex.", nameof(fixes));
        }

        _session.AddAveragedVertex(averager);
        SyncVertices();
    }

    /// <summary>
    /// Begins a live GPS averaging run (BACKLOG G3). The page feeds fixes via
    /// <see cref="AddGpsSample"/> as they arrive and finishes with
    /// <see cref="CommitAveragedVertex"/>.
    /// </summary>
    public void BeginGpsAveraging()
    {
        _averaging = new GpsAverager();
        OnPropertyChanged(nameof(IsAveraging));
        OnPropertyChanged(nameof(AveragingSampleCount));
    }

    /// <summary>Adds one GPS fix to the in-progress averaging run.</summary>
    /// <param name="fix">A GPS position fix.</param>
    public void AddGpsSample(FieldGeoPoint fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        if (_averaging is null)
        {
            throw new InvalidOperationException("Call BeginGpsAveraging before adding GPS samples.");
        }

        _averaging.Add(fix);
        OnPropertyChanged(nameof(AveragingSampleCount));
    }

    /// <summary>
    /// Commits the averaged position of the in-progress run as a single vertex and
    /// ends the run.
    /// </summary>
    public void CommitAveragedVertex()
    {
        if (_averaging is null || !_averaging.HasSamples)
        {
            throw new InvalidOperationException("No GPS samples have been collected to average.");
        }

        _session.AddAveragedVertex(_averaging);
        _averaging = null;
        OnPropertyChanged(nameof(IsAveraging));
        OnPropertyChanged(nameof(AveragingSampleCount));
        SyncVertices();
    }

    /// <summary>Cancels the in-progress GPS averaging run without committing a vertex.</summary>
    public void CancelGpsAveraging()
    {
        if (_averaging is null)
        {
            return;
        }

        _averaging = null;
        OnPropertyChanged(nameof(IsAveraging));
        OnPropertyChanged(nameof(AveragingSampleCount));
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
