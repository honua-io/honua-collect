using System.Collections.ObjectModel;
using Honua.Collect.Core.Assignments;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.Assignments;

/// <summary>
/// View-model for the live, service-backed assignment inbox (BACKLOG E5, wired into
/// the #40 unified loop). Unlike <see cref="InboxViewModel"/> — which renders a
/// fixed set of assignments handed to it — this drives the Core
/// <see cref="AssignmentService"/>: it loads the signed-in operator's inbox from the
/// store (identity comes from the live session, not the UI), runs accept/start/
/// complete/decline through the service so the assignee-only and status-transition
/// guards apply, optionally filters by status, surfaces per-status counts, and can
/// pull/push against the sync seam. The view binds to <see cref="OpenRows"/> /
/// <see cref="CompletedRows"/> and the command-style methods.
/// </summary>
public sealed class AssignmentInboxViewModel : ObservableObject
{
    private readonly AssignmentService _service;
    private readonly Func<DateTimeOffset> _now;

    private AssignmentStatus? _statusFilter;
    private int _openCount;
    private int _overdueCount;
    private bool _isBusy;
    private string? _lastError;

    /// <summary>Creates the inbox view-model over the dispatch service.</summary>
    /// <param name="service">The Core assignment service.</param>
    /// <param name="now">Reference-time source for overdue calculation; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public AssignmentInboxViewModel(AssignmentService service, Func<DateTimeOffset>? now = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        OpenRows = [];
        CompletedRows = [];
    }

    /// <summary>Raised when the operator opens an assignment to begin capture.</summary>
    public event EventHandler<FieldAssignment>? OpenRequested;

    /// <summary>Open assignment rows (assigned/accepted/in-progress), most urgent first.</summary>
    public ObservableCollection<AssignmentRowViewModel> OpenRows { get; }

    /// <summary>Completed assignment rows.</summary>
    public ObservableCollection<AssignmentRowViewModel> CompletedRows { get; }

    /// <summary>Optional status filter applied on the next <see cref="RefreshAsync"/>.</summary>
    public AssignmentStatus? StatusFilter
    {
        get => _statusFilter;
        set => SetProperty(ref _statusFilter, value);
    }

    /// <summary>Count of assignments still needing action.</summary>
    public int OpenCount
    {
        get => _openCount;
        private set => SetProperty(ref _openCount, value);
    }

    /// <summary>Count of open assignments past their due date.</summary>
    public int OverdueCount
    {
        get => _overdueCount;
        private set => SetProperty(ref _overdueCount, value);
    }

    /// <summary>Whether a load/transition is in flight (for a busy indicator).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>The most recent error surfaced to the user (e.g. a rejected transition), or null.</summary>
    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    /// <summary>A header line like "3 open · 1 overdue".</summary>
    public string Header => $"{OpenCount} open · {OverdueCount} overdue";

    /// <summary>Loads (or reloads) the operator's inbox from the service.</summary>
    /// <param name="ct">Cancellation token.</param>
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

    /// <summary>Opens an assignment for capture, signalling the host (no transition yet).</summary>
    /// <param name="row">The row to open.</param>
    public void Open(AssignmentRowViewModel? row)
    {
        if (row is not null)
        {
            OpenRequested?.Invoke(this, row.Assignment);
        }
    }

    /// <summary>Accepts an assignment through the service, then refreshes.</summary>
    /// <param name="assignmentId">The assignment to accept.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task AcceptAsync(string assignmentId, CancellationToken ct = default)
        => RunAsync(() => _service.AcceptAsync(assignmentId, ct), ct);

    /// <summary>Declines an assignment through the service, then refreshes.</summary>
    /// <param name="assignmentId">The assignment to decline.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task DeclineAsync(string assignmentId, CancellationToken ct = default)
        => RunAsync(() => _service.DeclineAsync(assignmentId, ct), ct);

    /// <summary>Starts capture for an assignment, linking the record, then refreshes.</summary>
    /// <param name="assignmentId">The assignment to start.</param>
    /// <param name="recordId">The record being captured.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task StartAsync(string assignmentId, string recordId, CancellationToken ct = default)
        => RunAsync(() => _service.StartAsync(assignmentId, recordId, ct), ct);

    /// <summary>Completes an assignment through the service, then refreshes.</summary>
    /// <param name="assignmentId">The assignment to complete.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task CompleteAsync(string assignmentId, CancellationToken ct = default)
        => RunAsync(() => _service.CompleteAsync(assignmentId, ct), ct);

    /// <summary>Pulls assignments from the server and refreshes the inbox.</summary>
    /// <param name="ct">Cancellation token.</param>
    public Task PullAsync(CancellationToken ct = default)
        => RunAsync(() => _service.PullAsync(ct), ct);

    /// <summary>Pushes local status changes back to the dispatcher.</summary>
    /// <param name="ct">Cancellation token.</param>
    public Task PushStatusAsync(CancellationToken ct = default)
        => RunAsync(() => _service.PushStatusAsync(ct: ct), ct);

    /// <summary>
    /// Runs a service action, capturing a rejected transition / access denial into
    /// <see cref="LastError"/> (rather than crashing the UI) and refreshing on success.
    /// </summary>
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
