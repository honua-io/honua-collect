using Honua.Collect.App.Maps;
using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Core.Maps;
using Honua.Collect.Presentation.Geometry;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.App.Views;

/// <summary>
/// Geometry capture over a live OpenStreetMap basemap (BACKLOG G1/G2/G4): pan by
/// dragging, zoom with the +/– buttons, and tap to drop a vertex for a point,
/// line, or polygon. Screen taps are projected to lat/lon through
/// <see cref="WebMercator"/> (unit-tested in Core), so the captured vertices —
/// and the resulting GeoJSON from the tested <see cref="MapCaptureViewModel"/> —
/// are real coordinates registered to the basemap.
/// </summary>
public partial class GeometryCapturePage : ContentPage
{
    private const double TapThreshold = 12.0; // DIPs of movement under which a gesture is a tap
    private const int MinZoom = 2;
    private const int MaxZoom = 19;

    private readonly OsmTileLoader _tiles = new(Path.Combine(FileSystem.AppDataDirectory, "tiles"));
    private readonly SlippyMapDrawable _map;
    private MapCaptureViewModel _vm = new(CapturedGeometryType.Point);

    private PointF _gestureStart;
    private PointF _lastDrag;
    private double _moved;

    public GeometryCapturePage()
    {
        InitializeComponent();

        _map = new SlippyMapDrawable(_tiles) { Vertices = _vm.Vertices };
        Canvas.Drawable = _map;
        _tiles.TileLoaded += (_, _) => MainThread.BeginInvokeOnMainThread(() => Canvas.Invalidate());

        TypePicker.SelectedIndex = 0;
        UpdateStatus();
    }

    private void OnTypeChanged(object? sender, EventArgs e)
    {
        var type = TypePicker.SelectedIndex switch
        {
            1 => CapturedGeometryType.Line,
            2 => CapturedGeometryType.Polygon,
            _ => CapturedGeometryType.Point,
        };

        _vm = new MapCaptureViewModel(type);
        _map.Vertices = _vm.Vertices;
        _map.IsPolygon = type == CapturedGeometryType.Polygon;
        Canvas.Invalidate();
        UpdateStatus();
    }

    private void OnStart(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0)
        {
            return;
        }

        _gestureStart = e.Touches[0];
        _lastDrag = _gestureStart;
        _moved = 0;
    }

    private void OnDrag(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0)
        {
            return;
        }

        var p = e.Touches[0];
        var dx = p.X - _lastDrag.X;
        var dy = p.Y - _lastDrag.Y;
        _moved += Math.Abs(dx) + Math.Abs(dy);
        _lastDrag = p;

        // Pan: shift the centre opposite the drag so the map follows the finger.
        var (cx, cy) = WebMercator.ToWorldPixel(_map.Center, _map.Zoom);
        _map.Center = WebMercator.FromWorldPixel(cx - dx, cy - dy, _map.Zoom);
        Canvas.Invalidate();
    }

    private void OnEnd(object? sender, TouchEventArgs e)
    {
        // A gesture that barely moved is a tap → drop a vertex under the finger.
        if (_moved > TapThreshold)
        {
            return;
        }

        var point = e.Touches.Length > 0 ? e.Touches[0] : _gestureStart;
        var geo = WebMercator.FromScreen(point.X, point.Y, _map.Center, _map.Zoom, Canvas.Width, Canvas.Height);
        _vm.AddVertex(geo);
        Canvas.Invalidate();
        UpdateStatus();
    }

    private void OnZoomIn(object? sender, EventArgs e) => SetZoom(_map.Zoom + 1);

    private void OnZoomOut(object? sender, EventArgs e) => SetZoom(_map.Zoom - 1);

    private void SetZoom(int zoom)
    {
        _map.Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Canvas.Invalidate();
    }

    private async void OnDownloadArea(object? sender, EventArgs e)
    {
        // Take this area offline: prefetch the visible viewport across the current
        // zoom + 2 levels into the persistent tile cache.
        var w = Canvas.Width;
        var h = Canvas.Height;
        var topLeft = WebMercator.FromScreen(0, 0, _map.Center, _map.Zoom, w, h);
        var bottomRight = WebMercator.FromScreen(w, h, _map.Center, _map.Zoom, w, h);
        var bbox = GeoBoundingBox.FromCorners(topLeft, bottomRight);

        var progress = new Progress<TilePrefetchProgress>(p =>
            StatusLabel.Text = $"Downloading offline area… {p.Completed}/{p.Total}");

        StatusLabel.Text = "Preparing offline area…";
        var plan = await _tiles.PrefetchAreaAsync(bbox, _map.Zoom, _map.Zoom + 2, progress);
        StatusLabel.Text = plan.ExceedsCap
            ? $"Area too large ({plan.Count} tiles) — zoom in and retry."
            : $"Offline area ready: {plan.Count} tiles cached.";
        Canvas.Invalidate();
    }

    private void OnUndo(object? sender, EventArgs e)
    {
        if (_vm.UndoCommand.CanExecute(null))
        {
            _vm.UndoCommand.Execute(null);
            Canvas.Invalidate();
            UpdateStatus();
        }
    }

    private void OnClear(object? sender, EventArgs e)
    {
        _vm.ClearCommand.Execute(null);
        Canvas.Invalidate();
        UpdateStatus();
    }

    private async void OnDone(object? sender, EventArgs e)
    {
        if (!_vm.IsComplete)
        {
            StatusLabel.Text = $"{_vm.GeometryType} needs more vertices.";
            return;
        }

        await DisplayAlert("Geometry captured", _vm.ToGeoJson(), "OK");
    }

    private void UpdateStatus()
        => StatusLabel.Text = $"{_vm.GeometryType}: {_vm.Vertices.Count} vertex(es){(_vm.IsComplete ? " ✓" : string.Empty)}";
}
