// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Sync;

/// <summary>Fetches one server page at the requested offset for <see cref="PagedInboxPullViewModel"/>.</summary>
/// <param name="offset">Zero-based feature offset to request.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>The decoded page and paging continuation metadata.</returns>
public delegate Task<InboxPage> PagedInboxPageFetcher(int offset, CancellationToken cancellationToken);

/// <summary>
/// View-model for the resilient paged inbox pull UI (BACKLOG #38 / #101). Wraps
/// <see cref="PagedInboxPull"/> over a durable <see cref="IInboxPullProgressStore"/>
/// so interrupted large-dataset pulls surface exact cursor progress and resume from
/// the last persisted offset without re-pulling earlier pages.
/// </summary>
public sealed class PagedInboxPullViewModel : ObservableObject
{
    private readonly PagedInboxPull _pull;
    private readonly IInboxPullProgressStore _progressStore;
    private readonly FormDefinition _form;
    private readonly Func<IReadOnlyDictionary<long, FieldRecord>> _localIndexProvider;
    private readonly PagedInboxPageFetcher _fetchPageAsync;
    private InboxPullCursor _cursor = InboxPullCursor.Start;
    private bool _isPulling;
    private bool _isFaulted;
    private string? _lastError;
    private long _newCount;
    private long _conflictCount;

    /// <summary>Creates the paged inbox pull view-model.</summary>
    /// <param name="progressStore">Durable cursor/progress store.</param>
    /// <param name="form">Form definition supplying field metadata for merge classification.</param>
    /// <param name="localIndexProvider">Supplies local records keyed by server object id.</param>
    /// <param name="fetchPageAsync">Fetches one server page at a given offset.</param>
    public PagedInboxPullViewModel(
        IInboxPullProgressStore progressStore,
        FormDefinition form,
        Func<IReadOnlyDictionary<long, FieldRecord>> localIndexProvider,
        PagedInboxPageFetcher fetchPageAsync)
    {
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _form = form ?? throw new ArgumentNullException(nameof(form));
        _localIndexProvider = localIndexProvider ?? throw new ArgumentNullException(nameof(localIndexProvider));
        _fetchPageAsync = fetchPageAsync ?? throw new ArgumentNullException(nameof(fetchPageAsync));
        _pull = new PagedInboxPull(_progressStore);

        PulledRows = [];
        Conflicts = [];
        ResumeCommand = new RelayCommand(() => _ = ResumePullAsync(), () => !IsPulling);
        PullFromStartCommand = new RelayCommand(() => _ = PullFromStartAsync(), () => !IsPulling);
    }

    /// <summary>Current durable paging cursor.</summary>
    public InboxPullCursor Cursor
    {
        get => _cursor;
        private set
        {
            if (SetProperty(ref _cursor, value))
            {
                OnPropertyChanged(nameof(PulledCount));
                OnPropertyChanged(nameof(HasResumableProgress));
                OnPropertyChanged(nameof(IsComplete));
                OnPropertyChanged(nameof(StatusSummary));
            }
        }
    }

    /// <summary>Total features pulled across all completed pages.</summary>
    public long PulledCount => Cursor.PulledCount;

    /// <summary>Whether an interrupted pull has persisted progress ready to resume.</summary>
    public bool HasResumableProgress => !Cursor.Completed && Cursor.NextOffset > 0;

    /// <summary>Whether the server signalled no more pages remain.</summary>
    public bool IsComplete => Cursor.Completed;

