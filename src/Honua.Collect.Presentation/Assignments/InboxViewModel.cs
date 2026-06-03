using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Collect.Core.Assignments;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.Assignments;

/// <summary>
/// A single assignment row for the inbox list: the underlying
/// <see cref="FieldAssignment"/> plus display-friendly title/status/due text and
/// an overdue flag the list can highlight on.
/// </summary>
public sealed class AssignmentRowViewModel : ObservableObject
{
    private readonly DateTimeOffset _asOfUtc;

    /// <summary>Creates a row over an assignment.</summary>
    /// <param name="assignment">The assignment to display.</param>
    /// <param name="asOfUtc">Reference time for overdue calculation.</param>
    public AssignmentRowViewModel(FieldAssignment assignment, DateTimeOffset asOfUtc)
    {
        Assignment = assignment ?? throw new ArgumentNullException(nameof(assignment));
        _asOfUtc = asOfUtc;
    }

    /// <summary>The underlying assignment.</summary>
    public FieldAssignment Assignment { get; }

    /// <summary>Stable assignment identifier.</summary>
    public string AssignmentId => Assignment.AssignmentId;

    /// <summary>Human-readable title.</summary>
    public string Title => Assignment.Title;

    /// <summary>Lifecycle status text, with an overdue marker.</summary>
    public string StatusText => IsOverdue ? $"{Assignment.Status} · Overdue" : Assignment.Status.ToString();

    /// <summary>Due-date text, or "No due date" when undated.</summary>
    public string DueText => Assignment.DueAtUtc is { } due
        ? $"Due {due.LocalDateTime:g}"
        : "No due date";

    /// <summary>Whether the assignment is open and past its due date.</summary>
    public bool IsOverdue => Assignment.IsOverdue(_asOfUtc);
}

/// <summary>
/// View-model for the assignment inbox (BACKLOG E5): the worker's open, overdue,
/// and completed task lists, ordered by urgency, plus the actions that move an
/// assignment through its lifecycle. The view binds to <see cref="OpenRows"/> and
/// invokes <see cref="OpenCommand"/> on tap; the host listens for
/// <see cref="OpenRequested"/> to start a capture session for the assignment.
/// </summary>
public sealed class InboxViewModel : ObservableObject
{
    private readonly AssignmentInbox _inbox;
    private readonly DateTimeOffset _asOfUtc;

