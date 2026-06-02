using Microsoft.Maui.Graphics;

namespace Honua.Collect.App.Views;

/// <summary>A freehand signature / sketch capture pad backed by a GraphicsView.</summary>
public partial class SignaturePage : ContentPage
{
    private readonly InkDrawable _drawable = new();

    public SignaturePage()
    {
        InitializeComponent();
        Canvas.Drawable = _drawable;
    }

    private void OnStart(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length > 0)
        {
            _drawable.BeginStroke(e.Touches[0]);
            Canvas.Invalidate();
        }
    }

    private void OnDrag(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length > 0)
        {
            _drawable.Extend(e.Touches[0]);
            Canvas.Invalidate();
        }
    }

    private void OnEnd(object? sender, TouchEventArgs e) => _drawable.EndStroke();

    private void OnClear(object? sender, EventArgs e)
    {
        _drawable.Clear();
        Canvas.Invalidate();
        StatusLabel.Text = string.Empty;
    }

    private async void OnDone(object? sender, EventArgs e)
    {
        if (_drawable.IsEmpty)
        {
            StatusLabel.Text = "Nothing to capture.";
            return;
        }

        await DisplayAlert("Signature captured", $"{_drawable.StrokeCount} stroke(s), {_drawable.PointCount} points.", "OK");
    }

    private sealed class InkDrawable : IDrawable
    {
        private readonly List<List<PointF>> _strokes = [];

        public bool IsEmpty => _strokes.Count == 0;

        public int StrokeCount => _strokes.Count;

        public int PointCount => _strokes.Sum(s => s.Count);

        public void BeginStroke(PointF p) => _strokes.Add([p]);

        public void Extend(PointF p)
        {
            if (_strokes.Count > 0)
            {
                _strokes[^1].Add(p);
            }
        }

        public void EndStroke()
        {
        }

        public void Clear() => _strokes.Clear();

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FillColor = Colors.White;
            canvas.FillRectangle(dirtyRect);
            canvas.StrokeColor = Colors.Black;
            canvas.StrokeSize = 2.5f;
            canvas.StrokeLineCap = LineCap.Round;

            foreach (var stroke in _strokes)
            {
                if (stroke.Count < 2)
                {
                    continue;
                }

                var path = new PathF();
                path.MoveTo(stroke[0].X, stroke[0].Y);
                for (var i = 1; i < stroke.Count; i++)
                {
                    path.LineTo(stroke[i].X, stroke[i].Y);
                }

                canvas.DrawPath(path);
            }
        }
    }
}
