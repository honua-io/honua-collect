// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Field.Annotation;
using Honua.Field.Presentation.Field;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;
using IImage = Microsoft.Maui.Graphics.IImage;

namespace Honua.Field.App.Capture;

/// <summary>
/// Photo markup pad (BACKLOG C7). Draws a captured photo as the background of a
/// <see cref="GraphicsView"/> and lets a field worker draw freehand strokes,
/// arrows, rectangles, ellipses, and text callouts in selectable colors. On save
/// the photo and markup are flattened to a new JPEG at native resolution; the
/// structured markup is also returned as a <see cref="PhotoAnnotationOverlay"/>
/// in normalized 0..1 coordinates so the original image is never modified and
/// the markup can be re-edited later. Shown modally via <see cref="CaptureAsync"/>
/// or <see cref="CaptureResultAsync"/>.
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

    private readonly AnnotationDrawable _drawable;
    private readonly bool _hadInitialOverlay;
    private readonly string _sourceImagePath;
    private readonly TaskCompletionSource<PhotoAnnotationResult?> _result = new();
    private string _color = Palette[0].Hex;
    private AnnotationShape _tool = AnnotationShape.Freehand;
    private bool _closing;

    private PhotoAnnotationPage(string sourceImagePath, PhotoAnnotationOverlay? initialOverlay = null)
    {
        _hadInitialOverlay = initialOverlay is { Count: > 0 };
        _sourceImagePath = sourceImagePath;
        _drawable = new AnnotationDrawable(LoadImage(sourceImagePath), initialOverlay);
        InitializeComponent();
        Canvas.Drawable = _drawable;
        UpdateToolLabel();
    }

    /// <summary>
    /// Presents the markup pad modally over <paramref name="sourceImagePath"/> and
    /// returns the flattened image path, or <see langword="null"/> if the user
    /// cancelled or drew nothing. Use <see cref="CaptureResultAsync"/> for the
    /// overlay metadata as well.
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
    /// Presents the markup pad modally and returns both the flattened image path and
    /// the structured <see cref="PhotoAnnotationOverlay"/>, or <see langword="null"/>
    /// if the user cancelled or drew nothing. Pass <paramref name="initialOverlay"/>
    /// to re-edit existing markup non-destructively.
    /// </summary>
    /// <param name="navigation">The navigation stack to present on.</param>
    /// <param name="sourceImagePath">Path to the photo to annotate.</param>
    /// <param name="initialOverlay">Optional existing markup to load for re-editing.</param>
    /// <returns>The result, or null.</returns>
    public static async Task<PhotoAnnotationResult?> CaptureResultAsync(
        INavigation navigation,
        string sourceImagePath,
        PhotoAnnotationOverlay? initialOverlay = null)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);

        var page = new PhotoAnnotationPage(sourceImagePath, initialOverlay);
        await navigation.PushModalAsync(page);
        return await page._result.Task;
    }

    private static IImage? LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        return PlatformImage.FromStream(stream);
    }

    private async void OnStart(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0)
        {
            return;
        }

        var point = e.Touches[0];
        if (_tool == AnnotationShape.Text)
        {
            var label = await DisplayPromptAsync("Add label", "Enter callout text:", "Add", "Cancel");
            if (!string.IsNullOrWhiteSpace(label))
            {
                _drawable.AddText(point, label.Trim(), Canvas.Width, Canvas.Height);
                Canvas.Invalidate();
                StatusLabel.Text = string.Empty;
            }

            return;
        }

        _drawable.BeginShape(_tool, point, Canvas.Width, Canvas.Height);
        Canvas.Invalidate();
    }

    private void OnDrag(object? sender, TouchEventArgs e)
    {
        if (_tool == AnnotationShape.Text || e.Touches.Length == 0)
        {
            return;
        }

        _drawable.ExtendShape(e.Touches[0], Canvas.Width, Canvas.Height);
        Canvas.Invalidate();
    }

    private void OnEnd(object? sender, TouchEventArgs e) => _drawable.EndShape();

    private void OnPickFreehand(object? sender, EventArgs e) => PickTool(AnnotationShape.Freehand);

    private void OnPickArrow(object? sender, EventArgs e) => PickTool(AnnotationShape.Arrow);

    private void OnPickRectangle(object? sender, EventArgs e) => PickTool(AnnotationShape.Rectangle);

    private void OnPickEllipse(object? sender, EventArgs e) => PickTool(AnnotationShape.Ellipse);

    private void OnPickText(object? sender, EventArgs e) => PickTool(AnnotationShape.Text);

    private void PickTool(AnnotationShape tool)
    {
        _tool = tool;
        UpdateToolLabel();
    }

    private void OnPickRed(object? sender, EventArgs e) => PickColor(Palette[0].Hex);

    private void OnPickYellow(object? sender, EventArgs e) => PickColor(Palette[1].Hex);

    private void OnPickBlack(object? sender, EventArgs e) => PickColor(Palette[2].Hex);

    private void PickColor(string hex)
    {
        _color = hex;
        _drawable.StrokeColorHex = hex;
        UpdateToolLabel();
    }

    private void UpdateToolLabel()
    {
        var colorName = Array.Find(Palette, p => p.Hex == _color).Name;
        ColorLabel.Text = $"{_tool} · {colorName}";
    }

    private void OnUndo(object? sender, EventArgs e)
    {
        _drawable.Undo();
        Canvas.Invalidate();
        StatusLabel.Text = string.Empty;
    }

    private void OnClear(object? sender, EventArgs e)
    {
        _drawable.Clear();
        Canvas.Invalidate();
        StatusLabel.Text = string.Empty;
    }

    private async void OnCancel(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _result.TrySetResult(null);
        await Navigation.PopModalAsync();
        _drawable.Dispose();
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        if (_drawable.IsEmpty && !_hadInitialOverlay)
        {
            StatusLabel.Text = "Nothing to capture.";
            return;
        }

        // Rendering runs off the UI thread; a second Save or Cancel tap meanwhile
        // must not render again or pop the modal twice.
        _closing = true;

        // Flatten with the Core renderer at the source image's native pixel size, so
        // the markup is burned in at full resolution and the saved evidence is the
        // same drawing that Core CI tests. JPEG keeps the attachment within the
        // field's content-type and size policy, like an unannotated photo.
        var overlay = _drawable.BuildOverlay();
        var path = CaptureFiles.NewPath(".jpg");
        try
        {
            await Task.Run(() => PhotoAnnotationRenderer.RenderToFile(_sourceImagePath, path, overlay));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or ArgumentException)
        {
            _closing = false;
            StatusLabel.Text = "Could not save the markup.";
            return;
        }

        _result.TrySetResult(new PhotoAnnotationResult(path, overlay));
        await Navigation.PopModalAsync();
        _drawable.Dispose();
    }

    private static void DrawAnnotations(
        ICanvas canvas,
        IReadOnlyList<PhotoAnnotation> annotations,
        double width,
        double height)
    {
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;

        foreach (var annotation in annotations)
        {
            var pixels = PhotoAnnotationMapper.ToPixels(annotation, width, height);
            if (pixels.Count == 0)
            {
                continue;
            }

            var strokeWidth = (float)PhotoAnnotationMapper.StrokeWidthPixels(annotation, width, height);
            var color = ParseColorOrDefault(annotation.Color);
            canvas.StrokeColor = color;
            canvas.StrokeSize = strokeWidth;

            switch (annotation.Shape)
            {
                case AnnotationShape.Freehand:
                    DrawFreehand(canvas, pixels, color, strokeWidth);
                    break;

                case AnnotationShape.Arrow when pixels.Count >= 2:
                    DrawArrow(canvas, pixels[0], pixels[^1], strokeWidth);
                    break;

                case AnnotationShape.Rectangle when pixels.Count >= 2:
                {
                    var bounds = PhotoAnnotationMapper.ToBoundsRect(annotation, width, height);
                    canvas.DrawRectangle((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height);
                    break;
                }

                case AnnotationShape.Ellipse when pixels.Count >= 2:
                {
                    var bounds = PhotoAnnotationMapper.ToBoundsRect(annotation, width, height);
                    canvas.DrawEllipse((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height);
                    break;
                }

                case AnnotationShape.Text when !string.IsNullOrWhiteSpace(annotation.Text):
                {
                    var fontSize = (float)annotation.TextSizePixels(width, height);
                    var x = (float)pixels[0].X;
                    var y = (float)pixels[0].Y;
                    canvas.FontSize = fontSize;
                    canvas.FontColor = color;
                    canvas.DrawString(annotation.Text, x, y, HorizontalAlignment.Left);
                    break;
                }
            }
        }
    }

    private static void DrawFreehand(
        ICanvas canvas,
        IReadOnlyList<(double X, double Y)> pixels,
        Color color,
        float strokeWidth)
    {
        if (pixels.Count == 1)
        {
            canvas.FillColor = color;
            canvas.FillCircle((float)pixels[0].X, (float)pixels[0].Y, strokeWidth / 2f);
            return;
        }

        var path = new PathF();
        path.MoveTo((float)pixels[0].X, (float)pixels[0].Y);
        for (var i = 1; i < pixels.Count; i++)
        {
            path.LineTo((float)pixels[i].X, (float)pixels[i].Y);
        }

        canvas.DrawPath(path);
    }

    private static void DrawArrow(
        ICanvas canvas,
        (double X, double Y) start,
        (double X, double Y) end,
        float strokeWidth)
    {
        canvas.DrawLine((float)start.X, (float)start.Y, (float)end.X, (float)end.Y);
        var head = PhotoAnnotationMapper.ComputeArrowHead(start, end, strokeWidth * 6.0);
        if (head is { } wings)
        {
            canvas.DrawLine((float)end.X, (float)end.Y, (float)wings.Left.X, (float)wings.Left.Y);
            canvas.DrawLine((float)end.X, (float)end.Y, (float)wings.Right.X, (float)wings.Right.Y);
        }
    }

    private static Color ParseColorOrDefault(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var parsed))
        {
            return parsed;
        }

        return Color.FromArgb(Palette[0].Hex);
    }

    /// <summary>
    /// Draws the background photo and all normalized annotations (including any
    /// preloaded overlay and the active in-progress shape).
    /// </summary>
    private sealed class AnnotationDrawable : IDrawable, IDisposable
    {
        private readonly List<PhotoAnnotation> _items = [];
        private readonly List<AnnotationPoint> _activePoints = [];
        private AnnotationShape? _activeShape;
        private string _activeColor = Palette[0].Hex;
        private bool _disposed;

        public AnnotationDrawable(IImage? image, PhotoAnnotationOverlay? initialOverlay)
        {
            Image = image;
            if (initialOverlay is not null)
            {
                _items.AddRange(initialOverlay.Annotations);
            }
        }

        public IImage? Image { get; private set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Image?.Dispose();
            Image = null;
        }

        public string StrokeColorHex { get; set; } = Palette[0].Hex;

        public bool IsEmpty => _items.Count == 0 && _activePoints.Count == 0;

        public void BeginShape(AnnotationShape shape, PointF p, double width, double height)
        {
            _activeShape = shape;
            _activeColor = StrokeColorHex;
            _activePoints.Clear();
            var norm = PhotoAnnotationMapper.Normalize(p.X, p.Y, width, height);
            _activePoints.Add(norm);
            if (shape is AnnotationShape.Arrow or AnnotationShape.Rectangle or AnnotationShape.Ellipse)
            {
                _activePoints.Add(norm);
            }
        }

        public void ExtendShape(PointF p, double width, double height)
        {
            if (_activeShape is null || _activePoints.Count == 0)
            {
                return;
            }

            var norm = PhotoAnnotationMapper.Normalize(p.X, p.Y, width, height);
            if (_activeShape == AnnotationShape.Freehand)
            {
                _activePoints.Add(norm);
            }
            else if (_activePoints.Count >= 2)
            {
                _activePoints[^1] = norm;
            }
        }

        public void EndShape()
        {
            if (_activeShape is { } shape && _activePoints.Count > 0 && !IsZeroSizeTwoPointShape(shape))
            {
                _items.Add(new PhotoAnnotation
                {
                    Shape = shape,
                    Points = _activePoints.ToList(),
                    Color = _activeColor,
                });
            }

            _activeShape = null;
            _activePoints.Clear();
        }

        // A tap with the arrow, rectangle or ellipse tool has the same start and end
        // point; saving it would add an invisible shape to the evidence overlay.
        private bool IsZeroSizeTwoPointShape(AnnotationShape shape) =>
            shape is not AnnotationShape.Freehand
            && _activePoints.Count >= 2
            && _activePoints[0].Equals(_activePoints[^1]);

        public void AddText(PointF p, string text, double width, double height)
        {
            _items.Add(PhotoAnnotationMapper.ToText((p.X, p.Y), text, width, height, StrokeColorHex));
        }

        public void Undo()
        {
            if (_items.Count > 0)
            {
                _items.RemoveAt(_items.Count - 1);
            }
        }

        public void Clear()
        {
            _items.Clear();
            _activeShape = null;
            _activePoints.Clear();
        }

        public PhotoAnnotationOverlay BuildOverlay() => new(_items);

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

            var toDraw = _items;
            if (_activeShape is { } shape && _activePoints.Count > 0 && !IsZeroSizeTwoPointShape(shape))
            {
                toDraw =
                [
                    .. _items,
                    new PhotoAnnotation { Shape = shape, Points = _activePoints.ToList(), Color = _activeColor },
                ];
            }

            DrawAnnotations(canvas, toDraw, dirtyRect.Width, dirtyRect.Height);
        }
    }
}

/// <summary>
/// The outcome of a <see cref="PhotoAnnotationPage"/> session: the flattened
/// evidence JPEG and the structured markup overlay (normalized coordinates).
/// </summary>
/// <param name="ImagePath">Path to the flattened annotated JPEG.</param>
/// <param name="Overlay">The structured markup, in normalized image coordinates.</param>
public sealed record PhotoAnnotationResult(string ImagePath, PhotoAnnotationOverlay Overlay);
