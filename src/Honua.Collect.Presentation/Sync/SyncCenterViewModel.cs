// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Collect.Presentation.Sync;

public delegate Task<FeatureSyncResult> RecordUploader(CollectRecordEntry entry, CancellationToken cancellationToken);

public delegate Task<IReadOnlyList<FeatureSyncResult>> BatchRecordUploader(
    IReadOnlyList<CollectRecordEntry> entries,
    CancellationToken cancellationToken);

public delegate Task RecordStatePersister(CollectRecordEntry entry);

public delegate Task<FeatureQueryResult> FeaturePuller(CancellationToken cancellationToken);

public sealed class SyncCenterViewModel : ObservableObject
{
    public const int DefaultBatchSize = 100;

    private readonly List<CollectRecordEntry> _entries;
    private readonly RecordUploader _uploader;
    private readonly BatchRecordUploader? _batchUploader;
    private readonly int _batchSize;
    private readonly RecordStatePersister? _persist;
    private readonly FeaturePuller? _puller;
    private readonly FormDefinition? _form;
    private readonly FeaturePullService _pullService = new();
    private readonly ILogger<SyncCenterViewModel> _logger;
    private SyncSummary _summary;
    private bool _isSyncing;
    private bool _isPulling;

    public SyncCenterViewModel(IEnumerable<CollectRecordEntry> entries, RecordUploader uploader)
        : this(entries, uploader, null, null)
    {
    }

    public SyncCenterViewModel(
        IEnumerable<CollectRecordEntry> entries,
        RecordUploader uploader,
        FeaturePuller? puller,
        FormDefinition? form,
        RecordStatePersister? persist = null,
        BatchRecordUploader? batchUploader = null,
        int batchSize = DefaultBatchSize,
        ILogger<SyncCenterViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        _batchUploader = batchUploader;
        _batchSize = batchSize;
        _persist = persist;
        _puller = puller;
        _form = form;
        _logger = logger ?? NullLogger<SyncCenterViewModel>.Instance;
        _entries = entries.ToList();
        Pending = new ObservableCollection<CollectRecordEntry>(_entries.Where(e => e.IsPendingUpload));
        Conflicts = new ObservableCollection<ConflictReviewViewModel>();
        NewFromServer = new ObservableCollection<PullClassification>();
        _summary = SyncSummary.From(_entries);
        SyncCommand = new RelayCommand(() => _ = SyncAsync(), () => !IsSyncing && Summary.HasPendingWork);
        PullCommand = new RelayCommand(() => _ = PullAsync(), () => CanPull);
    }

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

    public string Header => $"Outbox {Summary.Outbox} · Conflicts {Summary.Conflicts} · Sent {Summary.Sent} · Failed {Summary.Failed}";

    public ObservableCollection<CollectRecordEntry> Pending { get; }

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

    public ICommand SyncCommand { get; }

    public ICommand PullCommand { get; }

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

    public bool CanPull => _puller is not null && _form is not null && !IsPulling;

    public ObservableCollection<ConflictReviewViewModel> Conflicts { get; }

    public ObservableCollection<PullClassification> NewFromServer { get; }

    public async Task<PullMergeResult?> PullAsync(CancellationToken cancellationToken = default)
    {
        if (_puller is null || _form is null || IsPulling)
        {
            return null;
        }

        IsPulling = true;
        _logger.LogInformation("Starting feature pull for form {FormId}.", _form.FormId);
        try
        {
            var queryResult = await _puller(cancellationToken).ConfigureAwait(false);
            if (!queryResult.Success)
            {
                _logger.LogWarning("Feature pull failed for form {FormId}.", _form.FormId);
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
            pendingCount = pending.Count;
            _logger.LogInformation("Starting sync pass for {PendingCount} pending record(s).", pendingCount);

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
            _logger.LogInformation(
                "Completed sync pass: pending={PendingCount} synced={SyncedCount} remainingFailed={FailedCount}.",
                pendingCount, synced, Summary.Failed);
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
                _logger.LogWarning(
                    ex,
                    "Upload threw {ExceptionType} for record {RecordId}.",
                    ex.GetType().Name, entry.Record.RecordId);
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
                _logger.LogWarning(
                    ex,
                    "Batch upload threw {ExceptionType} for {ChunkCount} record(s).",
                    ex.GetType().Name, chunk.Count);
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
        foreach (var entry in _entries.Where(e => e.IsPendingUpload))
        {
            Pending.Add(entry);
        }

        (SyncCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
