using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Sync;

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
/// <see cref="CollectRecordEntry.MarkFailed"/>, so a permissions/token error is
/// surfaced verbatim instead of being collapsed to a generic "rejected" sentinel.
/// </returns>
public delegate Task<FeatureSyncResult> RecordUploader(CollectRecordEntry entry, CancellationToken cancellationToken);

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
    IReadOnlyList<CollectRecordEntry> entries,
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
public delegate Task RecordStatePersister(CollectRecordEntry entry);

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

    private readonly List<CollectRecordEntry> _entries;
    private readonly RecordUploader _uploader;
    private readonly BatchRecordUploader? _batchUploader;
    private readonly int _batchSize;
    private readonly RecordStatePersister? _persist;
    private readonly FeaturePuller? _puller;
    private readonly FormDefinition? _form;
    private readonly FeaturePullService _pullService = new();
    private SyncSummary _summary;
    private bool _isSyncing;
    private bool _isPulling;

    /// <summary>Creates the sync center over a set of records and an uploader.</summary>
    /// <param name="entries">Tracked records.</param>
    /// <param name="uploader">Uploads a single record.</param>
    public SyncCenterViewModel(IEnumerable<CollectRecordEntry> entries, RecordUploader uploader)
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
    public SyncCenterViewModel(
        IEnumerable<CollectRecordEntry> entries,
        RecordUploader uploader,
        FeaturePuller? puller,
        FormDefinition? form,
        RecordStatePersister? persist = null,
        BatchRecordUploader? batchUploader = null,
        int batchSize = DefaultBatchSize)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        _batchUploader = batchUploader;
        _batchSize = batchSize;
        _persist = persist;
        _puller = puller;
        _form = form;
        _entries = entries.ToList();
        Pending = new ObservableCollection<CollectRecordEntry>(_entries.Where(e => e.IsPendingUpload));
        Conflicts = new ObservableCollection<ConflictReviewViewModel>();
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
            }
        }
    }

    /// <summary>A formatted one-line status: "Outbox 1 · Conflicts 0 · Sent 0 · Failed 1".</summary>
    public string Header => $"Outbox {Summary.Outbox} · Conflicts {Summary.Conflicts} · Sent {Summary.Sent} · Failed {Summary.Failed}";

    /// <summary>Records currently waiting to upload or retry.</summary>
    public ObservableCollection<CollectRecordEntry> Pending { get; }

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
    /// Conflicts produced by the most recent pull, ready for the review screen to
    /// resolve. A <see cref="ConflictReviewViewModel"/> binds to each entry.
    /// </summary>
    public ObservableCollection<ConflictReviewViewModel> Conflicts { get; }

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
        try
        {
            var queryResult = await _puller(cancellationToken).ConfigureAwait(false);
            if (!queryResult.Success)
            {
                return null;
            }

            var merge = _pullService.Merge(_form, queryResult.Records, BuildLocalIndex());

            Conflicts.Clear();
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
                    Conflicts.Add(new ConflictReviewViewModel(entry));
                }
                else
                {
                    Conflicts.Add(new ConflictReviewViewModel(classification.Conflict));
                }
            }

            NewFromServer.Clear();
            foreach (var item in merge.NewRecords)
            {
                NewFromServer.Add(item);
            }

            RebuildState();
            return merge;
        }
        finally
        {
            IsPulling = false;
        }
    }

    /// <summary>
    /// Indexes the local records by the server object id they were synced as. Only
    /// records carrying a numeric <see cref="CollectRecordEntry.RemoteId"/>
    /// participate; an unsynced (local-only) record has no server counterpart, so a
    /// matching server feature is correctly treated as new-from-server.
    /// </summary>
    private CollectRecordEntry? FindEntry(string? recordId)
    {
        if (string.IsNullOrEmpty(recordId))
        {
            return null;
        }

        return _entries.FirstOrDefault(e =>
            string.Equals(e.Record.RecordId, recordId, StringComparison.Ordinal));
    }

    private Dictionary<long, FieldRecord> BuildLocalIndex()
    {
        var index = new Dictionary<long, FieldRecord>();
        foreach (var entry in _entries)
        {
            if (entry.RemoteId is { } remoteId
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
        try
        {
            var pending = _entries.Where(e => e.IsPendingUpload).ToList();

            if (_batchUploader is not null)
            {
                // New-record adds can be batched into one applyEdits round-trip; a
                // server update (PendingUpdate keyed by RemoteId) stays one-at-a-time
                // because applyEdits batching here targets the adds array only.
                var adds = pending.Where(e => !e.IsServerUpdate).ToList();
                var updates = pending.Where(e => e.IsServerUpdate).ToList();
                synced += await SyncBatchedAsync(adds, cancellationToken).ConfigureAwait(false);
                synced += await SyncPerRecordAsync(updates, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                synced += await SyncPerRecordAsync(pending, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            IsSyncing = false;
            RebuildState();
        }

        return synced;
    }

    private async Task<int> SyncPerRecordAsync(
        IReadOnlyList<CollectRecordEntry> entries,
        CancellationToken cancellationToken)
    {
        var synced = 0;
        foreach (var entry in entries)
        {
            entry.MarkUploading();
            try
            {
                var result = await _uploader(entry, cancellationToken).ConfigureAwait(false);
                if (ApplyResult(entry, result))
                {
                    synced++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                entry.MarkFailed(ex.Message);
            }

            await PersistAsync(entry).ConfigureAwait(false);
        }

        return synced;
    }

    private async Task<int> SyncBatchedAsync(
        IReadOnlyList<CollectRecordEntry> adds,
        CancellationToken cancellationToken)
    {
        var synced = 0;
        for (var start = 0; start < adds.Count; start += _batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = adds.Skip(start).Take(_batchSize).ToList();
            foreach (var entry in chunk)
            {
                entry.MarkUploading();
            }

            IReadOnlyList<FeatureSyncResult> results;
            try
            {
                results = await _batchUploader!(chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A whole-batch transport failure fails every record in the chunk;
                // they stay in the Outbox to retry on the next pass.
                foreach (var entry in chunk)
                {
                    entry.MarkFailed(ex.Message);
                    await PersistAsync(entry).ConfigureAwait(false);
                }

                continue;
            }

            for (var i = 0; i < chunk.Count; i++)
            {
                // A result list shorter than the chunk means the server did not
                // report that entry — treat the unreported tail as failed rather than
                // silently marking it synced.
                var result = i < results.Count
                    ? results[i]
                    : FeatureSyncResult.Fail("The server did not return a result for this record.");
                if (ApplyResult(chunk[i], result))
                {
                    synced++;
                }

                await PersistAsync(chunk[i]).ConfigureAwait(false);
            }
        }

        return synced;
    }

    /// <summary>
    /// Applies an upload outcome to an entry, threading the server's error message
    /// and code through to <see cref="CollectRecordEntry.MarkFailed"/> (no null
    /// sentinel). Returns whether the upload succeeded.
    /// </summary>
    private static bool ApplyResult(CollectRecordEntry entry, FeatureSyncResult result)
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

    private async Task PersistAsync(CollectRecordEntry entry)
    {
        if (_persist is null)
        {
            return;
        }

        try
        {
            await _persist(entry).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Local persistence failed; keep the computed in-memory state. The
            // server side already reflects the upload, so we must not flip a synced
            // record back to a re-uploadable state here. The next SaveAsync (e.g.
            // on the following sync pass) will re-attempt the durable write.
            _ = ex;
        }
    }

    private void RebuildState()
    {
        Summary = SyncSummary.From(_entries);
        Pending.Clear();
        foreach (var entry in _entries.Where(e => e.IsPendingUpload))
        {
            Pending.Add(entry);
        }

        (SyncCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
