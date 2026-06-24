namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// A non-durable <see cref="IAuditStore"/> kept in process memory. Useful as a
/// default before a SQLite file is provisioned and for testing the trail logic
/// without I/O. Thread-safe.
/// </summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly object _gate = new();
    private readonly List<AuditEntry> _entries = [];

    /// <inheritdoc />
    public Task<long> HeadSequenceAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.Count == 0 ? -1L : _entries[^1].Sequence);
        }
    }

    /// <inheritdoc />
    public Task<string> HeadHashAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.Count == 0 ? string.Empty : _entries[^1].Hash);
        }
    }

    /// <inheritdoc />
    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery? query = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<AuditEntry> result = _entries;
            if (query is not null)
            {
                result = result.Where(query.Matches);
            }

            return Task.FromResult<IReadOnlyList<AuditEntry>>(result.OrderBy(e => e.Sequence).ToList());
        }
    }
}
