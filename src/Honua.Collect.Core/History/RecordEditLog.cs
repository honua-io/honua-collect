// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Records;

namespace Honua.Collect.Core.History;

public sealed class RecordEditLog
{
    private readonly IRecordHistoryStore _history;

    public RecordEditLog(IRecordHistoryStore history)
        => _history = history ?? throw new ArgumentNullException(nameof(history));

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
