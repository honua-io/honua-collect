using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Records;

/// <summary>
/// A captured <see cref="FieldRecord"/> together with the product-owned transport
/// metadata Collect tracks on the device: its <see cref="RecordSyncState"/>,
/// retry bookkeeping, and the last sync error. This is what the Drafts/Outbox/Sent
/// list screens (BACKLOG S3/S4) bind to.
/// </summary>
/// <remarks>
/// Sync metadata is intentionally kept out of the SDK <see cref="FieldRecord"/>
/// contract, which models portable capture data and the review workflow only.
/// </remarks>
public sealed class CollectRecordEntry
{
    /// <summary>Creates an entry wrapping a record.</summary>
    /// <param name="record">The captured record.</param>
    /// <param name="syncState">Initial transport state. Defaults to <see cref="RecordSyncState.Local"/>.</param>
    public CollectRecordEntry(FieldRecord record, RecordSyncState syncState = RecordSyncState.Local)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        SyncState = syncState;
    }

    /// <summary>The captured record.</summary>
    public FieldRecord Record { get; }

    /// <summary>Current transport state.</summary>
    public RecordSyncState SyncState { get; private set; }

    /// <summary>Number of failed upload attempts since the last success.</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>UTC time of the most recent upload attempt, when any.</summary>
    public DateTimeOffset? LastAttemptUtc { get; private set; }

    /// <summary>UTC time the record was last successfully synced, when any.</summary>
    public DateTimeOffset? LastSyncedUtc { get; private set; }

    /// <summary>Message from the most recent failed attempt, when any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Server-assigned identifier returned on a successful upload, when any.</summary>
    public string? RemoteId { get; private set; }

    /// <summary>
    /// The mailbox this record currently belongs to, derived from its review
    /// status and transport state.
    /// </summary>
    public RecordBox Box => RecordBoxClassifier.Classify(Record.Status, SyncState);

    /// <summary>Whether the record is finished and waiting to upload (or retry).</summary>
    public bool IsPendingUpload => Box == RecordBox.Outbox;

    /// <summary>Whether the record is waiting on manual conflict review.</summary>
    public bool IsConflicted => SyncState == RecordSyncState.Conflicted;

    /// <summary>
    /// The unresolved record conflict surfaced by a pull, when the record is in
    /// the <see cref="RecordSyncState.Conflicted"/> state. The manual-review screen
    /// binds to this; <see langword="null"/> for every other state.
    /// </summary>
    public Sync.RecordConflict? Conflict { get; private set; }

    /// <summary>Queues a finished record for upload.</summary>
    /// <remarks>Only finished records (review status past <see cref="RecordStatus.Draft"/>) may be queued.</remarks>
    public void MarkPending()
    {
        if (Record.Status == RecordStatus.Draft)
        {
            throw new InvalidOperationException("A draft must be submitted before it can be queued for upload.");
        }

        SyncState = RecordSyncState.Pending;
        LastError = null;
    }

    /// <summary>Marks the record as currently uploading.</summary>
    /// <param name="attemptTimeUtc">Optional attempt timestamp.</param>
    public void MarkUploading(DateTimeOffset? attemptTimeUtc = null)
    {
        SyncState = RecordSyncState.Uploading;
        LastAttemptUtc = attemptTimeUtc ?? DateTimeOffset.UtcNow;
    }

    /// <summary>Records a successful upload.</summary>
    /// <param name="remoteId">Server-assigned identifier, when provided.</param>
    /// <param name="syncedAtUtc">Optional success timestamp.</param>
    public void MarkSynced(string? remoteId = null, DateTimeOffset? syncedAtUtc = null)
    {
        SyncState = RecordSyncState.Synced;
        RemoteId = remoteId;
        LastSyncedUtc = syncedAtUtc ?? DateTimeOffset.UtcNow;
        LastError = null;
        FailedAttempts = 0;
    }

    /// <summary>Records a failed upload attempt.</summary>
    /// <param name="error">Failure message to surface in the sync center.</param>
    /// <param name="attemptTimeUtc">Optional attempt timestamp.</param>
    public void MarkFailed(string error, DateTimeOffset? attemptTimeUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        SyncState = RecordSyncState.Failed;
        LastError = error;
        LastAttemptUtc = attemptTimeUtc ?? DateTimeOffset.UtcNow;
        FailedAttempts++;
    }

    /// <summary>
    /// Marks the record as conflicted because a pull found the server version had
    /// diverged from the local edits (the mobile sync engine's <c>ManualReview</c>
    /// strategy). Moves the record into the Conflicts box and attaches the
    /// field-level conflict for the review screen.
    /// </summary>
    /// <param name="conflict">The detected record conflict to resolve.</param>
    public void MarkConflicted(Sync.RecordConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        SyncState = RecordSyncState.Conflicted;
        Conflict = conflict;
    }

    /// <summary>
    /// Restores the conflicted transport state when rehydrating from storage. The
    /// field-level <see cref="Conflict"/> body is recomputed by the next pull (it is
    /// not persisted), so the record stays in the Conflicts box and out of the
    /// Outbox after a restart rather than silently re-uploading.
    /// </summary>
    internal void RestoreConflicted() => SyncState = RecordSyncState.Conflicted;

    /// <summary>
    /// Applies a manual conflict resolution: copies the merged values produced by
    /// the review screen onto the local record, clears the conflict, and re-queues
    /// the record for upload so the resolved version flows back through the normal
    /// sync path.
    /// </summary>
    /// <param name="merged">The merged record from <c>RecordConflict.Resolve</c>.</param>
    /// <remarks>
    /// The same <see cref="FieldRecord"/> instance is kept (its id is stable);
    /// only the mutable capture data is overwritten so existing references stay
    /// valid.
    /// </remarks>
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
        SyncState = RecordSyncState.Pending;
    }
}
