using System.Collections.Concurrent;
using Honua.Collect.Core.Maps;
using Microsoft.Maui.Graphics.Platform;
using IImage = Microsoft.Maui.Graphics.IImage;

namespace Honua.Collect.App.Maps;

/// <summary>
/// Fetches OpenStreetMap raster tiles (the XYZ slippy-map scheme) for the
/// embedded map (BACKLOG G4) with no third-party map SDK or API key. Tiles are
/// backed by a persistent on-disk <see cref="TileCache"/> so a viewed or
/// pre-downloaded area keeps working fully offline and survives app restarts;
/// decoded images are also held in an in-memory cache as a fast layer above
/// disk. Misses are loaded asynchronously; <see cref="TileLoaded"/> fires when a
/// fetch completes so the view can redraw. <see cref="PrefetchAreaAsync"/>
/// downloads a whole geographic area into the disk cache for offline use.
/// </summary>
public sealed class OsmTileLoader : IDisposable
{
    private const string UrlTemplate = "https://tile.openstreetmap.org/{0}/{1}/{2}.png";

    private readonly HttpClient _http;
    private readonly TileCache _disk;
    private readonly ConcurrentDictionary<string, IImage> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly SemaphoreSlim _gate = new(6); // be polite to the tile server

    /// <summary>Raised on the thread pool when a requested tile finishes loading.</summary>
    public event EventHandler? TileLoaded;

    /// <summary>
    /// Creates a loader with a descriptive User-Agent (required by the OSM tile
    /// policy), backed by an on-disk cache rooted at <paramref name="cacheRoot"/>.
    /// </summary>
    /// <param name="cacheRoot">
    /// The directory under which tiles are persisted, e.g.
    /// <c>Path.Combine(FileSystem.AppDataDirectory, "tiles")</c>.
    /// </param>
    /// <param name="http">
    /// The HTTP client for tile requests, from <c>IHttpClientFactory</c> (the
    /// caller owns its lifetime, so this type does not dispose it).
    /// </param>
    public OsmTileLoader(string cacheRoot, HttpClient http)
    {
        _disk = new TileCache(cacheRoot);
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Returns the decoded tile if available, otherwise <see langword="null"/>
    /// and schedules a background load that raises <see cref="TileLoaded"/>. A
    /// disk-cached tile is decoded and returned without any network access, so
    /// the map works offline for any area previously viewed or prefetched.
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
            _ = LoadAsync(key, zoom, x, y);
        }

        return null;
    }

    private async Task LoadAsync(string key, int zoom, int x, int y)
    {
        try
        {
            // Disk first: an offline-cached tile decodes without the network.
            if (_disk.TryGetPath(zoom, x, y, out var path))
            {
                if (TryDecodeInto(key, path))
                {
                    TileLoaded?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var bytes = await DownloadAsync(zoom, x, y).ConfigureAwait(false);
                if (bytes is null)
                {
                    return;
                }

                // Persist before decoding so a future session/offline run finds it.
                await _disk.SaveAsync(zoom, x, y, bytes).ConfigureAwait(false);

                using var ms = new MemoryStream(bytes, writable: false);
                var img = PlatformImage.FromStream(ms);
                if (img is not null)
                {
                    _cache[key] = img;
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

    private bool TryDecodeInto(string key, string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var img = PlatformImage.FromStream(fs);
            if (img is not null)
            {
                _cache[key] = img;
                return true;
            }
        }
        catch
        {
            // A corrupt/truncated cached tile falls through to a network fetch.
        }

        return false;
    }

    private async Task<byte[]?> DownloadAsync(int zoom, int x, int y)
    {
        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, UrlTemplate, zoom, x, y);
        using var response = await _http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads every tile covering <paramref name="bbox"/> across the inclusive
    /// zoom range into the on-disk cache, so the area works fully offline. Tiles
    /// already cached are skipped. Refuses areas larger than
    /// <paramref name="maxTiles"/> by returning the over-cap plan without
    /// downloading, so callers can warn and let the user narrow the area.
    /// </summary>
    /// <param name="bbox">The geographic area to make available offline.</param>
    /// <param name="minZoom">The lowest (coarsest) zoom, inclusive.</param>
    /// <param name="maxZoom">The highest (most detailed) zoom, inclusive.</param>
    /// <param name="progress">Reports tiles completed out of the total to fetch.</param>
    /// <param name="maxTiles">The cap on tiles per area.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan that was executed (or refused when it exceeds the cap).</returns>
    public async Task<OfflineAreaPlan> PrefetchAreaAsync(
        GeoBoundingBox bbox,
        int minZoom,
        int maxZoom,
        IProgress<TilePrefetchProgress>? progress = null,
        int maxTiles = OfflineAreaPlanner.DefaultMaxTiles,
        CancellationToken cancellationToken = default)
    {
        var plan = OfflineAreaPlanner.Plan(bbox, minZoom, maxZoom, maxTiles);
        if (plan.ExceedsCap)
        {
            return plan;
        }

        var total = plan.Count;
        var done = 0;
        progress?.Report(new TilePrefetchProgress(0, total));

        foreach (var t in plan.Tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_disk.Contains(t.Zoom, t.X, t.Y))
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var bytes = await DownloadAsync(t.Zoom, t.X, t.Y).ConfigureAwait(false);
                    if (bytes is not null)
                    {
                        await _disk.SaveAsync(t.Zoom, t.X, t.Y, bytes, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // A single failed tile shouldn't abort the whole area; it
                    // will be re-fetched on demand later when viewed.
                }
                finally
                {
                    _gate.Release();
                }
            }

            done++;
            progress?.Report(new TilePrefetchProgress(done, total));
        }

        return plan;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The HttpClient is owned by IHttpClientFactory, not this type.
        _gate.Dispose();
    }
}

/// <summary>Progress for an offline-area prefetch: tiles completed out of the total.</summary>
/// <param name="Completed">Tiles finished so far.</param>
/// <param name="Total">Total tiles in the area.</param>
public readonly record struct TilePrefetchProgress(int Completed, int Total)
{
    /// <summary>Completion fraction in [0, 1]; 1 when there is nothing to fetch.</summary>
    public double Fraction => Total <= 0 ? 1.0 : (double)Completed / Total;
}
