namespace Honua.Collect.Core.Records;

/// <summary>
/// Transport state of a captured record on the device, tracking its journey to
/// the server. This is deliberately <em>orthogonal</em> to the SDK's
/// <see cref="Sdk.Field.Records.RecordStatus"/>: that enum models the review
/// workflow (draft → submitted → approved/rejected), while this models whether
/// the bytes have actually reached the server yet.
/// </summary>
public enum RecordSyncState
{
    /// <summary>Held only on the device; not yet queued for upload.</summary>
    Local = 0,

    /// <summary>Queued for upload, waiting for connectivity or the next sync.</summary>
    Pending = 1,

    /// <summary>Currently uploading.</summary>
    Uploading = 2,

    /// <summary>Successfully uploaded and acknowledged by the server.</summary>
    Synced = 3,

    /// <summary>The last upload attempt failed; eligible for retry.</summary>
    Failed = 4,

    /// <summary>
    /// The server's version of this record diverged from the local edits, so the
    /// upload was rejected by the <c>ManualReview</c> strategy and the record is
    /// waiting on manual conflict review (BACKLOG S1). Held out of the Outbox until
    /// the user resolves it.
    /// </summary>
    Conflicted = 5,

    /// <summary>
    /// A previously <see cref="Synced"/> record has been re-edited offline and is
    /// queued to upload again as an <em>update</em> against its existing server
    /// record (keyed by <see cref="Records.CollectRecordEntry.RemoteId"/>), rather
    /// than as a new insert. This is the "never locked out after sync" state
    /// (BACKLOG #38): the device keeps the server-id reference so the edit syncs
    /// as a server-side update instead of duplicating the record.
    /// </summary>
    PendingUpdate = 6,
}
