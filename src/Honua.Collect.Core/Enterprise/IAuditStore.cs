namespace Honua.Collect.Core.Enterprise;

/// <summary>A filter over the audit trail for the query/export API (BACKLOG E3).</summary>
/// <param name="UserId">Restrict to one user, or null for all.</param>
/// <param name="Action">Restrict to one action, or null for all.</param>
/// <param name="SinceUtc">Inclusive lower bound on timestamp, or null.</param>
/// <param name="UntilUtc">Exclusive upper bound on timestamp, or null.</param>
public sealed record AuditQuery(
    string? UserId = null,
    AuditAction? Action = null,
    DateTimeOffset? SinceUtc = null,
    DateTimeOffset? UntilUtc = null)
{
    /// <summary>Whether an entry matches this filter.</summary>
    /// <param name="entry">Entry to test.</param>
    /// <returns><see langword="true"/> when it matches.</returns>
    public bool Matches(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var e = entry.Event;
        if (UserId is not null && !string.Equals(e.UserId, UserId, StringComparison.Ordinal))
        {
            return false;
        }

        if (Action is { } action && e.Action != action)
        {
            return false;
        }

        if (SinceUtc is { } since && e.TimestampUtc < since)
        {
            return false;
        }

        if (UntilUtc is { } until && e.TimestampUtc >= until)
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// A durable, append-only store for the audit trail (BACKLOG E3). Sequencing and
/// hash-chaining are the store's responsibility so the monotonic order survives a
/// restart. The seam mirrors <see cref="Storage.IRecordStore"/> so the same SQLite
/// database file can back both.
/// </summary>
public interface IAuditStore
{
    /// <summary>The highest sequence persisted, or -1 when empty.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The head sequence.</returns>
    Task<long> HeadSequenceAsync(CancellationToken ct = default);

    /// <summary>The hash of the most recent entry, or empty when the store is empty.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The head hash.</returns>
    Task<string> HeadHashAsync(CancellationToken ct = default);

    /// <summary>Persists an already-sequenced, already-chained entry.</summary>
    /// <param name="entry">Entry to append.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AppendAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>Reads matching entries in ascending sequence order.</summary>
    /// <param name="query">Filter, or null for the whole trail.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matching entries, oldest first.</returns>
    Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery? query = null, CancellationToken ct = default);
}
