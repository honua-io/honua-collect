using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Collect.Core.Records;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.Sync;

/// <summary>
/// Uploads a single record to the server. Implemented by the app over the
/// platform transport; injected so the sync center is testable.
/// </summary>
/// <param name="entry">The record to upload.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>The server-assigned id on success, or <see langword="null"/> to signal failure.</returns>
public delegate Task<string?> RecordUploader(CollectRecordEntry entry, CancellationToken cancellationToken);

/// <summary>
/// View-model for the sync status center (BACKLOG S3): the Drafts/Outbox/Sent
/// counts, the list of records waiting to upload, and a command that pushes the
/// outbox to the server, driving each record through its sync lifecycle.
/// </summary>
public sealed class SyncCenterViewModel : ObservableObject
{
    private readonly List<CollectRecordEntry> _entries;
    private readonly RecordUploader _uploader;
    private SyncSummary _summary;
    private bool _isSyncing;

    /// <summary>Creates the sync center over a set of records and an uploader.</summary>
    /// <param name="entries">Tracked records.</param>
    /// <param name="uploader">Uploads a single record.</param>
    public SyncCenterViewModel(IEnumerable<CollectRecordEntry> entries, RecordUploader uploader)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        _entries = entries.ToList();
        Pending = new ObservableCollection<CollectRecordEntry>(_entries.Where(e => e.IsPendingUpload));
        _summary = SyncSummary.From(_entries);
        SyncCommand = new RelayCommand(() => _ = SyncAsync(), () => !IsSyncing && Summary.HasPendingWork);
    }

    /// <summary>Aggregate counts for the status header.</summary>
    public SyncSummary Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>Records currently waiting to upload or retry.</summary>
    public ObservableCollection<CollectRecordEntry> Pending { get; }

    /// <summary>Whether a sync run is in progress.</summary>
    public bool IsSyncing
    {
        get => _isSyncing;
        private set => SetProperty(ref _isSyncing, value);
    }

    /// <summary>Pushes the outbox to the server.</summary>
    public ICommand SyncCommand { get; }

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
            foreach (var entry in _entries.Where(e => e.IsPendingUpload).ToList())
            {
                entry.MarkUploading();
                try
                {
                    var remoteId = await _uploader(entry, cancellationToken).ConfigureAwait(false);
                    if (remoteId is not null)
                    {
                        entry.MarkSynced(remoteId);
                        synced++;
                    }
                    else
                    {
                        entry.MarkFailed("Upload was rejected.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    entry.MarkFailed(ex.Message);
                }
            }
        }
        finally
        {
            IsSyncing = false;
            RebuildState();
        }

        return synced;
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
