using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Presentation.Geometry;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Tests;

public class MapCaptureViewModelTests
{
    [Fact]
    public void Snapping_disabled_by_default_keeps_the_tapped_vertex()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Line);
        vm.SetSnapTargets([new SnapTarget([new(0.0, 0.0), new(0.0, 0.01)])]);

        var snap = vm.AddVertex(new FieldGeoPoint(0.00002, 0.0)); // ~2 m off a vertex

        Assert.False(vm.SnapEnabled);
        Assert.Equal(SnapKind.None, snap.Kind);
        Assert.Equal(0.00002, vm.Vertices[0].Latitude, 6);
    }

    [Fact]
    public void Snapping_enabled_snaps_a_near_vertex_to_the_target()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Line)
        {
            SnapEnabled = true,
            SnapToleranceMeters = 5,
        };
        vm.SetSnapTargets([new SnapTarget([new(0.0, 0.0), new(0.0, 0.01)])]);

        var snap = vm.AddVertex(new FieldGeoPoint(0.00002, 0.0));

        Assert.Equal(SnapKind.Vertex, snap.Kind);
        Assert.Equal(0.0, vm.Vertices[0].Latitude, 6);
        Assert.Equal(0.0, vm.Vertices[0].Longitude, 6);
    }

    [Fact]
    public void SnapEnabled_raises_property_changed()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Point);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SnapEnabled = true;

        Assert.Contains(nameof(MapCaptureViewModel.SnapEnabled), raised);
    }

    [Fact]
    public void Averaging_a_batch_of_fixes_commits_a_single_tightened_vertex()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Point);

        vm.AddAveragedVertex(new FieldGeoPoint[]
        {
            new(10.0, 20.0, 4),
            new(10.2, 20.2, 4),
            new(9.8, 19.8, 4),
            new(10.0, 20.0, 4),
        });

        Assert.Single(vm.Vertices);
        Assert.Equal(10.0, vm.Vertices[0].Latitude, 3);
        Assert.Equal(20.0, vm.Vertices[0].Longitude, 3);
        // mean accuracy 4 m reduced by sqrt(4) = 2 -> 2 m.
        Assert.Equal(2.0, vm.Vertices[0].AccuracyMeters!.Value, 3);
    }

    [Fact]
    public void Live_averaging_run_accumulates_then_commits_one_vertex()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Point);

        Assert.False(vm.IsAveraging);
        vm.BeginGpsAveraging();
        Assert.True(vm.IsAveraging);

        vm.AddGpsSample(new FieldGeoPoint(5.0, 5.0, 6));
        vm.AddGpsSample(new FieldGeoPoint(5.0, 5.0, 6));
        Assert.Equal(2, vm.AveragingSampleCount);

        vm.CommitAveragedVertex();

        Assert.False(vm.IsAveraging);
        Assert.Equal(0, vm.AveragingSampleCount);
        Assert.Single(vm.Vertices);
        Assert.Equal(5.0, vm.Vertices[0].Latitude, 3);
    }

    [Fact]
    public void Cancelling_an_averaging_run_commits_nothing()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Point);
        vm.BeginGpsAveraging();
        vm.AddGpsSample(new FieldGeoPoint(1, 1));

        vm.CancelGpsAveraging();

        Assert.False(vm.IsAveraging);
        Assert.Empty(vm.Vertices);
    }

    [Fact]
    public void Averaging_an_empty_batch_throws()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Point);
        Assert.Throws<ArgumentException>(() => vm.AddAveragedVertex(Array.Empty<FieldGeoPoint>()));
    }

    [Fact]
    public void SnapEnabled_set_to_same_value_is_a_no_op()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Line);
        var changes = 0;
        vm.PropertyChanged += (_, _) => changes++;

        vm.SnapEnabled = false; // already false

        Assert.Equal(0, changes);
    }

    [Fact]
    public void SnapTolerance_changes_only_on_a_new_value()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Line);
        var original = vm.SnapToleranceMeters;
        var changes = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SnapToleranceMeters)) changes++; };

        vm.SnapToleranceMeters = original; // no-op
        Assert.Equal(0, changes);

        vm.SnapToleranceMeters = original + 5;
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Undo_and_Clear_remove_vertices_and_gate_their_commands()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Line);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.ClearCommand.CanExecute(null));

        vm.AddVertex(new FieldGeoPoint(1, 1));
        vm.AddVertex(new FieldGeoPoint(2, 2));
        Assert.True(vm.UndoCommand.CanExecute(null));

        vm.UndoCommand.Execute(null);
        Assert.Single(vm.Vertices);

        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Vertices);
        Assert.False(vm.ClearCommand.CanExecute(null));
    }

    [Fact]
    public void AddGpsSample_before_begin_throws()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Point);
        Assert.Throws<InvalidOperationException>(() => vm.AddGpsSample(new FieldGeoPoint(1, 1)));
    }

    [Fact]
    public void Commit_without_samples_throws()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Point);
        vm.BeginGpsAveraging();
        Assert.Throws<InvalidOperationException>(() => vm.CommitAveragedVertex());
    }

    [Fact]
    public void Cancel_with_no_run_is_a_no_op()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Point);
        vm.CancelGpsAveraging(); // no run in progress
        Assert.False(vm.IsAveraging);
    }

    [Fact]
    public void ApplyTo_and_ToGeoJson_emit_the_captured_geometry()
    {
        var vm = new MapCaptureViewModel(CapturedGeometryType.Point);
        vm.AddVertex(new FieldGeoPoint(12.5, -1.25));
        Assert.True(vm.IsComplete);

        var json = vm.ToGeoJson();
        Assert.Contains("Point", json, StringComparison.OrdinalIgnoreCase);

        var record = new FieldRecord { RecordId = "r", FormId = "f" };
        vm.ApplyTo(record, "geom");
        Assert.NotNull(record.Location);
    }
}
