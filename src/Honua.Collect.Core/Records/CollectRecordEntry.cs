// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Records;

public sealed class CollectRecordEntry
{
    public CollectRecordEntry(FieldRecord record, RecordSyncState syncState = RecordSyncState.Local)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        SyncState = syncState;
    }

    public FieldRecord Record { get; }

    public RecordSyncState SyncState { get; private set; }

    public int FailedAttempts { get; private set; }

    public DateTimeOffset? LastAttemptUtc { get; private set; }

    public DateTimeOffset? LastSyncedUtc { get; private set; }

    public string? LastError { get; private set; }

    public string? RemoteId { get; private set; }

    public int Version { get; private set; }

    public bool IsServerUpdate => SyncState == RecordSyncState.PendingUpdate && RemoteId is not null;

    public RecordBox Box => RecordBoxClassifier.Classify(Record.Status, SyncState);

    public bool IsPendingUpload => Box == RecordBox.Outbox;

    public bool IsConflicted => SyncState == RecordSyncState.Conflicted;

    public Sync.RecordConflict? Conflict { get; private set; }

    public void ApplyResolution(FieldRecord merged)
    {
        ArgumentNullException.ThrowIfNull(merged);
        if (!IsConflicted)
        {
            throw new InvalidOperationException("Only a conflicted record can have a resolution applied.");
        }

        Record.Values.Clear();
        foreach (var pair in merged.Values)
        {
            Record.Values[pair.Key] = pair.Value;
        }

        Record.Location = merged.Location;
        Record.Status = merged.Status;
        Record.AssignedUserId = merged.AssignedUserId;

        Conflict = null;
        LastError = null;

        // A resolved conflict on a record that already exists on the server
        // (it carries a RemoteId) must upload as an UPDATE against that object id,
        // not a fresh add — re-adding would duplicate the server feature. Only a
        // never-synced local record (no RemoteId) re-queues as a plain Pending add.
        SyncState = RemoteId is not null ? RecordSyncState.PendingUpdate : RecordSyncState.Pending;
    }
}
