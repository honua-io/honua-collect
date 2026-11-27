// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Records;

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
public sealed class FieldRecordEntry
{
    /// <summary>Creates an entry wrapping a record.</summary>
    /// <param name="record">The captured record.</param>
    /// <param name="syncState">Initial transport state. Defaults to <see cref="RecordSyncState.Local"/>.</param>
    public FieldRecordEntry(FieldRecord record, RecordSyncState syncState = RecordSyncState.Local)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        SyncState = syncState;
    }

    /// <summary>The captured record.</summary>
    public FieldRecord Record { get; }

    /// <summary>Frozen request used when replaying a versioned published form.</summary>
    public PublishedFormSubmission? PublishedSubmission { get; init; }

    /// <summary>Saved editor state for a published form draft.</summary>
    public PublishedFormDraft? PublishedDraft { get; init; }

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
    /// The edit-history version this entry is currently on. Starts at zero on
    /// capture and increments by one on every committed edit recorded through
    /// <see cref="RecordEditHistory"/>; persisted so the monotonic version sequence
    /// survives restarts. Used to detect the next version number to append and to
    /// keep the change log tamper-evident (BACKLOG #38).
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// Whether the next upload of this record must be sent as an <em>update</em>
    /// against an existing server record (keyed by <see cref="RemoteId"/>) rather
    /// than a fresh insert. True only for a re-edited, previously-synced record
    /// (<see cref="RecordSyncState.PendingUpdate"/>) that still carries its
    /// <see cref="RemoteId"/>.
    /// </summary>
    public bool IsServerUpdate => SyncState == RecordSyncState.PendingUpdate && RemoteId is not null;

    /// <summary>
    /// The mailbox this record currently belongs to, derived from its review
    /// status and transport state.
    /// </summary>
    public RecordBox Box => HasPendingOrdinaryPhotos ? RecordBox.Outbox : FeatureBox;

    private RecordBox FeatureBox => RecordBoxClassifier.Classify(Record.Status, SyncState);

    /// <summary>Whether an acknowledged ordinary feature has photos awaiting confirmation.</summary>
    public bool HasPendingOrdinaryPhotos => FeatureBox == RecordBox.Sent
        && SyncState == RecordSyncState.Synced
        && PublishedSubmission is null
        && RemoteId is not null
        && OrdinaryPhotos.Any(photo => photo.State != OrdinaryPhotoState.Synced);

    /// <summary>Whether the record is finished and waiting to upload (or retry).</summary>
    public bool IsPendingUpload => FeatureBox == RecordBox.Outbox
        && SyncState != RecordSyncState.SubmissionRejected && !RequiresServerVerification;

    public const string OrdinaryAddVerificationMessage =
        "Check the server layer for this feature before creating another record. The upload may already have been applied.";

    public bool IsOrdinaryAdd => PublishedSubmission is null && RemoteId is null && Record.Status != RecordStatus.Deleted;

    public bool RequiresServerVerification => FeatureBox == RecordBox.Outbox && IsOrdinaryAdd
        && SyncState is RecordSyncState.AddNeedsVerification or RecordSyncState.Uploading or RecordSyncState.Failed;

    public void MarkAddNeedsVerification()
    {
        MarkUploading();
        SyncState = RecordSyncState.AddNeedsVerification;
        LastError = OrdinaryAddVerificationMessage;
    }

    public async Task PrepareOrdinaryAddAsync(Func<FieldRecordEntry, Task> persist)
    {
        var previousState = SyncState;
        var previousError = LastError;
        var previousAttempt = LastAttemptUtc;
        MarkAddNeedsVerification();
        try
        {
            await persist(this);
        }
        catch
        {
            SyncState = previousState;
            LastError = previousError;
            LastAttemptUtc = previousAttempt;
            throw;
        }
    }

    internal void RestoreOrdinaryAddState(RecordSyncState state, string? error, int failedAttempts)
    {
        SyncState = state;
        LastError = error;
        FailedAttempts = failedAttempts;
    }

    public bool ApplyOrdinaryAddResult(Sync.FeatureSyncResult result)
    {
        if (result.Success && result.ObjectId is > 0)
        {
            MarkSynced(result.ObjectId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        if (!result.Success && (result.DefinitiveEditRejection || result.AddNotDispatched))
        {
            MarkFailed(result.FailureDescription);
            SyncState = RecordSyncState.AddRejected;
        }

        return false;
    }

    /// <summary>Whether the captured editor can safely open for changes.</summary>
    public bool CanOpenPublishedEditor => PublishedDraft is not null
        && (SyncState == RecordSyncState.SubmissionRejected
            || (PublishedSubmission is null && Record.Status == RecordStatus.Draft));

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

    /// <summary>
    /// Re-opens an already-<see cref="RecordSyncState.Synced"/> record for editing
    /// (BACKLOG #38: post-sync editability). The record returns to a pending state
    /// — <see cref="RecordSyncState.PendingUpdate"/> — while <em>preserving</em> its
    /// <see cref="RemoteId"/> and <see cref="LastSyncedUtc"/>, so the next upload is
    /// sent as an update against the existing server record (no duplicate insert).
    /// Editing the <see cref="Record"/> values in place keeps the same
    /// <see cref="Sdk.Field.Records.FieldRecord.RecordId"/>, so the record store
    /// upserts the one row rather than creating a second.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the record has never synced (no <see cref="RemoteId"/>); only a
    /// confirmed server record can be re-edited as an update. Records not yet sent
    /// should simply be edited and re-queued with <see cref="MarkPending"/>.
    /// </exception>
    public void MarkEditedAfterSync()
    {
        if (SyncState is not (RecordSyncState.Synced or RecordSyncState.PendingUpdate))
        {
            throw new InvalidOperationException(
                "Only a synced record can be re-edited as a server update. " +
                "Records not yet sent should be re-queued with MarkPending().");
        }

        if (RemoteId is null)
        {
            throw new InvalidOperationException(
                "A synced record without a server id cannot be re-edited as an update.");
        }

        // RemoteId and LastSyncedUtc are deliberately preserved so the upload is
        // keyed to the existing server record.
        SyncState = RecordSyncState.PendingUpdate;
        LastError = null;
    }

    /// <summary>
    /// Sets the current edit-history version. Used by the edit-history seam when an
    /// edit is committed, and by the store when rehydrating a persisted entry, so
    /// the monotonic version counter is restored exactly.
    /// </summary>
    /// <param name="version">The new (non-negative) version number.</param>
    public void SetVersion(int version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        Version = version;
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
        RestoreServerIdentity(remoteId, syncedAtUtc ?? DateTimeOffset.UtcNow);
        LastError = null;
        FailedAttempts = 0;
    }

    internal void RestoreServerIdentity(string? remoteId, DateTimeOffset? lastSyncedUtc)
    {
        RemoteId = remoteId;
        LastSyncedUtc = lastSyncedUtc;
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

    /// <summary>Parks a published submission after the server confirms a pre-edit rejection.</summary>
    public void MarkSubmissionRejected(string error)
    {
        if (PublishedSubmission is null)
        {
            throw new InvalidOperationException("Only published submissions can receive a form rejection.");
        }

        MarkFailed(error);
        SyncState = RecordSyncState.SubmissionRejected;
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

    /// <summary>Rehydrates the stored sync state without attempting another upload.</summary>
    internal void RestoreSyncState(RecordSyncState state, string? remoteId, string? error,
        int failedAttempts, DateTimeOffset? lastAttemptUtc, DateTimeOffset? lastSyncedUtc)
    {
        SyncState = state;
        RemoteId = remoteId;
        LastError = error;
        FailedAttempts = failedAttempts;
        LastAttemptUtc = lastAttemptUtc;
        LastSyncedUtc = lastSyncedUtc;
    }

    // Conflict details are recomputed by the next pull.
    internal void RestoreConflicted() => SyncState = RecordSyncState.Conflicted;

    /// <summary>Photos and their independent upload checkpoints.</summary>
    public IReadOnlyList<OrdinaryFeaturePhoto> OrdinaryPhotos { get; private set; } = [];

    internal IReadOnlyList<OrdinaryFeaturePhoto> PersistedOrdinaryPhotos { get; private set; } = [];

    /// <summary>Attaches newly captured evidence to this record.</summary>
    public void AddOrdinaryPhoto(OrdinaryFeaturePhoto photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (photo.Id == Guid.Empty || OrdinaryPhotos.Any(existing => existing.Id == photo.Id))
        {
            throw new ArgumentException("A photo must have a unique capture identifier.", nameof(photo));
        }

        if (photo.State != OrdinaryPhotoState.Pending || photo.ServerAttachmentId is not null)
        {
            throw new ArgumentException("Newly captured photos must be pending upload.", nameof(photo));
        }

        OrdinaryPhotos = Array.AsReadOnly(OrdinaryPhotos.Append(photo).ToArray());
    }

    /// <summary>Changes an attachment checkpoint while preserving its captured evidence and target.</summary>
    public void UpdateOrdinaryPhoto(OrdinaryFeaturePhoto photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var existing = OrdinaryPhotos.SingleOrDefault(item => item.Id == photo.Id);
        if (existing is null || (existing with { State = photo.State, ServerAttachmentId = photo.ServerAttachmentId }) != photo)
        {
            throw new ArgumentException("The checkpoint must match previously captured evidence.", nameof(photo));
        }

        bool validReceipt = photo.State switch
        {
            OrdinaryPhotoState.Pending or OrdinaryPhotoState.NeedsVerification => photo.ServerAttachmentId is null,
            OrdinaryPhotoState.Synced => photo.ServerAttachmentId is > 0,
            _ => false
        };
        if (!validReceipt || (existing.State == OrdinaryPhotoState.Synced && existing != photo))
        {
            throw new ArgumentException("The photo checkpoint or attachment receipt is invalid.", nameof(photo));
        }

        OrdinaryPhotos = Array.AsReadOnly(OrdinaryPhotos.Select(item => item.Id == photo.Id ? photo : item).ToArray());
    }

    internal async Task UpdateOrdinaryPhotoAsync(OrdinaryFeaturePhoto photo, Func<FieldRecordEntry, Task> persist)
    {
        var previousPhotos = OrdinaryPhotos;
        var previousPersistedPhotos = PersistedOrdinaryPhotos;
        UpdateOrdinaryPhoto(photo);
        try
        {
            await persist(this).ConfigureAwait(false);
        }
        catch
        {
            OrdinaryPhotos = previousPhotos;
            PersistedOrdinaryPhotos = previousPersistedPhotos;
            throw;
        }
    }

    internal void RestoreOrdinaryPhotos(OrdinaryFeaturePhoto[]? photos)
    {
        OrdinaryPhotos = Array.AsReadOnly(photos ?? []);
        AcceptPersistedOrdinaryPhotos(OrdinaryPhotos);
    }

    internal void AcceptPersistedOrdinaryPhotos(IReadOnlyList<OrdinaryFeaturePhoto> photos)
    {
        PersistedOrdinaryPhotos = photos;
    }

    internal static void ValidateOrdinaryPhotos(IReadOnlyList<OrdinaryFeaturePhoto> expected,
        IReadOnlyList<OrdinaryFeaturePhoto> stored)
    {
        if (!expected.SequenceEqual(stored))
        {
            throw new InvalidOperationException("Another operation changed this record's photos. Reload the record before retrying.");
        }
    }

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

        // A resolved conflict on a record that already exists on the server
        // (it carries a RemoteId) must upload as an UPDATE against that object id,
        // not a fresh add — re-adding would duplicate the server feature. Only a
        // never-synced local record (no RemoteId) re-queues as a plain Pending add.
        SyncState = RemoteId is not null ? RecordSyncState.PendingUpdate : RecordSyncState.Pending;
    }
}
