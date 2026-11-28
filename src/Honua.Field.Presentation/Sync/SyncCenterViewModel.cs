// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Honua.Field.Core.Records;
using Honua.Field.Core.Sync;
using Honua.Field.Presentation.Mvvm;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Field.Presentation.Sync;

/// <summary>
/// Uploads a single record to the server. Implemented by the app over the
/// platform transport; injected so the sync center is testable.
/// </summary>
/// <param name="entry">The record to upload.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>
/// The <see cref="FeatureSyncResult"/> describing the outcome — success with the
/// server-assigned object id, or failure carrying the server error message and
/// code. The sync center threads this straight through to
/// <see cref="FieldRecordEntry.MarkFailed"/>, so a permissions/token error is
/// surfaced verbatim instead of being collapsed to a generic "rejected" sentinel.
/// </returns>
public delegate Task<FeatureSyncResult> RecordUploader(FieldRecordEntry entry, CancellationToken cancellationToken);

/// <summary>
/// Uploads a batch of records to the server in one round-trip (a GeoServices
/// <c>applyEdits</c> with a multi-feature <c>adds</c> array). Implemented by the
/// app; injected so the sync center is testable. When supplied, the sync center
/// uses this for new-record adds instead of one HTTP request per record.
/// </summary>
/// <param name="entries">The records to upload together, in order.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>
/// One <see cref="FeatureSyncResult"/> per input entry, aligned by index. A short
/// list is treated as a failure for the unreported tail entries.
/// </returns>
public delegate Task<IReadOnlyList<FeatureSyncResult>> BatchRecordUploader(
    IReadOnlyList<FieldRecordEntry> entries,
    CancellationToken cancellationToken);

/// <summary>
/// Durably persists an entry's transport state after the sync center mutates it.
/// Implemented by the host over the record store (typically
/// <see cref="Core.Storage.RecordBook.SaveAsync"/>); injected so the sync path is
/// testable without a store. Persisting the post-upload state is what stops an
/// already-synced record from re-uploading (and duplicating the server feature)
/// after an app restart.
/// </summary>
/// <param name="entry">The entry whose current state should be persisted.</param>
/// <returns>A task that completes when the state is durably written.</returns>
public delegate Task RecordStatePersister(FieldRecordEntry entry);

/// <summary>
/// Pulls the server's current features for the sync center. Implemented by the app
/// over the platform transport (typically wrapping
/// <see cref="GeoServicesFeatureSync.QueryAsync"/>); injected so the pull path is
/// testable without a live server.
/// </summary>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>The query result carrying the decoded server features.</returns>
public delegate Task<FeatureQueryResult> FeaturePuller(CancellationToken cancellationToken);

/// <summary>
/// View-model for the sync status center (BACKLOG S3): the Drafts/Outbox/Sent
/// counts, the list of records waiting to upload, and a command that pushes the
/// outbox to the server, driving each record through its sync lifecycle.
/// </summary>
public sealed class SyncCenterViewModel : ObservableObject
{
    /// <summary>Default number of records sent in one batch <c>applyEdits</c> round-trip.</summary>
    public const int DefaultBatchSize = 100;

    private readonly List<FieldRecordEntry> _entries;
    private readonly RecordUploader _uploader;
    private readonly BatchRecordUploader? _batchUploader;
    private readonly int _batchSize;
    private readonly RecordStatePersister? _persist;
    private readonly Func<FieldRecordEntry, Task<bool>>? _replayOrdinaryPhotos;
    private readonly Func<FieldRecordEntry, bool>? _canDispatchOrdinaryRecord;
    private readonly Func<bool>? _canDispatchOrdinaryAdd;
    private readonly FeaturePuller? _puller;
    private readonly FormDefinition? _form;
    private readonly FeaturePullService _pullService = new();
    private readonly ILogger<SyncCenterViewModel> _logger;
    private readonly Func<FieldRecordEntry, CancellationToken, Task<bool>>? _replayPublished;
    private SyncSummary _summary;
    private bool _isSyncing;
    private bool _isPulling;

