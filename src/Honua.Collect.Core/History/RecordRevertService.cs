using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.History;

/// <summary>
/// The outcome of a revert: the entry whose values were restored, the new edit that
/// records the revert (so the change log stays a complete, append-only audit), and
/// the reconstructed values that were applied.
/// </summary>
/// <param name="Entry">The reverted entry (its <see cref="CollectRecordEntry.Record"/> now holds the restored values).</param>
/// <param name="RevertEdit">
/// The edit appended to record the revert, or <see langword="null"/> when the target
/// version already matched the current values (nothing to undo).
/// </param>
/// <param name="RestoredValues">The field values as they were at the target version.</param>
public sealed record RecordRevertResult(
    CollectRecordEntry Entry,
    RecordEdit? RevertEdit,
    IReadOnlyDictionary<string, object?> RestoredValues);

/// <summary>
/// "Undo last sync'd change" / revert-to-previous-version (#38), built on the durable
/// edit history. A revert is itself a forward edit: it reconstructs an earlier
/// version by reverse-applying the durable history, writes those values onto the live
/// record, and records the restoration as a <em>new</em> pending edit. So the record
/// stays fully editable (no rewind that loses the audit trail) and the next sync ships
/// the restored values as an ordinary update.
/// </summary>
/// <remarks>
/// Type fidelity is the whole game here. The durable history persists each value
/// preserving its type (see <see cref="SqliteRecordHistoryStore"/>), so reverting a
/// numeric/boolean/date field restores the original <em>typed</em> value rather than
/// a stringified copy — a follow-up diff against the restored record therefore sees no
/// spurious change. Reverse-apply is the single shared
/// <see cref="RecordEditHistory.ReverseApplyAfter"/> implementation, so the durable
/// and in-memory paths can never drift.
/// </remarks>
public sealed class RecordRevertService
{
    private readonly IRecordStore _records;
    private readonly IRecordHistoryStore _history;
    private readonly RecordEditLog _log;

    /// <summary>Creates the revert service over the durable record and history stores.</summary>
    /// <param name="records">Where the live record/entry is persisted.</param>
    /// <param name="history">The append-only edit history the revert reconstructs from and appends to.</param>
    public RecordRevertService(IRecordStore records, IRecordHistoryStore history)
    {
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _log = new RecordEditLog(history);
    }

    /// <summary>
    /// Reverts the record to the values it held immediately after the edit at
    /// <paramref name="toSequence"/> (pass -1 for the original capture), restoring
    /// each field's original typed value. The restoration is written onto
    /// <paramref name="entry"/>, recorded as a new edit, and both the record and its
    /// history are persisted.
    /// </summary>
    /// <param name="entry">The entry to revert (mutated in place; its record keeps its id).</param>
    /// <param name="toSequence">The history sequence to revert to (inclusive); -1 for the original.</param>
    /// <param name="editorUserId">Who is performing the revert (audited on the new edit).</param>
    /// <param name="timestampUtc">Revert timestamp; defaults to now.</param>
    /// <param name="note">Optional human note; defaults to a revert marker.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The revert outcome; <see cref="RecordRevertResult.RevertEdit"/> is null when nothing changed.</returns>
    public async Task<RecordRevertResult> RevertToVersionAsync(
        CollectRecordEntry entry,
        long toSequence,
        string editorUserId,
        DateTimeOffset? timestampUtc = null,
        string? note = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(editorUserId);

        var recordId = entry.Record.RecordId;
        var edits = await _history.GetHistoryAsync(recordId, ct).ConfigureAwait(false);

        if (toSequence >= 0 && !edits.Any(e => e.Sequence == toSequence))
        {
            throw new ArgumentOutOfRangeException(
                nameof(toSequence),
                toSequence,
                $"Record '{recordId}' has no edit at sequence {toSequence} to revert to.");
        }

        return await ApplyRevertAsync(entry, edits, toSequence, editorUserId, timestampUtc, note, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Undoes the most recent recorded edit, restoring the values from immediately
    /// before it. A no-op (returns a null <see cref="RecordRevertResult.RevertEdit"/>)
    /// when the record has no history.
    /// </summary>
    /// <param name="entry">The entry to undo the last change on.</param>
    /// <param name="editorUserId">Who is performing the undo.</param>
    /// <param name="timestampUtc">Undo timestamp; defaults to now.</param>
    /// <param name="note">Optional human note.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The revert outcome.</returns>
    public async Task<RecordRevertResult> UndoLastChangeAsync(
        CollectRecordEntry entry,
        string editorUserId,
        DateTimeOffset? timestampUtc = null,
        string? note = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(editorUserId);

        var edits = await _history.GetHistoryAsync(entry.Record.RecordId, ct).ConfigureAwait(false);
        if (edits.Count == 0)
        {
            return new RecordRevertResult(entry, null, Snapshot(entry.Record.Values));
        }

        // Undo the last edit: revert to just before the highest sequence. Locate the
        // target by max sequence, never by index, so a sparse/partial window works.
        var lastSequence = edits.Max(e => e.Sequence);
        return await ApplyRevertAsync(entry, edits, lastSequence - 1, editorUserId, timestampUtc, note, ct)
            .ConfigureAwait(false);
    }

    private async Task<RecordRevertResult> ApplyRevertAsync(
        CollectRecordEntry entry,
        IReadOnlyList<RecordEdit> edits,
        long toSequence,
        string editorUserId,
        DateTimeOffset? timestampUtc,
        string? note,
        CancellationToken ct)
    {
        var record = entry.Record;
        var before = Snapshot(record.Values);

        // Reconstruct the target version with the ONE shared reverse-apply so the
        // durable revert can never diverge from the in-memory RevertTo/UndoLast.
        var restored = RecordEditHistory.ReverseApplyAfter(before, edits, toSequence);

        // Write the restored values onto the live record, preserving its id. Replace
        // the whole value set so fields added after the target version are removed.
        record.Values.Clear();
        foreach (var pair in restored)
        {
            record.Values[pair.Key] = pair.Value;
        }

        // A synced record stays editable post-sync: re-open it as a server update so
        // the restored values ship as an ordinary update (no duplicate insert).
        // MarkEditedAfterSync requires a server id; a synced record without one (never
        // confirmed remotely) is left in its current state for the normal pending path.
        if (entry is { SyncState: RecordSyncState.Synced, RemoteId: not null })
        {
            entry.MarkEditedAfterSync();
        }

        // Record the revert as a new, audited edit (diffed against the pre-revert
        // values). When nothing actually changed, no edit is appended.
        var revertEdit = await _log.RecordEditAsync(
            entry,
            before,
            editorUserId,
            timestampUtc ?? DateTimeOffset.UtcNow,
            note ?? $"Reverted to version {toSequence + 1}",
            ct).ConfigureAwait(false);

        // Persist the restored record (and its advanced version) after the history
        // append so a crash can't leave a logged revert with un-saved record values.
        await _records.SaveAsync(entry, ct).ConfigureAwait(false);

        return new RecordRevertResult(entry, revertEdit, restored);
    }

    private static Dictionary<string, object?> Snapshot(IReadOnlyDictionary<string, object?> values)
        => new(values, StringComparer.OrdinalIgnoreCase);
}
