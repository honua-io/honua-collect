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
}