    /// <summary>Creates the sync center over a set of records and an uploader.</summary>
    /// <param name="entries">Tracked records.</param>
    /// <param name="uploader">Uploads a single record.</param>
    public SyncCenterViewModel(IEnumerable<FieldRecordEntry> entries, RecordUploader uploader)
        : this(entries, uploader, null, null)
    {
    }

    /// <summary>
    /// Creates the sync center with a pull path so the outbox can both push edits
    /// and pull the server's current features, feeding real conflicts into review.
    /// </summary>
    /// <param name="entries">Tracked records.</param>
    /// <param name="uploader">Uploads a single record.</param>
    /// <param name="puller">Pulls the server's current features.</param>
    /// <param name="form">Form definition used to diff local vs server records.</param>
    /// <param name="persist">
    /// Optional sink that durably persists an entry's transport state after each
    /// upload, so a synced record is not re-uploaded (and duplicated) after a
    /// restart. When null, state changes are in-memory only.
    /// </param>
    /// <param name="batchUploader">
    /// Optional bulk uploader; when supplied, new-record adds are sent in batched
    /// <c>applyEdits</c> round-trips (chunked at <paramref name="batchSize"/>) instead
    /// of one HTTP request per record. Server updates still go one-at-a-time through
    /// <paramref name="uploader"/>. When null, every record uses <paramref name="uploader"/>.
    /// </param>
    /// <param name="batchSize">Maximum records per batch round-trip (defaults to <see cref="DefaultBatchSize"/>).</param>
    /// <param name="logger">Optional structured logger.</param>
    /// <param name="replayPublished">Optional published-form replay handler.</param>
    /// <param name="canDispatchOrdinaryAdd">Whether the device can attempt an ordinary add now.</param>
    /// <param name="replayOrdinaryPhotos">Optional replay handler for acknowledged ordinary features with pending photos.</param>
    /// <param name="canDispatchOrdinaryRecord">Optional check that the current target is valid for an ordinary record.</param>
    public SyncCenterViewModel(
        IEnumerable<FieldRecordEntry> entries,
        RecordUploader uploader,
        FeaturePuller? puller,
        FormDefinition? form,
        RecordStatePersister? persist = null,
        BatchRecordUploader? batchUploader = null,
        int batchSize = DefaultBatchSize,
        ILogger<SyncCenterViewModel>? logger = null,
        Func<FieldRecordEntry, CancellationToken, Task<bool>>? replayPublished = null,
        Func<bool>? canDispatchOrdinaryAdd = null,
        Func<FieldRecordEntry, Task<bool>>? replayOrdinaryPhotos = null,
        Func<FieldRecordEntry, bool>? canDispatchOrdinaryRecord = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        _batchUploader = batchUploader;
        _batchSize = batchSize;
        _persist = persist;
        _replayOrdinaryPhotos = replayOrdinaryPhotos;
        _canDispatchOrdinaryRecord = canDispatchOrdinaryRecord;
        _puller = puller;
        _form = form;
        _replayPublished = replayPublished;
        _canDispatchOrdinaryAdd = canDispatchOrdinaryAdd;
        _logger = logger ?? NullLogger<SyncCenterViewModel>.Instance;
        _entries = entries.ToList();
        Pending = new ObservableCollection<FieldRecordEntry>(_entries.Where(e => e.IsPendingUpload || e.HasPendingOrdinaryPhotos));
        Conflicts = new ObservableCollection<ConflictReviewViewModel>(_entries
            .Where(entry => entry.IsConflicted && entry.Conflict is not null)
            .Select(entry => new ConflictReviewViewModel(entry)));
        NewFromServer = new ObservableCollection<PullClassification>();
        _summary = SyncSummary.From(_entries);
        SyncCommand = new RelayCommand(() => _ = SyncAsync(), () => !IsSyncing && Summary.HasPendingWork);
        PullCommand = new RelayCommand(() => _ = PullAsync(), () => CanPull);
    }

