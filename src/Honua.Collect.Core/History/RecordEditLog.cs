using Honua.Collect.Core.Records;

namespace Honua.Collect.Core.History;

/// <summary>
/// Durable per-record edit log (BACKLOG #38): the persistence-backed counterpart of
/// the in-memory <see cref="RecordEditHistory"/>. It records an edit by diffing the
/// record's values before and after (reusing
/// <see cref="RecordEditHistory.ComputeChanges"/> so the comparison can't drift),
/// allocates the next monotonic sequence from the durable
/// <see cref="IRecordHistoryStore"/>, stamps who/when and whether the edit happened
/// after the record had synced, persists the <see cref="RecordEdit"/>, and advances
/// the entry's <see cref="CollectRecordEntry.Version"/> so the in-memory counter
/// stays in step with storage.
/// </summary>
public sealed class RecordEditLog
{
    private readonly IRecordHistoryStore _history;

    /// <summary>Creates the log over a durable history store.</summary>
    /// <param name="history">The append-only history store.</param>
    public RecordEditLog(IRecordHistoryStore history)
        => _history = history ?? throw new ArgumentNullException(nameof(history));

    /// <summary>
    /// Records an edit to <paramref name="entry"/>: diffs <paramref name="valuesBefore"/>
    /// against the entry's current (post-edit) values, appends a new
    /// <see cref="RecordEdit"/>, and advances <see cref="CollectRecordEntry.Version"/>.
    /// When nothing actually changed, no edit is appended and <see langword="null"/>
    /// is returned.
    /// </summary>
    /// <remarks>
    /// Call after mutating the entry's <c>Record.Values</c>, passing a snapshot of
    /// the values as they were before the edit. Whether the edit is flagged
    /// <see cref="RecordEdit.AfterSync"/> is derived from the entry's current state:
    /// a re-opened synced record is in <see cref="RecordSyncState.PendingUpdate"/>
    /// (or still <see cref="RecordSyncState.Synced"/> at the instant of editing), so
    /// either of those marks the edit as post-sync.
    /// </remarks>
    /// <param name="entry">The edited entry (already mutated).</param>
    /// <param name="valuesBefore">Snapshot of the record's values before the edit.</param>
    /// <param name="editorUserId">Who made the edit.</param>
    /// <param name="timestampUtc">Edit timestamp; defaults to now.</param>
    /// <param name="note">Optional human note (e.g. "fixed serial number").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The appended edit, or <see langword="null"/> when nothing changed.</returns>
    public async Task<RecordEdit?> RecordEditAsync(
        CollectRecordEntry entry,
        IReadOnlyDictionary<string, object?> valuesBefore,
        string editorUserId,
        DateTimeOffset? timestampUtc = null,
        string? note = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(valuesBefore);
        ArgumentException.ThrowIfNullOrWhiteSpace(editorUserId);

        var changes = RecordEditHistory.ComputeChanges(valuesBefore, entry.Record.Values);
        if (changes.Count == 0)
        {
            return null;
        }

        var sequence = await _history.GetNextSequenceAsync(entry.Record.RecordId, ct).ConfigureAwait(false);
        var afterSync = entry.SyncState is RecordSyncState.PendingUpdate or RecordSyncState.Synced;
        var edit = new RecordEdit(
            sequence,
            timestampUtc ?? DateTimeOffset.UtcNow,
            editorUserId,
            changes,
            afterSync,
            note);

        await _history.AppendAsync(entry.Record.RecordId, edit, ct).ConfigureAwait(false);

        // Version is the 1-based count of edits; sequence is 0-based, so +1.
        entry.SetVersion((int)sequence + 1);
        return edit;
    }
}
