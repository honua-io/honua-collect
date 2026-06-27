namespace Honua.Collect.Core.Automation.Http;

/// <summary>
/// An in-memory <see cref="IHttpOutboxStore"/> — the test/default store, and the
/// reference for the SQLite one. Thread-safe via a simple lock; enqueue order is
/// preserved so a drain replays requests in the order they were queued.
/// </summary>
public sealed class InMemoryHttpOutboxStore : IHttpOutboxStore
{
    private readonly object _gate = new();
    private readonly List<HttpOutboxEntry> _entries = [];

    /// <inheritdoc />
    public Task SaveAsync(HttpOutboxEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == entry.Id);
            if (index >= 0)
            {
                _entries[index] = entry;
            }
            else
            {
                _entries.Add(entry);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HttpOutboxEntry>> LoadAllAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<HttpOutboxEntry>>(_entries.ToList());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HttpOutboxEntry>> LoadDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var due = _entries
                .Where(e => e.IsDue(now))
                .OrderBy(e => e.EnqueuedAtUtc)
                .ToList();
            return Task.FromResult<IReadOnlyList<HttpOutboxEntry>>(due);
        }
    }

    /// <inheritdoc />
    public Task<int> PurgeTerminalAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var removed = _entries.RemoveAll(e => e.Status is HttpOutboxStatus.Sent or HttpOutboxStatus.Failed);
            return Task.FromResult(removed);
        }
    }

    /// <inheritdoc />
    public Task<HttpOutboxEntry?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        lock (_gate)
        {
            var match = _entries.FirstOrDefault(e =>
                string.Equals(e.Request.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
            return Task.FromResult(match);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id == id);
        }

        return Task.CompletedTask;
    }
}
