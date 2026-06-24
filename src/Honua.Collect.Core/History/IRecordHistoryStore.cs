namespace Honua.Collect.Core.History;

/// <summary>
/// Durable, append-only store for the per-record offline edit history
/// (BACKLOG #38), persisted alongside the captured records in the same device
/// database. It is the durability layer under the in-memory
/// <see cref="RecordEditHistory"/>: the change log survives restarts so a record's
/// who/when/what evolution is never lost. The log is write-once — edits are
/// appended and queried, never updated or deleted in place — so it stays
/// tamper-evident.
/// </summary>
public interface IRecordHistoryStore
{
    /// <summary>
    /// Appends a recorded edit for a record. The edit's
    /// <see cref="RecordEdit.Sequence"/> must be exactly one greater than the
    /// highest sequence already stored for that record (the first edit is sequence
    /// 0), enforcing a monotonic, gap-free sequence.
    /// </summary>
    /// <param name="recordId">The record the edit belongs to.</param>
    /// <param name="edit">The edit to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <paramref name="edit"/>'s <see cref="RecordEdit.Sequence"/> is not
    /// the expected next number for its record (a duplicate, gap, or out-of-order
    /// append), which would break the tamper-evident sequence.
    /// </exception>
    Task AppendAsync(string recordId, RecordEdit edit, CancellationToken ct = default);

    /// <summary>
    /// Loads the full edit history for a record, ordered by ascending
    /// <see cref="RecordEdit.Sequence"/>.
    /// </summary>
    /// <param name="recordId">The record whose history to load.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The record's edits, oldest first (empty if none).</returns>
    Task<IReadOnlyList<RecordEdit>> GetHistoryAsync(string recordId, CancellationToken ct = default);

    /// <summary>
    /// Returns the next sequence number to use for a record: the count of edits
    /// already recorded for it (zero when the record has no history yet, so the
    /// first edit is sequence 0).
    /// </summary>
    /// <param name="recordId">The record to inspect.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> GetNextSequenceAsync(string recordId, CancellationToken ct = default);
}
