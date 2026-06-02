using System.Collections.ObjectModel;
using Honua.Collect.Core.Records;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.Records;

/// <summary>A record row for the Drafts/Outbox/Sent lists.</summary>
public sealed class RecordRowViewModel(CollectRecordEntry entry) : ObservableObject
{
    /// <summary>The underlying record entry.</summary>
    public CollectRecordEntry Entry { get; } = entry;

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
        _ => Entry.SyncState == RecordSyncState.Failed
            ? $"Failed — {Entry.LastError}"
            : "Ready to send",
    };
}

/// <summary>
/// View-model for the records screen — the Survey123-style Drafts / Outbox /
/// Sent mailboxes (BACKLOG S4). Groups the tracked records by box and exposes a
/// summary header; the list screen binds tabs to each box.
/// </summary>
public sealed class RecordBoxViewModel : ObservableObject
{
    private readonly List<CollectRecordEntry> _entries;
    private SyncSummary _summary;

    /// <summary>Creates the records view-model over the tracked entries.</summary>
    /// <param name="entries">Captured records to display.</param>
    public RecordBoxViewModel(IEnumerable<CollectRecordEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.ToList();
        Drafts = [];
        Outbox = [];
        Sent = [];
        _summary = SyncSummary.From(_entries);
        Refresh();
    }

    /// <summary>Records still being edited.</summary>
    public ObservableCollection<RecordRowViewModel> Drafts { get; }

    /// <summary>Records waiting to upload or retry.</summary>
    public ObservableCollection<RecordRowViewModel> Outbox { get; }

    /// <summary>Records uploaded to the server.</summary>
    public ObservableCollection<RecordRowViewModel> Sent { get; }

    /// <summary>Aggregate counts for the header.</summary>
    public SyncSummary Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>A header line like "Drafts 2 · Outbox 1 · Sent 3".</summary>
    public string Header => $"Drafts {Summary.Drafts} · Outbox {Summary.Outbox} · Sent {Summary.Sent}";

    /// <summary>Rebuilds the per-box lists and summary from the current entries.</summary>
    public void Refresh()
    {
        FillBox(Drafts, RecordBox.Drafts);
        FillBox(Outbox, RecordBox.Outbox);
        FillBox(Sent, RecordBox.Sent);
        Summary = SyncSummary.From(_entries);
        OnPropertyChanged(nameof(Header));
    }

    private void FillBox(ObservableCollection<RecordRowViewModel> target, RecordBox box)
    {
        target.Clear();
        foreach (var entry in _entries.Where(e => e.Box == box))
        {
            target.Add(new RecordRowViewModel(entry));
        }
    }
}
