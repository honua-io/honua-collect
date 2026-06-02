using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Presentation.Geometry;
using Microsoft.Maui.Graphics;

namespace Honua.Collect.App.Views;

/// <summary>
/// Geometry capture surface: tap the canvas to drop vertices for a point, line,
/// or polygon. Backed by the tested <see cref="MapCaptureViewModel"/>; canvas
/// taps are mapped to lat/lon so the same runtime produces GeoJSON.
/// </summary>
public partial class GeometryCapturePage : ContentPage
{
    private MapCaptureViewModel _vm = new(CapturedGeometryType.Point);
    private readonly GeometryDrawable _drawable = new();
    private Rect _canvasBounds;

    public GeometryCapturePage()
    {
        InitializeComponent();
        Canvas.Drawable = _drawable;
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
        _drawable.Points.Clear();
        Canvas.Invalidate();
        UpdateStatus();
    }

    private void OnCanvasTapped(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0)
        {
            return;
        }

        var p = e.Touches[0];
        _canvasBounds = new Rect(0, 0, Canvas.Width, Canvas.Height);

        // Map the canvas point to a lon/lat within a demo extent so the runtime
        // (and resulting GeoJSON) sees real coordinates.
        var lon = -158.05 + (p.X / Math.Max(1, Canvas.Width)) * 0.40;   // ~Oahu extent
        var lat = 21.45 - (p.Y / Math.Max(1, Canvas.Height)) * 0.20;
        _vm.AddVertex(new Honua.Sdk.Field.Records.FieldGeoPoint(lat, lon));

        _drawable.Points.Add(p);
        _drawable.IsPolygon = _vm.GeometryType == CapturedGeometryType.Polygon;
        Canvas.Invalidate();
        UpdateStatus();
    }

    private void OnUndo(object? sender, EventArgs e)
    {
        if (_vm.UndoCommand.CanExecute(null))
        {
            _vm.UndoCommand.Execute(null);
            if (_drawable.Points.Count > 0)
            {
                _drawable.Points.RemoveAt(_drawable.Points.Count - 1);
            }

            Canvas.Invalidate();
            UpdateStatus();
        }
    }

    private void OnClear(object? sender, EventArgs e)
    {
        _vm.ClearCommand.Execute(null);
        _drawable.Points.Clear();
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

    /// <summary>Draws the captured vertices and the connecting line/ring.</summary>
    private sealed class GeometryDrawable : IDrawable
    {
        public List<PointF> Points { get; } = [];

        public bool IsPolygon { get; set; }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FillColor = Colors.White;
            canvas.FillRectangle(dirtyRect);

            if (Points.Count == 0)
            {
                return;
            }

            canvas.StrokeColor = Color.FromArgb("#3F51B5");
            canvas.StrokeSize = 3;

            if (Points.Count > 1)
            {
                var path = new PathF();
                path.MoveTo(Points[0].X, Points[0].Y);
                for (var i = 1; i < Points.Count; i++)
                {
                    path.LineTo(Points[i].X, Points[i].Y);
                }

                if (IsPolygon && Points.Count >= 3)
                {
                    path.Close();
                }

                canvas.DrawPath(path);
            }

            canvas.FillColor = Color.FromArgb("#FF5722");
            foreach (var p in Points)
            {
                canvas.FillCircle(p.X, p.Y, 6);
            }
        }
    }
}
