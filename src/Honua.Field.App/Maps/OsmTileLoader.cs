// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Honua.Field.Core.Maps;
using Microsoft.Maui.Graphics.Platform;
using IImage = Microsoft.Maui.Graphics.IImage;

namespace Honua.Field.App.Maps;

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
    private readonly HttpClient _offlineHttp;
    private readonly TileCache _disk;
    private readonly LruImageCache _cache = new(MaxCachedTiles);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly SemaphoreSlim _gate = new(6); // be polite to the tile server
    private volatile bool _disposed;

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
    /// <param name="offlineHttp">The download client, configured to reject redirects.</param>
    public OsmTileLoader(string cacheRoot, HttpClient http, HttpClient offlineHttp)
    {
        _disk = new TileCache(OsmTileUrl.CacheDirectory(cacheRoot, OsmTileUrl.Template));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _offlineHttp = offlineHttp ?? throw new ArgumentNullException(nameof(offlineHttp));
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

        if (!_disposed && _inFlight.TryAdd(key, 0))
        {
            // Draw calls Get on the UI thread. Disk access and bitmap decoding
            // must run off that thread even when the tile is already on disk.
            _ = Task.Run(() => LoadAsync(key, zoom, x, y));
        }

        return null;
    }

    private async Task LoadAsync(string key, int zoom, int x, int y)
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            // A cached tile can load while all the network slots are busy.
            if (_disk.TryGetPath(zoom, x, y, out var path) && TryDecodeInto(key, path))
            {
                TileLoaded?.Invoke(this, EventArgs.Empty);
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                var bytes = await DownloadAsync(_http, zoom, x, y).ConfigureAwait(false);
                if (bytes is null)
                {
                    return;
                }

                using var ms = new MemoryStream(bytes, writable: false);
                var img = PlatformImage.FromStream(ms);
                if (img is null)
                {
                    return;
                }

                if (img.Width <= 0 || img.Height <= 0)
                {
                    img.Dispose();
                    return;
                }

                if (_cache.TrySet(key, img))
                {
                    TileLoaded?.Invoke(this, EventArgs.Empty);
                    await _disk.SaveAsync(zoom, x, y, bytes).ConfigureAwait(false);
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
            return img is not null && _cache.TrySet(key, img);
        }
        catch
        {
            // A corrupt/truncated cached tile falls through to a network fetch.
        }

        return false;
    }

    private static async Task<byte[]?> DownloadAsync(HttpClient http, int zoom, int x, int y, CancellationToken cancellationToken = default)
    {
        var url = OsmTileUrl.For(zoom, x, y);

        // Bound each fetch independently of the HttpClient default so a stalled tile
        // releases the concurrency gate promptly instead of holding it for ~100s and
        // starving the rest of the map. Linked to the caller's token so a real cancel
        // (e.g. an aborted prefetch) still propagates.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);

        using var response = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>Verifies cached tiles and fetches missing tiles from an offline-capable source.</summary>
    public Task<OfflineAreaDownload> PrefetchAreaAsync(
        OfflineAreaPlan plan,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default) =>
        OfflineAreaDownload.RunAsync(plan, EnsureCachedAsync, progress, cancellationToken);

    public static bool SupportsOfflineDownloads =>
        OsmTileUrl.AllowsOfflinePrefetch(OsmTileUrl.Template, OsmTileUrl.AllowOfflinePrefetch);

    private async ValueTask<bool> EnsureCachedAsync(TileCoordinate tile, CancellationToken cancellationToken)
    {
        try
        {
            if (_disk.TryGetPath(tile.Zoom, tile.X, tile.Y, out var path))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    using var image = PlatformImage.FromStream(stream);
                    if (image is { Width: > 0, Height: > 0 })
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // Invalid or unreadable cached bytes must be replaced.
                }
            }

            if (!SupportsOfflineDownloads)
            {
                return false;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var bytes = await DownloadAsync(_offlineHttp, tile.Zoom, tile.X, tile.Y, cancellationToken).ConfigureAwait(false);
                if (bytes is null)
                {
                    return false;
                }

                using var stream = new MemoryStream(bytes, writable: false);
                using var image = PlatformImage.FromStream(stream);
                if (image is not { Width: > 0, Height: > 0 })
                {
                    return false;
                }

                await _disk.SaveAsync(tile.Zoom, tile.X, tile.Y, bytes, cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A failed tile remains missing. Other tiles can still be cached.
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The HttpClient is owned by IHttpClientFactory, not this type.
        _disposed = true;
        // Pending workers can still release the gate. The semaphore uses no wait handle.
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
        private bool _disposed;

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

        public bool TrySet(string key, IImage image)
        {
            IImage? toDispose = null;
            lock (_sync)
            {
                if (_disposed)
                {
                    image.Dispose();
                    return false;
                }

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
            return true;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
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
