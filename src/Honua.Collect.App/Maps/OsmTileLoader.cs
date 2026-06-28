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
    /// <summary>
    /// Upper bound on decoded tiles held in memory. Each decoded tile is a full RGBA
    /// bitmap (~256 KB for a 256px tile), so this caps the memory layer at roughly a
    /// few tens of MB; the least-recently-used tile is evicted and disposed past it.
    /// The on-disk <see cref="TileCache"/> remains the (bounded) backing store, so an
    /// evicted tile is simply re-decoded from disk on the next view.
    /// </summary>
    private const int MaxCachedTiles = 256;

    /// <summary>Per-tile network deadline so a slow fetch releases its concurrency slot quickly.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly TileCache _disk;
    private readonly LruImageCache _cache = new(MaxCachedTiles);
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
        var key = OsmTileUrl.CacheKey(zoom, x, y);
        if (_cache.TryGet(key, out var image))
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
                    _cache.Set(key, img);
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
                _cache.Set(key, img);
                return true;
            }
        }
        catch
        {
            // A corrupt/truncated cached tile falls through to a network fetch.
        }

        return false;
    }

    private async Task<byte[]?> DownloadAsync(int zoom, int x, int y, CancellationToken cancellationToken = default)
    {
        var url = OsmTileUrl.For(zoom, x, y);

        // Bound each fetch independently of the HttpClient default so a stalled tile
        // releases the concurrency gate promptly instead of holding it for ~100s and
        // starving the rest of the map. Linked to the caller's token so a real cancel
        // (e.g. an aborted prefetch) still propagates.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);

        using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
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

        // Dispatch every missing tile concurrently and let the shared 6-slot gate cap
        // in-flight downloads. Awaiting each download inside the foreach (the previous
        // shape) serialized the whole run — the gate gave zero parallelism — so a large
        // offline area downloaded ~6x slower than the design's budget and one slow tile
        // stalled everything behind it. The same gate also bounds the on-demand Get
        // path, so total concurrency across both stays at 6.
        var tasks = new List<Task>(total);
        foreach (var t in plan.Tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_disk.Contains(t.Zoom, t.X, t.Y))
            {
                ReportTileDone();
                continue;
            }

            tasks.Add(FetchTileAsync(t));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return plan;

        async Task FetchTileAsync(TileCoordinate t)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var bytes = await DownloadAsync(t.Zoom, t.X, t.Y, cancellationToken).ConfigureAwait(false);
                if (bytes is not null)
                {
                    await _disk.SaveAsync(t.Zoom, t.X, t.Y, bytes, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A real cancellation of the prefetch aborts the whole run.
                throw;
            }
            catch
            {
                // A single failed tile (including a per-tile fetch timeout) shouldn't
                // abort the whole area; it will be re-fetched on demand later.
            }
            finally
            {
                _gate.Release();
            }

            ReportTileDone();
        }

        void ReportTileDone()
        {
            // Tiles complete on pooled threads, so the running count must be advanced
            // atomically; IProgress<T>.Report is itself safe to call concurrently.
            var completed = Interlocked.Increment(ref done);
            progress?.Report(new TilePrefetchProgress(completed, total));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The HttpClient is owned by IHttpClientFactory, not this type.
        _gate.Dispose();
        _cache.Dispose();
    }

    /// <summary>
    /// A small thread-safe LRU cache of decoded tile images bounded by a tile count.
    /// Reads and writes take a short lock; an entry pushed past the capacity (or a
    /// value replaced for an existing key) is disposed so decoded bitmaps don't leak.
    /// </summary>
    private sealed class LruImageCache : IDisposable
    {
        private readonly int _capacity;
        private readonly object _sync = new();
        private readonly Dictionary<string, LinkedListNode<Entry>> _map;
        private readonly LinkedList<Entry> _lru = new();

        public LruImageCache(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _map = new Dictionary<string, LinkedListNode<Entry>>(_capacity);
        }

        public bool TryGet(string key, out IImage image)
        {
            lock (_sync)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    // Touch: move to the most-recently-used end.
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    image = node.Value.Image;
                    return true;
                }
            }

            image = null!;
            return false;
        }

        public void Set(string key, IImage image)
        {
            IImage? toDispose = null;
            lock (_sync)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    if (!ReferenceEquals(existing.Value.Image, image))
                    {
                        toDispose = existing.Value.Image;
                    }

                    existing.Value = new Entry(key, image);
                    _lru.Remove(existing);
                    _lru.AddFirst(existing);
                }
                else
                {
                    var node = new LinkedListNode<Entry>(new Entry(key, image));
                    _lru.AddFirst(node);
                    _map[key] = node;

                    if (_map.Count > _capacity)
                    {
                        var lru = _lru.Last!;
                        _lru.RemoveLast();
                        _map.Remove(lru.Value.Key);
                        toDispose = lru.Value.Image;
                    }
                }
            }

            // Dispose the evicted/replaced bitmap outside the lock.
            toDispose?.Dispose();
        }

        public void Dispose()
        {
            lock (_sync)
            {
                foreach (var entry in _lru)
                {
                    entry.Image.Dispose();
                }

                _lru.Clear();
                _map.Clear();
            }
        }

        private struct Entry(string key, IImage image)
        {
            public string Key { get; } = key;

            public IImage Image { get; } = image;
        }
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
