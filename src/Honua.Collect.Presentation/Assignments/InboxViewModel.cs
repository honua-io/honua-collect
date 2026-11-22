// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Collect.Core.Assignments;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.Assignments;

public sealed class AssignmentRowViewModel : ObservableObject
{
    private readonly DateTimeOffset _asOfUtc;

    public AssignmentRowViewModel(FieldAssignment assignment, DateTimeOffset asOfUtc)
    {
        Assignment = assignment ?? throw new ArgumentNullException(nameof(assignment));
        _asOfUtc = asOfUtc;
    }

    public FieldAssignment Assignment { get; }

    public string AssignmentId => Assignment.AssignmentId;

    public string Title => Assignment.Title;

    public string StatusText => IsOverdue ? $"{Assignment.Status} · Overdue" : Assignment.Status.ToString();

    public string DueText => Assignment.DueAtUtc is { } due
        ? $"Due {due.LocalDateTime:g}"
        : "No due date";

    public bool IsOverdue => Assignment.IsOverdue(_asOfUtc);
}

public sealed class InboxViewModel : ObservableObject
{
    private readonly AssignmentInbox _inbox;
    private readonly DateTimeOffset _asOfUtc;

    public InboxViewModel(AssignmentInbox inbox, DateTimeOffset asOfUtc)
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _asOfUtc = asOfUtc;
        OpenRows = [];
        CompletedRows = [];
        OpenCommand = new RelayCommand<AssignmentRowViewModel>(OpenAssignment, row => row is not null);
        RefreshLists();
    }

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

    public event EventHandler<FieldAssignment>? OpenRequested;

    public ObservableCollection<AssignmentRowViewModel> OpenRows { get; }

    public ObservableCollection<AssignmentRowViewModel> CompletedRows { get; }

    public IReadOnlyList<FieldAssignment> Open => _inbox.Open;

    public IReadOnlyList<FieldAssignment> Overdue => _inbox.Overdue(_asOfUtc);

    public IReadOnlyList<FieldAssignment> Completed => _inbox.Completed;

    public int OpenCount => _inbox.OpenCount;

    public string Header => $"{OpenCount} open · {Overdue.Count} overdue";

    public ICommand OpenCommand { get; }

    public void OpenAssignment(AssignmentRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        OpenRequested?.Invoke(this, row.Assignment);
    }

    public void Accept(FieldAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        assignment.Accept();
        RefreshLists();
    }

    public void Decline(FieldAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        assignment.Decline();
        RefreshLists();
    }

    public void Start(FieldAssignment assignment, string recordId)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        assignment.Start(recordId);
        RefreshLists();
    }

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
