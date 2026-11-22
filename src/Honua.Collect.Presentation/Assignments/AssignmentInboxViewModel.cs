// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using Honua.Collect.Core.Assignments;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.Assignments;

public sealed class AssignmentInboxViewModel : ObservableObject
{
    private readonly AssignmentService _service;
    private readonly Func<DateTimeOffset> _now;

    private AssignmentStatus? _statusFilter;
    private int _openCount;
    private int _overdueCount;
    private bool _isBusy;
    private string? _lastError;

    public AssignmentInboxViewModel(AssignmentService service, Func<DateTimeOffset>? now = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        OpenRows = [];
        CompletedRows = [];
    }

    public event EventHandler<FieldAssignment>? OpenRequested;

    public ObservableCollection<AssignmentRowViewModel> OpenRows { get; }

    public ObservableCollection<AssignmentRowViewModel> CompletedRows { get; }

    public AssignmentStatus? StatusFilter
    {
        get => _statusFilter;
        set => SetProperty(ref _statusFilter, value);
    }

    public int OpenCount
    {
        get => _openCount;
        private set => SetProperty(ref _openCount, value);
    }

    public int OverdueCount
    {
        get => _overdueCount;
        private set => SetProperty(ref _overdueCount, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public string Header => $"{OpenCount} open · {OverdueCount} overdue";

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        LastError = null;
        try
        {
            var inbox = await _service.GetInboxAsync(StatusFilter, ct).ConfigureAwait(false);
            var asOf = _now();

            OpenRows.Clear();
            foreach (var assignment in inbox.Open)
            {
                OpenRows.Add(new AssignmentRowViewModel(assignment, asOf));
            }

            CompletedRows.Clear();
            foreach (var assignment in inbox.Completed)
            {
                CompletedRows.Add(new AssignmentRowViewModel(assignment, asOf));
            }

            OpenCount = inbox.OpenCount;
            OverdueCount = inbox.Overdue(asOf).Count;
            OnPropertyChanged(nameof(Header));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Open(AssignmentRowViewModel? row)
    {
        if (row is not null)
        {
            OpenRequested?.Invoke(this, row.Assignment);
        }
    }

    public Task AcceptAsync(string assignmentId, CancellationToken ct = default)
        => RunAsync(() => _service.AcceptAsync(assignmentId, ct), ct);

    public Task DeclineAsync(string assignmentId, CancellationToken ct = default)
        => RunAsync(() => _service.DeclineAsync(assignmentId, ct), ct);

    public Task StartAsync(string assignmentId, string recordId, CancellationToken ct = default)
        => RunAsync(() => _service.StartAsync(assignmentId, recordId, ct), ct);

    public Task CompleteAsync(string assignmentId, CancellationToken ct = default)
        => RunAsync(() => _service.CompleteAsync(assignmentId, ct), ct);

    public Task PullAsync(CancellationToken ct = default)
        => RunAsync(() => _service.PullAsync(ct), ct);

    public Task PushStatusAsync(CancellationToken ct = default)
        => RunAsync(() => _service.PushStatusAsync(ct: ct), ct);

    private async Task RunAsync(Func<Task> action, CancellationToken ct)
    {
        IsBusy = true;
        LastError = null;
        try
        {
            await action().ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or KeyNotFoundException && ex is not OperationCanceledException)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
