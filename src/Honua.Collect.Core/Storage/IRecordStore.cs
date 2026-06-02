using Honua.Collect.Core.Records;

namespace Honua.Collect.Core.Storage;

/// <summary>
/// Durable, device-local store for captured <see cref="CollectRecordEntry"/> items
/// so drafts, the outbox, and sync metadata survive app restarts. Backed by an
/// on-device database; the in-memory capture list rehydrates from this on startup
/// and writes through on every capture or sync-state change.
/// </summary>
public interface IRecordStore
{
    /// <summary>
    /// Inserts the entry, or updates the existing row with the same
    /// <see cref="Sdk.Field.Records.FieldRecord.RecordId"/>.
    /// </summary>
    /// <param name="entry">The entry to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(CollectRecordEntry entry, CancellationToken ct = default);

    /// <summary>Loads every stored entry, reconstructing its record and sync state.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All persisted entries.</returns>
    Task<IReadOnlyList<CollectRecordEntry>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Removes the entry with the given record id, if present.</summary>
    /// <param name="recordId">The record id to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string recordId, CancellationToken ct = default);
}
