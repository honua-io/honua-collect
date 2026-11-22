// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Geometry;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.App.Maps;

public sealed class SlippyMapDrawable : IDrawable
{
    private readonly OsmTileLoader _tiles;

    public SlippyMapDrawable(OsmTileLoader tiles) => _tiles = tiles;

    public FieldGeoPoint Center { get; set; } = new(21.31, -157.81);

    public int Zoom { get; set; } = 12;

    public IReadOnlyList<FieldGeoPoint> Vertices { get; set; } = [];

    public bool IsPolygon { get; set; }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var w = dirtyRect.Width;
        var h = dirtyRect.Height;

        // Backdrop so any un-loaded tile cells read as a neutral grid, not black.
        canvas.FillColor = Color.FromArgb("#E8EAED");
        canvas.FillRectangle(dirtyRect);

        DrawTiles(canvas, w, h);
        DrawOverlay(canvas, w, h);
        DrawAttribution(canvas, w, h);
    }

    private void DrawTiles(ICanvas canvas, float w, float h)
    {
        const int tile = WebMercator.TileSize;
        var max = (1 << Zoom) - 1;

        // World-pixel of the viewport's top-left corner.
        var (cx, cy) = WebMercator.ToWorldPixel(Center, Zoom);
        var originX = cx - w / 2.0;
        var originY = cy - h / 2.0;

        var firstCol = (int)Math.Floor(originX / tile);
        var firstRow = (int)Math.Floor(originY / tile);
        var lastCol = (int)Math.Floor((originX + w) / tile);
        var lastRow = (int)Math.Floor((originY + h) / tile);

        for (var ty = firstRow; ty <= lastRow; ty++)
        {
            if (ty < 0 || ty > max)
            {
                continue;
            }

            for (var tx = firstCol; tx <= lastCol; tx++)
            {
                if (tx < 0 || tx > max)
                {
                    continue;
                }

                var screenX = (float)(tx * tile - originX);
                var screenY = (float)(ty * tile - originY);

                var image = _tiles.Get(Zoom, tx, ty);
                if (image is not null)
                {
                    canvas.DrawImage(image, screenX, screenY, tile, tile);
                }
            }
        }
    }

    private void DrawOverlay(ICanvas canvas, float w, float h)
    {
        if (Vertices.Count == 0)
        {
            return;
        }

        var pts = new PointF[Vertices.Count];
        for (var i = 0; i < Vertices.Count; i++)
        {
            var (sx, sy) = WebMercator.ToScreen(Vertices[i], Center, Zoom, w, h);
            pts[i] = new PointF((float)sx, (float)sy);
        }

        if (pts.Length > 1)
        {
            var path = new PathF();
            path.MoveTo(pts[0].X, pts[0].Y);
            for (var i = 1; i < pts.Length; i++)
            {
                path.LineTo(pts[i].X, pts[i].Y);
            }

            if (IsPolygon && pts.Length >= 3)
            {
                path.Close();
                canvas.FillColor = Color.FromArgb("#3F51B544"); // translucent fill
                canvas.FillPath(path);
            }

            canvas.StrokeColor = Color.FromArgb("#3F51B5");
            canvas.StrokeSize = 3;
            canvas.DrawPath(path);
        }

        foreach (var p in pts)
        {
            canvas.FillColor = Colors.White;
            canvas.FillCircle(p.X, p.Y, 7);
            canvas.FillColor = Color.FromArgb("#FF5722");
            canvas.FillCircle(p.X, p.Y, 5);
        }
    }

    private static void DrawAttribution(ICanvas canvas, float w, float h)
    {
        canvas.FontColor = Colors.Black;
        canvas.FontSize = 10;
        canvas.DrawString("© OpenStreetMap contributors", w - 4, h - 4, HorizontalAlignment.Right);
    }
}
