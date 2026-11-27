// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using Honua.Field.Core.Records;
using Honua.Field.Presentation.Mvvm;

namespace Honua.Field.Presentation.Records;

/// <summary>A record row for the Drafts/Outbox/Sent lists.</summary>
public sealed class RecordRowViewModel(FieldRecordEntry entry) : ObservableObject
{
    /// <summary>The underlying record entry.</summary>
    public FieldRecordEntry Entry { get; } = entry;

    /// <summary>Record identifier.</summary>
    public string RecordId => Entry.Record.RecordId;

    /// <summary>Which mailbox the record is in.</summary>
    public RecordBox Box => Entry.Box;

    /// <summary>Transport state for the status line.</summary>
    public RecordSyncState SyncState => Entry.SyncState;

    /// <summary>A short status summary for the list.</summary>
    public string StatusText => Entry.Box switch
    {
        RecordBox.Drafts => "Draft",
        RecordBox.Sent => $"Sent — {Entry.RemoteId ?? "synced"}",
        RecordBox.Conflicts => "Conflict — needs review",
        _ when Entry.RequiresServerVerification => FieldRecordEntry.OrdinaryAddVerificationMessage,
        _ when Entry.SyncState == RecordSyncState.SubmissionRejected => Entry.CanOpenPublishedEditor
            ? $"Rejected. Open to correct. {Entry.LastError}"
            : $"Rejected. {Entry.LastError}",
        _ => Entry.SyncState == RecordSyncState.Failed
            ? $"Failed — {Entry.LastError}"
            : "Ready to send",
    };
}

/// <summary>A selectable record or an empty-state message in a mailbox.</summary>
public sealed record RecordBoxItem(RecordRowViewModel? Record, string? EmptyText)
{
    /// <summary>Whether this row represents a record.</summary>
    public bool HasRecord => Record is not null;

    /// <summary>Whether this row displays the empty state.</summary>
    public bool IsEmpty => Record is null;
}

/// <summary>A mailbox and its ordered rows. Empty mailboxes retain a message row.</summary>
public sealed class RecordBoxGroup(string title, IEnumerable<RecordBoxItem> items) : ReadOnlyCollection<RecordBoxItem>(items.ToList())
{
    /// <summary>The mailbox name displayed above its rows.</summary>
    public string Title { get; } = title;
}

/// <summary>
/// View-model for the records screen: Drafts, Outbox, and Sent mailboxes (BACKLOG S4).
/// Groups the tracked records by box and exposes a
/// summary header.
/// </summary>
public sealed class RecordBoxViewModel : ObservableObject
{
    private readonly List<FieldRecordEntry> _entries;
    private SyncSummary _summary;

    /// <summary>Creates the records view-model over the tracked entries.</summary>
    /// <param name="entries">Captured records to display.</param>
    public RecordBoxViewModel(IEnumerable<FieldRecordEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.ToList();
        Drafts = [];
        Outbox = [];
        Conflicts = [];
        Sent = [];
        _summary = SyncSummary.From(_entries);
        Refresh();
    }

    /// <summary>Records still being edited.</summary>
    public ObservableCollection<RecordRowViewModel> Drafts { get; }

    /// <summary>Records waiting to upload or retry.</summary>
    public ObservableCollection<RecordRowViewModel> Outbox { get; }

    /// <summary>Records held for manual conflict review (BACKLOG S1).</summary>
    public ObservableCollection<RecordRowViewModel> Conflicts { get; }

    /// <summary>Records uploaded to the server.</summary>
    public ObservableCollection<RecordRowViewModel> Sent { get; }

    /// <summary>The three mailboxes displayed in the records list.</summary>
    public IReadOnlyList<RecordBoxGroup> VisibleGroups { get; private set; } = [];

    /// <summary>Aggregate counts for the header.</summary>
    public SyncSummary Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>A header line like "Drafts 2 · Outbox 1 · Conflicts 0 · Sent 3".</summary>
    public string Header => $"Drafts {Summary.Drafts} · Outbox {Summary.Outbox} · Conflicts {Summary.Conflicts} · Sent {Summary.Sent}";

    /// <summary>Rebuilds the per-box lists and summary from the current entries.</summary>
    public void Refresh()
    {
        FillBox(Drafts, RecordBox.Drafts);
        FillBox(Outbox, RecordBox.Outbox);
        FillBox(Conflicts, RecordBox.Conflicts);
        FillBox(Sent, RecordBox.Sent);
        VisibleGroups =
        [
            CreateGroup("Drafts", Drafts, "No drafts."),
            CreateGroup("Outbox", Outbox, "Outbox is empty."),
            CreateGroup("Sent", Sent, "Nothing sent yet."),
        ];
        Summary = SyncSummary.From(_entries);
        OnPropertyChanged(nameof(VisibleGroups));
        OnPropertyChanged(nameof(Header));
    }

    private static RecordBoxGroup CreateGroup(string title, IReadOnlyCollection<RecordRowViewModel> rows, string emptyText) =>
        new(title, rows.Count == 0
            ? [new RecordBoxItem(null, emptyText)]
            : rows.Select(row => new RecordBoxItem(row, null)));

    private void FillBox(ObservableCollection<RecordRowViewModel> target, RecordBox box)
    {
        target.Clear();
        foreach (var entry in _entries.Where(e => e.Box == box))
        {
            target.Add(new RecordRowViewModel(entry));
        }
    }
}
