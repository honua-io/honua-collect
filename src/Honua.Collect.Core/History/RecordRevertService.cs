// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.History;

public sealed record RecordRevertResult(
    CollectRecordEntry Entry,
    RecordEdit? RevertEdit,
    IReadOnlyDictionary<string, object?> RestoredValues);

public sealed class RecordRevertService
{
    private readonly IRecordStore _records;
    private readonly IRecordHistoryStore _history;
    private readonly RecordEditLog _log;

    public RecordRevertService(IRecordStore records, IRecordHistoryStore history)
    {
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _log = new RecordEditLog(history);
    }

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
