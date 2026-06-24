using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Records;

/// <summary>
/// Derives the Drafts/Outbox/Sent <see cref="RecordBox"/> a record belongs to
/// from its review status and transport <see cref="RecordSyncState"/>. Pure and
/// deterministic so list screens and the sync center agree on placement.
/// </summary>
public static class RecordBoxClassifier
{
    /// <summary>Classifies a record into its mailbox.</summary>
    /// <param name="status">SDK review-workflow status.</param>
    /// <param name="syncState">Transport state.</param>
    /// <returns>The mailbox the record appears in.</returns>
    public static RecordBox Classify(RecordStatus status, RecordSyncState syncState)
    {
        // A confirmed server upload always lands in Sent, regardless of where the
        // review workflow has since moved.
        if (syncState == RecordSyncState.Synced)
        {
            return RecordBox.Sent;
        }

        // A diverged server version needs review before it can upload, so it sits
        // in its own box rather than the Outbox where a retry would re-push it.
        if (syncState == RecordSyncState.Conflicted)
        {
            return RecordBox.Conflicts;
        }

        // Anything still in the review-draft stage is being edited -> Drafts.
        if (status == RecordStatus.Draft)
        {
            return RecordBox.Drafts;
        }

        // Finished locally but not yet confirmed on the server -> Outbox
        // (covers Pending, Uploading, Failed, and not-yet-queued Local records).
        return RecordBox.Outbox;
    }
}
