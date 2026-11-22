// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Records;

public sealed record SyncSummary(int Drafts, int Outbox, int Sent, int Failed, int Conflicts, DateTimeOffset? LastSyncedUtc)
{
    public int Total => Drafts + Outbox + Conflicts + Sent;

    public bool HasPendingWork => Outbox > 0;

    public bool HasConflicts => Conflicts > 0;

    public static SyncSummary From(IEnumerable<CollectRecordEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var drafts = 0;
        var outbox = 0;
        var sent = 0;
        var failed = 0;
        var conflicts = 0;
        DateTimeOffset? lastSynced = null;

        foreach (var entry in entries)
        {
            switch (entry.Box)
            {
                case RecordBox.Drafts:
                    drafts++;
                    break;
                case RecordBox.Outbox:
                    outbox++;
                    if (entry.SyncState == RecordSyncState.Failed)
                    {
                        failed++;
                    }

                    break;
                case RecordBox.Conflicts:
                    conflicts++;
                    break;
                case RecordBox.Sent:
                    sent++;
                    break;
            }

            if (entry.LastSyncedUtc is { } synced && (lastSynced is null || synced > lastSynced))
            {
                lastSynced = synced;
            }
        }

        return new SyncSummary(drafts, outbox, sent, failed, conflicts, lastSynced);
    }
}
