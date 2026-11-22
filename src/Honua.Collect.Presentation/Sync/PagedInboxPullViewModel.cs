// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Sync;

public delegate Task<InboxPage> PagedInboxPageFetcher(int offset, CancellationToken cancellationToken);

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

        LatestPageRows = [];
        Conflicts = [];
        ResumeCommand = new RelayCommand(() => _ = RunCommandAsync(() => ResumePullAsync()), () => !IsPulling);
        PullFromStartCommand = new RelayCommand(() => _ = RunCommandAsync(() => PullFromStartAsync()), () => !IsPulling);
    }

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

    public long PulledCount => Cursor.PulledCount;

    public bool HasResumableProgress => !Cursor.Completed && Cursor.NextOffset > 0;

    public bool IsComplete => Cursor.Completed;

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

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

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

    public ObservableCollection<PullClassification> LatestPageRows { get; }

    public ObservableCollection<RecordConflict> Conflicts { get; }

    public ICommand ResumeCommand { get; }

    public ICommand PullFromStartCommand { get; }

    public async Task LoadCursorAsync(CancellationToken ct = default)
    {
        Cursor = await _progressStore.LoadCursorAsync(ct);
    }

    public Task<InboxPullResult?> ResumePullAsync(CancellationToken ct = default)
        => RunPullAsync(fromStart: false, ct);

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
            LatestPageRows.Clear();
            Conflicts.Clear();
            NewCount = 0;
            ConflictCount = 0;
        }

        try
        {
            var localIndex = _localIndexProvider();
            var result = fromStart
                ? await _pull.PullAllAsync(_form, localIndex, FetchAsync, ct, OnPageMergedAsync)
                : await _pull.ResumeAsync(_form, localIndex, FetchAsync, ct, OnPageMergedAsync);

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
        LatestPageRows.Clear();
        foreach (var item in page.Classifications)
        {
            LatestPageRows.Add(item);
        }

        foreach (var conflict in page.Conflicts)
        {
            Conflicts.Add(conflict);
        }

        NewCount += page.NewRecords.Count;
        ConflictCount += page.Conflicts.Count;
        return Task.CompletedTask;
    }

    private async Task RunCommandAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsFaulted = true;
            LastError = ex.Message;
        }
    }
}
