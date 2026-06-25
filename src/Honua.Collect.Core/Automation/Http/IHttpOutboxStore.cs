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

    /// <summary>Finds an entry by its idempotency key, or null if none is queued.</summary>
    /// <param name="idempotencyKey">The key to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching entry, or null.</returns>
    Task<HttpOutboxEntry?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>Removes an entry (e.g. to prune a delivered request).</summary>
    /// <param name="id">The entry id to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string id, CancellationToken ct = default);
}
