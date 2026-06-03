using Honua.Collect.Core.Field.Annotation;
using Honua.Collect.Presentation.Field;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;
using IImage = Microsoft.Maui.Graphics.IImage;

namespace Honua.Collect.App.Capture;

/// <summary>
/// Pro photo markup pad (BACKLOG C7 — Fulcrum parity). Draws a captured photo as
/// the background of a <see cref="GraphicsView"/> and lets a field worker draw
/// freehand strokes over it in one of a few markup colors. On save the photo and
/// the strokes are flattened to a new PNG (the evidence image); the structured
/// markup is also returned as a <see cref="PhotoAnnotationOverlay"/> in normalized
/// 0..1 coordinates so the original image is never modified and the markup can be
/// re-rendered or re-edited. Shown modally via <see cref="CaptureAsync"/>.
/// </summary>
public partial class PhotoAnnotationPage : ContentPage
{
    /// <summary>The markup color choices offered to the user.</summary>
    private static readonly (string Name, string Hex)[] Palette =
    [
        ("Red", "#FF3B30"),
        ("Yellow", "#FFCC00"),
        ("Black", "#000000"),
    ];

    private readonly string _sourceImagePath;
    private readonly AnnotationDrawable _drawable;
    private readonly TaskCompletionSource<PhotoAnnotationResult?> _result = new();
    private string _color = Palette[0].Hex;

    private PhotoAnnotationPage(string sourceImagePath)
    {
        _sourceImagePath = sourceImagePath;
        _drawable = new AnnotationDrawable(LoadImage(sourceImagePath));
        InitializeComponent();
        Canvas.Drawable = _drawable;
        UpdateColorLabel();
    }

    /// <summary>
    /// Presents the markup pad modally over <paramref name="sourceImagePath"/> and
    /// returns the flattened PNG path, or <see langword="null"/> if the user
    /// cancelled or drew nothing. Use <see cref="CaptureAsync"/> for the overlay
    /// metadata as well.
    /// </summary>
    /// <param name="navigation">The navigation stack to present on.</param>
    /// <param name="sourceImagePath">Path to the photo to annotate.</param>
    /// <returns>The annotated image path, or null.</returns>
    public static async Task<string?> CaptureAsync(INavigation navigation, string sourceImagePath)
    {
        var result = await CaptureResultAsync(navigation, sourceImagePath);
        return result?.ImagePath;
    }