    /// <summary>Aggregate counts for the status header.</summary>
    public SyncSummary Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value))
            {
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(HasRejectedSubmissions));
            }
        }
    }

    /// <summary>A formatted one-line status: "Outbox 1 · Conflicts 0 · Sent 0 · Failed 1".</summary>
    public string Header => $"Outbox {Summary.Outbox} · Conflicts {Summary.Conflicts} · Sent {Summary.Sent} · Failed {Summary.Failed}";

    /// <summary>Whether Records contains a submission that received a definitive rejection.</summary>
    public bool HasRejectedSubmissions => Summary.Rejected > 0;

    /// <summary>Records currently waiting to upload or retry.</summary>
    public ObservableCollection<FieldRecordEntry> Pending { get; }

    /// <summary>Whether a sync run is in progress.</summary>
    public bool IsSyncing
    {
        get => _isSyncing;
        private set
        {
            if (SetProperty(ref _isSyncing, value))
            {
                (SyncCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Pushes the outbox to the server.</summary>
    public ICommand SyncCommand { get; }

    /// <summary>Pulls the server's current features and builds the conflict list.</summary>
    public ICommand PullCommand { get; }

    /// <summary>Whether a pull run is in progress.</summary>
    public bool IsPulling
    {
        get => _isPulling;
        private set
        {
            if (SetProperty(ref _isPulling, value))
            {
                (PullCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether the pull path is configured and idle.</summary>
    public bool CanPull => _puller is not null && _form is not null && !IsPulling;

    /// <summary>
    /// Conflicts with captured comparison details, ready for the review screen to
    /// resolve. A <see cref="ConflictReviewViewModel"/> binds to each entry.
    /// </summary>
    public ObservableCollection<ConflictReviewViewModel> Conflicts { get; }

    /// <summary>Whether the latest pull could not save all conflict reviews to local storage.</summary>
    public bool HasUnsavedPullConflicts { get; private set; }

    /// <summary>Whether an available conflict can be reviewed offline.</summary>
    public bool HasReviewableConflicts => Conflicts.Count > 0;

    /// <summary>
    /// Server features the device had not seen before, surfaced by the most recent
    /// pull so the host can insert them into the local store.
    /// </summary>
    public ObservableCollection<PullClassification> NewFromServer { get; }

    /// <summary>
    /// Pulls the server's current features, classifies them against the local
    /// records, and exposes the resulting conflicts (as
    /// <see cref="ConflictReviewViewModel"/>s) and new-from-server records.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The structured merge result, or <see langword="null"/> if no pull path is configured or the query failed.</returns>
    public async Task<PullMergeResult?> PullAsync(CancellationToken cancellationToken = default)
    {
        if (_puller is null || _form is null || IsPulling)
        {
            return null;
        }

        IsPulling = true;
        HasUnsavedPullConflicts = false;
        _logger.LogInformation("Starting feature pull for form {FormId}.", _form.FormId);
        try
        {
            var queryResult = await _puller(cancellationToken);
            if (!queryResult.Success)
            {
                _logger.LogWarning("Feature pull failed for form {FormId}.", _form.FormId);
                return null;
            }

            var merge = _pullService.Merge(_form, queryResult.Records, IndexFeatureLayerRecords(_entries, _form.FormId));

            foreach (var classification in merge.Classifications)
            {
                if (classification.Disposition != PullDisposition.Conflict || classification.Conflict is null)
                {
                    continue;
                }

                // Mark the owning local entry conflicted so it leaves the Outbox
                // (and a retry never re-pushes over it) and shows in the Conflicts
                // box; bind the review to that entry so resolving it re-queues the
                // merged record through the normal sync path.
                var entry = FindEntry(classification.Local?.RecordId ?? classification.Conflict.RecordId);
                if (entry is not null)
                {
                    entry.MarkConflicted(classification.Conflict);
                    if (_persist is not null)
                    {
                        try
                        {
                            await _persist(entry);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            HasUnsavedPullConflicts = true;
                            _logger.LogError(ex, "Failed to save pulled conflict for record {RecordId}.", entry.Record.RecordId);
                        }
                    }
                }
            }

            Conflicts.Clear();
            foreach (var entry in _entries.Where(entry => entry.IsConflicted && entry.Conflict is not null))
            {
                Conflicts.Add(new ConflictReviewViewModel(entry));
            }

            OnPropertyChanged(nameof(HasReviewableConflicts));
            NewFromServer.Clear();
            foreach (var item in merge.NewRecords)
            {
                NewFromServer.Add(item);
            }

            RebuildState();
            _logger.LogInformation(
                "Completed feature pull for form {FormId}: new={NewCount} conflicts={ConflictCount} unchanged={UnchangedCount}.",
                _form.FormId, merge.NewRecords.Count, merge.Conflicts.Count, merge.Unchanged.Count);
            return merge;
        }
        finally
        {
            IsPulling = false;
        }
    }

    private FieldRecordEntry? FindEntry(string? recordId)
    {
        if (string.IsNullOrEmpty(recordId))
        {
            return null;
        }

        return _entries.FirstOrDefault(e =>
            string.Equals(e.Record.RecordId, recordId, StringComparison.Ordinal));
    }

    /// <summary>Finds records synced through this form's feature layer. Published submissions own a separate target.</summary>
    public static IReadOnlyDictionary<long, FieldRecord> IndexFeatureLayerRecords(IEnumerable<FieldRecordEntry> entries, string formId)
    {
        var index = new Dictionary<long, FieldRecord>();
        foreach (var entry in entries)
        {
            if (entry.PublishedSubmission is null && entry.Record.FormId == formId
                && entry.RemoteId is { } remoteId
                && long.TryParse(remoteId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                index[objectId] = entry.Record;
            }
        }

        return index;
    }

    /// <summary>Runs a sync pass over all pending records.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of records successfully synced.</returns>
    public async Task<int> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (IsSyncing)
        {
            return 0;
        }

        IsSyncing = true;
        var synced = 0;
        var pendingCount = 0;
        try
        {
            var pending = _entries.Where(e => e.IsPendingUpload).ToList();
            pendingCount = _entries.Count(e => e.IsPendingUpload || e.HasPendingOrdinaryPhotos);
            _logger.LogInformation("Starting sync pass for {PendingCount} pending record(s).", pendingCount);

            // Stay on the caller’s UI context when updating the bound outbox and command state.
            if (_batchUploader is not null)
            {
                // New-record adds can be batched into one applyEdits round-trip; a
                // server update (PendingUpdate keyed by RemoteId) stays one-at-a-time
                // because applyEdits batching here targets the adds array only.
                var adds = pending.Where(e => e.IsOrdinaryAdd).ToList();
                var updates = pending.Except(adds).ToList();
                synced += await SyncBatchedAsync(adds, cancellationToken);
                synced += await SyncPerRecordAsync(updates, cancellationToken);
            }
            else
            {
                synced += await SyncPerRecordAsync(pending, cancellationToken);
            }

            foreach (var entry in _entries.Where(e => e.HasPendingOrdinaryPhotos).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var complete = false;
                if (_replayOrdinaryPhotos is not null)
                {
                    try
                    {
                        complete = await _replayOrdinaryPhotos(entry) && !entry.HasPendingOrdinaryPhotos;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning("Photo sync for record {RecordId} failed: {ExceptionType}.",
                            entry.Record.RecordId, ex.GetType().Name);
                    }
                }

                if (pending.Contains(entry))
                {
                    synced -= complete ? 0 : 1;
                }
                else if (complete)
                {
                    synced++;
                }
            }
        }
        finally
        {
            IsSyncing = false;
            RebuildState();
            _logger.LogInformation(
                "Completed sync pass: pending={PendingCount} synced={SyncedCount} remainingFailed={FailedCount}.",
                pendingCount, synced, Summary.Failed);
        }

        return synced;
    }

    private async Task<int> SyncPerRecordAsync(
        IReadOnlyList<FieldRecordEntry> entries,
        CancellationToken cancellationToken)
    {
        var synced = 0;
        foreach (var entry in entries)
        {
            if (entry.PublishedSubmission is not null)
            {
                try
                {
                    if (_replayPublished is null)
                    {
                        throw new InvalidOperationException("Published outbox replay has not been configured.");
                    }

                    if (await _replayPublished(entry, cancellationToken))
                    {
                        synced++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Published outbox replay failed for record {RecordId} with {ExceptionType}.",
                        entry.Record.RecordId, ex.GetType().Name);
                }

                continue;
            }

            if (_canDispatchOrdinaryRecord?.Invoke(entry) == false)
            {
                continue;
            }

            var ordinaryAdd = entry.IsOrdinaryAdd;
            if (ordinaryAdd)
            {
                if (!await PrepareOrdinaryAddAsync(entry))
                {
                    continue;
                }
            }
            else
            {
                entry.MarkUploading();
            }

            try
            {
                var result = await _uploader(entry, cancellationToken);
                if (ordinaryAdd ? entry.ApplyOrdinaryAddResult(result) : ApplyResult(entry, result))
                {
                    synced++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Upload threw {ExceptionType} for record {RecordId}.",
                    ex.GetType().Name, entry.Record.RecordId);
                if (!ordinaryAdd)
                {
                    entry.MarkFailed(ex.Message);
                }
            }

            if (!entry.RequiresServerVerification)
            {
                await PersistAsync(entry);
            }
        }

        return synced;
    }

    private async Task<int> SyncBatchedAsync(
        IReadOnlyList<FieldRecordEntry> adds,
        CancellationToken cancellationToken)
    {
        var synced = 0;
        for (var start = 0; start < adds.Count; start += _batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = new List<FieldRecordEntry>();
            foreach (var entry in adds.Skip(start).Take(_batchSize))
            {
                if (_canDispatchOrdinaryRecord?.Invoke(entry) != false && await PrepareOrdinaryAddAsync(entry))
                {
                    chunk.Add(entry);
                }
            }

            if (chunk.Count == 0)
            {
                continue;
            }

            IReadOnlyList<FeatureSyncResult> results;
            try
            {
                results = await _batchUploader!(chunk, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Batch upload threw {ExceptionType} for {ChunkCount} record(s).",
                    ex.GetType().Name, chunk.Count);
                continue;
            }

            for (var i = 0; i < chunk.Count; i++)
            {
                // Missing results leave the durable verification hold in place.
                var result = i < results.Count
                    ? results[i]
                    : FeatureSyncResult.Fail("The server did not return a result for this record.");
                if (chunk[i].ApplyOrdinaryAddResult(result))
                {
                    synced++;
                }

                if (!chunk[i].RequiresServerVerification)
                {
                    await PersistAsync(chunk[i]);
                }
            }
        }

        return synced;
    }

    /// <summary>
    /// Applies an upload outcome to an entry, threading the server's error message
    /// and code through to <see cref="FieldRecordEntry.MarkFailed"/> (no null
    /// sentinel). Returns whether the upload succeeded.
    /// </summary>
    private static bool ApplyResult(FieldRecordEntry entry, FeatureSyncResult result)
    {
        if (result.Success)
        {
            // An update returns the same object id; preserve the existing RemoteId
            // when the server echoes none back.
            var remoteId = result.ObjectId?.ToString(CultureInfo.InvariantCulture) ?? entry.RemoteId;
            entry.MarkSynced(remoteId);
            return true;
        }

        entry.MarkFailed(result.FailureDescription);

        return false;
    }

    private async Task<bool> PrepareOrdinaryAddAsync(FieldRecordEntry entry)
    {
        if (!entry.IsPendingUpload || _canDispatchOrdinaryAdd?.Invoke() == false)
        {
            return false;
        }

        try
        {
            await entry.PrepareOrdinaryAddAsync(e => _persist?.Invoke(e) ?? Task.CompletedTask);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not prepare record {RecordId} for upload.", entry.Record.RecordId);
            return false;
        }
    }

    private async Task PersistAsync(FieldRecordEntry entry)
    {
        if (_persist is null)
        {
            return;
        }

        try
        {
            await _persist(entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Local persistence failed; keep the computed in-memory state. The
            // server side already reflects the upload, so we must not flip a synced
            // record back to a re-uploadable state here. Log at Warning with the
            // record id and exception type (never field values) so restart-duplicate
            // risk is diagnosable.
            _logger.LogWarning(
                ex,
                "Failed to persist post-sync state for record {RecordId} (syncState={SyncState}): {ExceptionType}.",
                entry.Record.RecordId, entry.SyncState, ex.GetType().Name);
        }
    }

    private void RebuildState()
    {
        Summary = SyncSummary.From(_entries);
        Pending.Clear();
        foreach (var entry in _entries.Where(e => e.IsPendingUpload || e.HasPendingOrdinaryPhotos))
        {
            Pending.Add(entry);
        }

        (SyncCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