    /// <summary>Whether a pull run is currently active.</summary>
    public bool IsPulling
    {
        get => _isPulling;
        private set
        {
            if (SetProperty(ref _isPulling, value))
            {
                OnPropertyChanged(nameof(StatusSummary));
                (ResumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (PullFromStartCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether the most recent pull run stopped on a page fetch fault.</summary>
    public bool IsFaulted
    {
        get => _isFaulted;
        private set
        {
            if (SetProperty(ref _isFaulted, value))
            {
                OnPropertyChanged(nameof(StatusSummary));
            }
        }
    }

    /// <summary>Error message from the most recent faulted fetch, if any.</summary>
    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    /// <summary>New-from-server records discovered in the current session.</summary>
    public long NewCount
    {
        get => _newCount;
        private set
        {
            if (SetProperty(ref _newCount, value))
            {
                OnPropertyChanged(nameof(StatusSummary));
            }
        }
    }

    /// <summary>Conflicts discovered in the current session.</summary>
    public long ConflictCount
    {
        get => _conflictCount;
        private set
        {
            if (SetProperty(ref _conflictCount, value))
            {
                OnPropertyChanged(nameof(StatusSummary));
            }
        }
    }

    /// <summary>Human-readable one-line status for the paged inbox screen.</summary>
    public string StatusSummary
    {
        get
        {
            if (IsPulling)
            {
                return $"Pulling… {PulledCount} record(s) so far (offset {Cursor.NextOffset}).";
            }

            if (IsFaulted)
            {
                return $"Paused after error at offset {Cursor.NextOffset} ({PulledCount} saved) — resume to continue.";
            }

            if (IsComplete)
            {
                return $"Complete: {PulledCount} pulled · {NewCount} new · {ConflictCount} conflict(s).";
            }

            if (HasResumableProgress)
            {
                return $"Resumable from offset {Cursor.NextOffset} ({PulledCount} pulled so far).";
            }

            return "Ready to pull inbox pages.";
        }
    }

    /// <summary>Classifications merged during the active session.</summary>
    public ObservableCollection<PullClassification> PulledRows { get; }

    /// <summary>Conflicts discovered during the active session.</summary>
    public ObservableCollection<RecordConflict> Conflicts { get; }

    /// <summary>Resumes pulling from the last persisted cursor.</summary>
    public ICommand ResumeCommand { get; }

    /// <summary>Starts a fresh pull from offset 0.</summary>
    public ICommand PullFromStartCommand { get; }

    /// <summary>Loads the persisted cursor from storage so resumable state is shown immediately.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task LoadCursorAsync(CancellationToken ct = default)
    {
        Cursor = await _progressStore.LoadCursorAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Resumes pulling from the persisted cursor until complete or a page fetch faults.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The pull result, or <see langword="null"/> if a pull is already running.</returns>
    public Task<InboxPullResult?> ResumePullAsync(CancellationToken ct = default)
        => RunPullAsync(fromStart: false, ct);

    /// <summary>Clears session rows and pulls from offset 0.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The pull result, or <see langword="null"/> if a pull is already running.</returns>
    public Task<InboxPullResult?> PullFromStartAsync(CancellationToken ct = default)
        => RunPullAsync(fromStart: true, ct);

    private async Task<InboxPullResult?> RunPullAsync(bool fromStart, CancellationToken ct)
    {
        if (IsPulling)
        {
            return null;
        }

        IsPulling = true;
        IsFaulted = false;
        LastError = null;
        if (fromStart)
        {
            PulledRows.Clear();
            Conflicts.Clear();
            NewCount = 0;
            ConflictCount = 0;
        }

        try
        {
            var localIndex = _localIndexProvider();
            var result = fromStart
                ? await _pull.PullAllAsync(_form, localIndex, FetchAsync, ct, OnPageMergedAsync).ConfigureAwait(false)
                : await _pull.ResumeAsync(_form, localIndex, FetchAsync, ct, OnPageMergedAsync).ConfigureAwait(false);

            Cursor = result.Cursor;
            IsFaulted = result.Faulted;
            LastError = result.Error;
            return result;
        }
        finally
        {
            IsPulling = false;
        }
    }

    private Task<InboxPage> FetchAsync(int offset, CancellationToken ct)
        => _fetchPageAsync(offset, ct);

    private Task OnPageMergedAsync(PullMergeResult page, CancellationToken ct)
    {
        foreach (var item in page.Classifications)
        {
            PulledRows.Add(item);
        }

        foreach (var conflict in page.Conflicts)
        {
            Conflicts.Add(conflict);
        }

        NewCount += page.NewRecords.Count;
        ConflictCount += page.Conflicts.Count;
        return Task.CompletedTask;
    }
}
