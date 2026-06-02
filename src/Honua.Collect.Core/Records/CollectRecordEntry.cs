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
}
