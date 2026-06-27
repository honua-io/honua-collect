namespace Honua.Collect.Core.Automation.Http;

/// <summary>
/// Durable storage for the HTTP outbox (BACKLOG #44). This is the same persistence
/// seam pattern the rest of Collect uses (cf. <see cref="Assignments.IAssignmentStore"/>):
/// an in-memory implementation for tests and a SQLite-backed one for the device, so
/// queued requests survive an app restart and replay on the next connectivity drain.
/// Entries are keyed by <see cref="HttpOutboxEntry.Id"/>; the
/// <see cref="HttpOutboxRequest.IdempotencyKey"/> de-duplicates logical requests on
/// enqueue so the same rule firing twice does not queue two copies.
/// </summary>
public interface IHttpOutboxStore
{
    /// <summary>
    /// Persists (inserts or updates) an entry. Implementations must upsert by
    /// <see cref="HttpOutboxEntry.Id"/> so the dispatcher can advance delivery state.
    /// </summary>
    /// <param name="entry">The entry to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(HttpOutboxEntry entry, CancellationToken ct = default);

    /// <summary>Loads every entry, in enqueue order.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All entries.</returns>
    Task<IReadOnlyList<HttpOutboxEntry>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads only the entries eligible to send at <paramref name="now"/> — those that
    /// are <see cref="HttpOutboxStatus.Pending"/> and past their next-attempt time —
    /// in enqueue order. This is the drain path: it must not rescan the whole table
    /// (terminal <see cref="HttpOutboxStatus.Sent"/>/<see cref="HttpOutboxStatus.Failed"/>
    /// rows that accumulate over the app's life), so implementations should serve it
    /// from an index on the status/next-attempt columns.
    /// </summary>
    /// <param name="now">Current UTC time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The due pending entries, in enqueue order.</returns>
    Task<IReadOnlyList<HttpOutboxEntry>> LoadDueAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Finds an entry by its idempotency key, or null if none is queued.</summary>
    /// <param name="idempotencyKey">The key to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching entry, or null.</returns>
    Task<HttpOutboxEntry?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>Removes an entry (e.g. to prune a delivered request).</summary>
    /// <param name="id">The entry id to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Deletes all terminal entries (<see cref="HttpOutboxStatus.Sent"/> and
    /// <see cref="HttpOutboxStatus.Failed"/>), which are never retried and would
    /// otherwise grow the table without bound. The host calls this periodically (e.g.
    /// after a drain) to keep the outbox small.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries purged.</returns>
    Task<int> PurgeTerminalAsync(CancellationToken ct = default);
}
