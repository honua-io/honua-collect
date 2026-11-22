// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Geometry;

public sealed class MapCaptureViewModel : ObservableObject
{
    private readonly GeometryCaptureSession _session;

    public MapCaptureViewModel(CapturedGeometryType type)
    {
        _session = new GeometryCaptureSession(type);
        Vertices = [];
        UndoCommand = new RelayCommand(Undo, () => Vertices.Count > 0);
        ClearCommand = new RelayCommand(Clear, () => Vertices.Count > 0);
    }

    private GpsAverager? _averaging;

    public CapturedGeometryType GeometryType => _session.Type;

    public ObservableCollection<FieldGeoPoint> Vertices { get; }

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

    public bool IsAveraging => _averaging is not null;

    public int AveragingSampleCount => _averaging?.SampleCount ?? 0;

    public bool IsComplete => _session.IsComplete;

    public ICommand UndoCommand { get; }

    public ICommand ClearCommand { get; }

    public void SetSnapTargets(IEnumerable<SnapTarget>? targets) => _session.SetSnapTargets(targets);

    public SnapResult AddVertex(FieldGeoPoint vertex)
    {
        var snap = _session.AddVertex(vertex);
        SyncVertices();
        return snap;
    }

    public void AddAveragedVertex(GpsAverager averager)
    {
        _session.AddAveragedVertex(averager);
        SyncVertices();
    }

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

    public void BeginGpsAveraging()
    {
        _averaging = new GpsAverager();
        OnPropertyChanged(nameof(IsAveraging));
        OnPropertyChanged(nameof(AveragingSampleCount));
    }

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

    public void ApplyTo(FieldRecord record, string fieldId) => _session.ApplyTo(record, fieldId);

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