    /// <summary>Creates the inbox view-model.</summary>
    /// <param name="inbox">The worker's inbox.</param>
    /// <param name="asOfUtc">Reference time for overdue calculations.</param>
    public InboxViewModel(AssignmentInbox inbox, DateTimeOffset asOfUtc)
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _asOfUtc = asOfUtc;
        OpenRows = [];
        CompletedRows = [];
        OpenCommand = new RelayCommand<AssignmentRowViewModel>(OpenAssignment, row => row is not null);
        RefreshLists();
    }

    /// <summary>
    /// Builds an inbox view-model seeded with a few demo assignments so the
    /// screen is populated on first run (no server dispatch required).
    /// </summary>
    /// <param name="userId">The worker the demo inbox belongs to.</param>
    /// <param name="formId">Form id the demo assignments capture against.</param>
    /// <param name="asOfUtc">Reference time; defaults to now.</param>
    /// <returns>An inbox view-model over the seeded assignments.</returns>
    public static InboxViewModel CreateDemo(string userId, string formId, DateTimeOffset? asOfUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);

        var now = asOfUtc ?? DateTimeOffset.UtcNow;
        var assignments = new[]
        {
            new FieldAssignment
            {
                AssignmentId = "demo-overdue",
                FormId = formId,
                AssignedToUserId = userId,
                Title = "Inspect culvert at Mile 4",
                Instructions = "Check for blockage after the storm.",
                DueAtUtc = now.AddDays(-1),
            },
            new FieldAssignment
            {
                AssignmentId = "demo-today",
                FormId = formId,
                AssignedToUserId = userId,
                Title = "Survey new utility pole",
                DueAtUtc = now.AddHours(6),
            },
            new FieldAssignment
            {
                AssignmentId = "demo-week",
                FormId = formId,
                AssignedToUserId = userId,
                Title = "Vegetation check, north trail",
                DueAtUtc = now.AddDays(5),
            },
            new FieldAssignment
            {
                AssignmentId = "demo-undated",
                FormId = formId,
                AssignedToUserId = userId,
                Title = "Photograph signage condition",
            },
        };

        return new InboxViewModel(new AssignmentInbox(userId, assignments), now);
    }

    /// <summary>Raised when the worker opens an assignment to begin capture.</summary>
    public event EventHandler<FieldAssignment>? OpenRequested;

    /// <summary>Open assignment rows, most urgent first (overdue highlighted).</summary>
    public ObservableCollection<AssignmentRowViewModel> OpenRows { get; }

    /// <summary>Completed assignment rows.</summary>
    public ObservableCollection<AssignmentRowViewModel> CompletedRows { get; }

    /// <summary>Open assignments, most urgent first.</summary>
    public IReadOnlyList<FieldAssignment> Open => _inbox.Open;

    /// <summary>Overdue assignments.</summary>
    public IReadOnlyList<FieldAssignment> Overdue => _inbox.Overdue(_asOfUtc);

    /// <summary>Completed assignments.</summary>
    public IReadOnlyList<FieldAssignment> Completed => _inbox.Completed;

    /// <summary>Count of assignments still needing action.</summary>
    public int OpenCount => _inbox.OpenCount;

    /// <summary>A header line like "3 open · 1 overdue".</summary>
    public string Header => $"{OpenCount} open · {Overdue.Count} overdue";

    /// <summary>Opens an assignment (tap target): raises <see cref="OpenRequested"/> for the host.</summary>
    public ICommand OpenCommand { get; }

    /// <summary>
    /// Opens an assignment to begin capture, raising <see cref="OpenRequested"/>
    /// so the host can start a form session. The lifecycle transition itself
    /// happens when the host calls <see cref="Start"/> with the new record id.
    /// </summary>
    /// <param name="row">The row to open.</param>
    public void OpenAssignment(AssignmentRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        OpenRequested?.Invoke(this, row.Assignment);
    }

    /// <summary>Accepts an assignment and refreshes the lists.</summary>
    /// <param name="assignment">Assignment to accept.</param>
    public void Accept(FieldAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        assignment.Accept();
        RefreshLists();
    }

    /// <summary>Declines an assignment and refreshes the lists.</summary>
    /// <param name="assignment">Assignment to decline.</param>
    public void Decline(FieldAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        assignment.Decline();
        RefreshLists();
    }

    /// <summary>Starts capture for an assignment, linking the new record.</summary>
    /// <param name="assignment">Assignment to start.</param>
    /// <param name="recordId">Identifier of the record being captured.</param>
    public void Start(FieldAssignment assignment, string recordId)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        assignment.Start(recordId);
        RefreshLists();
    }

    /// <summary>
    /// Rebuilds the open/completed lists and counts from the inbox. Call after an
    /// assignment changes state outside this view-model (for example, when capture
    /// completes on the form page).
    /// </summary>
    public void Refresh() => RefreshLists();

    private void RefreshLists()
    {
        OpenRows.Clear();
        foreach (var assignment in _inbox.Open)
        {
            OpenRows.Add(new AssignmentRowViewModel(assignment, _asOfUtc));
        }

        CompletedRows.Clear();
        foreach (var assignment in _inbox.Completed)
        {
            CompletedRows.Add(new AssignmentRowViewModel(assignment, _asOfUtc));
        }

        OnPropertyChanged(nameof(Open));
        OnPropertyChanged(nameof(Overdue));
        OnPropertyChanged(nameof(Completed));
        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(Header));
    }
}
