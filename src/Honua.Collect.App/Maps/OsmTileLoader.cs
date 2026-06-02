using System.Collections.Concurrent;
using Microsoft.Maui.Graphics.Platform;
using IImage = Microsoft.Maui.Graphics.IImage;

namespace Honua.Collect.App.Maps;

/// <summary>
/// Fetches OpenStreetMap raster tiles (the XYZ slippy-map scheme) and caches the
/// decoded images in memory, so the embedded map (BACKLOG G4) has a real basemap
/// without any third-party map SDK or API key. Misses are loaded asynchronously;
/// <see cref="TileLoaded"/> fires when a fetch completes so the view can redraw.
/// </summary>
public sealed class OsmTileLoader : IDisposable
{
    private const string UrlTemplate = "https://tile.openstreetmap.org/{0}/{1}/{2}.png";

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, IImage> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly SemaphoreSlim _gate = new(6); // be polite to the tile server

    /// <summary>Raised on the thread pool when a requested tile finishes loading.</summary>
    public event EventHandler? TileLoaded;

    /// <summary>Creates a loader with a descriptive User-Agent (required by the OSM tile policy).</summary>
    public OsmTileLoader()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HonuaCollect/1.0 (+https://honua.io)");
    }

    /// <summary>
    /// Returns the decoded tile if cached, otherwise <see langword="null"/> and
    /// schedules a background fetch that will raise <see cref="TileLoaded"/>.
    /// </summary>
    /// <param name="zoom">Tile zoom level.</param>
    /// <param name="x">Tile column.</param>
    /// <param name="y">Tile row.</param>
    /// <returns>The cached image, or null while loading.</returns>
    public IImage? Get(int zoom, int x, int y)
    {
        var key = $"{zoom}/{x}/{y}";
        if (_cache.TryGetValue(key, out var image))
        {
            return image;
        }

        if (_inFlight.TryAdd(key, 0))
        {
            _ = FetchAsync(key, zoom, x, y);
        }

        return null;
    }

    private async Task FetchAsync(string key, int zoom, int x, int y)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, UrlTemplate, zoom, x, y);
                await using var stream = await _http.GetStreamAsync(url).ConfigureAwait(false);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms).ConfigureAwait(false);
                ms.Position = 0;

                var image = PlatformImage.FromStream(ms);
                if (image is not null)
                {
                    _cache[key] = image;
                    TileLoaded?.Invoke(this, EventArgs.Empty);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // Transient tile failures are non-fatal — the map just shows the
            // placeholder grid for that cell and retries on the next pan.
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
