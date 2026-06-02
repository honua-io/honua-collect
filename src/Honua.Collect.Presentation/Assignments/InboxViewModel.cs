using Honua.Collect.Core.Assignments;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.Assignments;

/// <summary>
/// View-model for the assignment inbox (BACKLOG E5): the worker's open, overdue,
/// and completed task lists, ordered by urgency, plus the actions that move an
/// assignment through its lifecycle.
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
    }

    /// <summary>Open assignments, most urgent first.</summary>
    public IReadOnlyList<FieldAssignment> Open => _inbox.Open;

    /// <summary>Overdue assignments.</summary>
    public IReadOnlyList<FieldAssignment> Overdue => _inbox.Overdue(_asOfUtc);

    /// <summary>Completed assignments.</summary>
    public IReadOnlyList<FieldAssignment> Completed => _inbox.Completed;

    /// <summary>Count of assignments still needing action.</summary>
    public int OpenCount => _inbox.OpenCount;

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

    private void RefreshLists()
    {
        OnPropertyChanged(nameof(Open));
        OnPropertyChanged(nameof(Overdue));
        OnPropertyChanged(nameof(Completed));
        OnPropertyChanged(nameof(OpenCount));
    }
}
