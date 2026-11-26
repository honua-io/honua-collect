// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Field.Core.Records;

/// <summary>
/// Aggregate counts across a set of records for the sync status center
/// (BACKLOG S3): how many are still drafts, how many are waiting to upload, how
/// many have failed, and how many have been sent.
/// </summary>
/// <param name="Drafts">Records still being edited.</param>
/// <param name="Outbox">Finished records waiting to upload, retry, or be corrected.</param>
/// <param name="Sent">Records confirmed on the server.</param>
/// <param name="Failed">Subset of <paramref name="Outbox"/> whose last attempt failed.</param>
/// <param name="Conflicts">Records held for manual conflict review (BACKLOG S1).</param>
/// <param name="LastSyncedUtc">Most recent successful sync time across the set, when any.</param>
public sealed record SyncSummary(int Drafts, int Outbox, int Sent, int Failed, int Conflicts, DateTimeOffset? LastSyncedUtc)
{
    /// <summary>Total records counted.</summary>
    public int Total => Drafts + Outbox + Conflicts + Sent;

    /// <summary>Whether anything is waiting to upload or retry.</summary>
    public bool HasPendingWork => Outbox > Rejected;

    /// <summary>Published submissions parked after a definitive rejection.</summary>
    public int Rejected { get; init; }

    /// <summary>Whether any records are waiting on manual conflict review.</summary>
    public bool HasConflicts => Conflicts > 0;

    /// <summary>Builds a summary from a set of record entries.</summary>
    /// <param name="entries">Records to summarise.</param>
    /// <returns>The aggregate summary.</returns>
    public static SyncSummary From(IEnumerable<FieldRecordEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var drafts = 0;
        var outbox = 0;
        var sent = 0;
        var failed = 0;
        var rejected = 0;
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
                    if (entry.SyncState is RecordSyncState.Failed or RecordSyncState.SubmissionRejected)
                    {
                        failed++;
                    }

                    if (entry.SyncState == RecordSyncState.SubmissionRejected)
                    {
                        rejected++;
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

        return new SyncSummary(drafts, outbox, sent, failed, conflicts, lastSynced) { Rejected = rejected };
    }
}