    /// <summary>
    /// Presents the markup pad modally and returns both the flattened PNG path and
    /// the structured <see cref="PhotoAnnotationOverlay"/>, or <see langword="null"/>
    /// if the user cancelled or drew nothing.
    /// </summary>
    /// <param name="navigation">The navigation stack to present on.</param>
    /// <param name="sourceImagePath">Path to the photo to annotate.</param>
    /// <returns>The result, or null.</returns>
    public static async Task<PhotoAnnotationResult?> CaptureResultAsync(
        INavigation navigation, string sourceImagePath)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);

        var page = new PhotoAnnotationPage(sourceImagePath);
        await navigation.PushModalAsync(page);
        return await page._result.Task;
    }

    private static IImage? LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        return PlatformImage.FromStream(stream);
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

    private void OnPickRed(object? sender, EventArgs e) => PickColor(Palette[0].Hex);

    private void OnPickYellow(object? sender, EventArgs e) => PickColor(Palette[1].Hex);

    private void OnPickBlack(object? sender, EventArgs e) => PickColor(Palette[2].Hex);

    private void PickColor(string hex)
    {
        _color = hex;
        _drawable.StrokeColor = Color.FromArgb(hex);
        UpdateColorLabel();
    }

    private void UpdateColorLabel()
    {
        var name = Array.Find(Palette, p => p.Hex == _color).Name;
        ColorLabel.Text = string.IsNullOrEmpty(name) ? string.Empty : $"Color: {name}";
    }

    private void OnUndo(object? sender, EventArgs e)
    {
        _drawable.Undo();
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

        // Flatten at the source image's native pixel size so the markup is burned
        // in at full resolution rather than at the (smaller) on-screen size.
        var width = (int)Math.Max(1, _drawable.ImageWidth);
        var height = (int)Math.Max(1, _drawable.ImageHeight);

        // The strokes were captured in display pixels; convert to a normalized
        // overlay using the on-screen canvas size, then render at native size.
        var displayWidth = Math.Max(1, Canvas.Width);
        var displayHeight = Math.Max(1, Canvas.Height);
        var overlay = _drawable.BuildOverlay(displayWidth, displayHeight);

        var path = CaptureFiles.NewPath(".png");
        using (var context = new PlatformBitmapExportContext(width, height, 1))
        {
            RenderFlattened(context.Canvas, width, height, overlay);
            using var stream = File.Create(path);
            context.WriteToStream(stream);
        }

        _result.TrySetResult(new PhotoAnnotationResult(path, overlay));
        await Navigation.PopModalAsync();
    }

    private void RenderFlattened(ICanvas canvas, int width, int height, PhotoAnnotationOverlay overlay)
    {
        var rect = new RectF(0, 0, width, height);

        if (_drawable.Image is { } image)
        {
            canvas.DrawImage(image, 0, 0, width, height);
        }
        else
        {
            canvas.FillColor = Colors.White;
            canvas.FillRectangle(rect);
        }

        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;

        foreach (var annotation in overlay.Annotations)
        {
            var pixels = PhotoAnnotationMapper.ToPixels(annotation, width, height);
            if (pixels.Count == 0)
            {
                continue;
            }

            canvas.StrokeColor = Color.FromArgb(annotation.Color);
            canvas.StrokeSize = (float)PhotoAnnotationMapper.StrokeWidthPixels(annotation, width, height);

            if (pixels.Count == 1)
            {
                canvas.FillColor = Color.FromArgb(annotation.Color);
                canvas.FillCircle((float)pixels[0].X, (float)pixels[0].Y, canvas.StrokeSize / 2f);
                continue;
            }

            var path = new PathF();
            path.MoveTo((float)pixels[0].X, (float)pixels[0].Y);
            for (var i = 1; i < pixels.Count; i++)
            {
                path.LineTo((float)pixels[i].X, (float)pixels[i].Y);
            }

            canvas.DrawPath(path);
        }
    }

    /// <summary>
    /// Draws the background photo and the in-progress strokes for the on-screen
    /// preview, and accumulates strokes (with their color) for the overlay.
    /// </summary>
    private sealed class AnnotationDrawable : IDrawable
    {
        private readonly List<Stroke> _strokes = [];

        public AnnotationDrawable(IImage? image)
        {
            Image = image;
            ImageWidth = image?.Width ?? 0f;
            ImageHeight = image?.Height ?? 0f;
            StrokeColor = Color.FromArgb(Palette[0].Hex);
        }

        public IImage? Image { get; }

        public float ImageWidth { get; }

        public float ImageHeight { get; }

        public Color StrokeColor { get; set; }

        public bool IsEmpty => _strokes.TrueForAll(s => s.Points.Count == 0);

        public void BeginStroke(PointF p) => _strokes.Add(new Stroke(StrokeColor.ToArgbHex(true), p));

        public void Extend(PointF p)
        {
            if (_strokes.Count > 0)
            {
                _strokes[^1].Points.Add(p);
            }
        }

        public void EndStroke()
        {
        }

        public void Undo()
        {
            if (_strokes.Count > 0)
            {
                _strokes.RemoveAt(_strokes.Count - 1);
            }
        }

        /// <summary>
        /// Maps the accumulated display-space strokes into a normalized overlay,
        /// preserving each stroke's color. Empty strokes are dropped.
        /// </summary>
        public PhotoAnnotationOverlay BuildOverlay(double displayWidth, double displayHeight)
        {
            var overlay = new PhotoAnnotationOverlay();
            foreach (var stroke in _strokes)
            {
                if (stroke.Points.Count == 0)
                {
                    continue;
                }

                var points = stroke.Points.Select(p => ((double)p.X, (double)p.Y));
                overlay.Add(PhotoAnnotationMapper.ToFreehand(
                    points, displayWidth, displayHeight, stroke.Color));
            }

            return overlay;
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            if (Image is { } image)
            {
                canvas.DrawImage(image, dirtyRect.X, dirtyRect.Y, dirtyRect.Width, dirtyRect.Height);
            }
            else
            {
                canvas.FillColor = Colors.Black;
                canvas.FillRectangle(dirtyRect);
            }

            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.StrokeSize = 3.5f;

            foreach (var stroke in _strokes)
            {
                if (stroke.Points.Count == 0)
                {
                    continue;
                }

                var color = Color.FromArgb(stroke.Color);
                canvas.StrokeColor = color;

                if (stroke.Points.Count == 1)
                {
                    canvas.FillColor = color;
                    canvas.FillCircle(stroke.Points[0].X, stroke.Points[0].Y, 2f);
                    continue;
                }

                var path = new PathF();
                path.MoveTo(stroke.Points[0].X, stroke.Points[0].Y);
                for (var i = 1; i < stroke.Points.Count; i++)
                {
                    path.LineTo(stroke.Points[i].X, stroke.Points[i].Y);
                }

                canvas.DrawPath(path);
            }
        }

        private sealed class Stroke(string color, PointF first)
        {
            public string Color { get; } = color;

            public List<PointF> Points { get; } = [first];
        }
    }
}

/// <summary>
/// The outcome of a <see cref="PhotoAnnotationPage"/> session: the flattened
/// evidence PNG and the structured markup overlay (normalized coordinates).
/// </summary>
/// <param name="ImagePath">Path to the flattened annotated PNG.</param>
/// <param name="Overlay">The structured markup, in normalized image coordinates.</param>
public sealed record PhotoAnnotationResult(string ImagePath, PhotoAnnotationOverlay Overlay);
