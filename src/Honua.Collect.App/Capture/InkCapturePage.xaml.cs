using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;

namespace Honua.Collect.App.Capture;

/// <summary>
/// A freehand ink pad used for both signature (C4) and sketch (C5) capture. The
/// strokes are rasterized to a white-background PNG and the file path is returned
/// to the caller, so the captured ink is stored and uploaded as ordinary image
/// media. Shown modally via <see cref="CaptureAsync"/>.
/// </summary>
public partial class InkCapturePage : ContentPage
{
    private readonly InkDrawable _drawable = new();
    private readonly TaskCompletionSource<string?> _result = new();

    /// <summary>The page heading (e.g. "Signature" or "Sketch").</summary>
    public string Title { get; }

    private InkCapturePage(string title)
    {
        Title = title;
        InitializeComponent();
        BindingContext = this;
        Canvas.Drawable = _drawable;
    }

    /// <summary>
    /// Presents the ink pad modally and returns the saved PNG path, or
    /// <see langword="null"/> if the user cancelled or drew nothing.
    /// </summary>
    /// <param name="navigation">The navigation stack to present on.</param>
    /// <param name="title">The page heading.</param>
    /// <returns>The captured image path, or null.</returns>
    public static async Task<string?> CaptureAsync(INavigation navigation, string title)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        var page = new InkCapturePage(title);
        await navigation.PushModalAsync(page);
        return await page._result.Task;
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

    private async void OnCancel(object? sender, EventArgs e)
    {
        _result.TrySetResult(null);
        await Navigation.PopModalAsync();
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        if (_drawable.IsEmpty)
        {
            StatusLabel.Text = "Nothing to capture.";
            return;
        }

        var width = (int)Math.Max(1, Canvas.Width);
        var height = (int)Math.Max(1, Canvas.Height);

        var path = CaptureFiles.NewPath(".png");
        using (var context = new PlatformBitmapExportContext(width, height, 1))
        {
            _drawable.Draw(context.Canvas, new RectF(0, 0, width, height));
            using var stream = File.Create(path);
            context.WriteToStream(stream);
        }

        _result.TrySetResult(path);
        await Navigation.PopModalAsync();
    }

    /// <summary>Accumulates freehand strokes and draws them on a white background.</summary>
    private sealed class InkDrawable : IDrawable
    {
        private readonly List<List<PointF>> _strokes = [];

        public bool IsEmpty => _strokes.Count == 0;

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
            canvas.StrokeLineJoin = LineJoin.Round;

            foreach (var stroke in _strokes)
            {
                if (stroke.Count == 1)
                {
                    canvas.FillColor = Colors.Black;
                    canvas.FillCircle(stroke[0].X, stroke[0].Y, 1.5f);
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
