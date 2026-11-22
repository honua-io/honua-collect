// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using Honua.Collect.Core.Records;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.Records;

public sealed class RecordRowViewModel(CollectRecordEntry entry) : ObservableObject
{
    public CollectRecordEntry Entry { get; } = entry;

    public string RecordId => Entry.Record.RecordId;

    public RecordBox Box => Entry.Box;

    public RecordSyncState SyncState => Entry.SyncState;

    public string StatusText => Entry.Box switch
    {
        RecordBox.Drafts => "Draft",
        RecordBox.Sent => $"Sent — {Entry.RemoteId ?? "synced"}",
        RecordBox.Conflicts => "Conflict — needs review",
        _ => Entry.SyncState == RecordSyncState.Failed
            ? $"Failed — {Entry.LastError}"
            : "Ready to send",
    };
}

public sealed class RecordBoxViewModel : ObservableObject
{
    private readonly List<CollectRecordEntry> _entries;
    private SyncSummary _summary;

    public RecordBoxViewModel(IEnumerable<CollectRecordEntry> entries)
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

    public ObservableCollection<RecordRowViewModel> Drafts { get; }

    public ObservableCollection<RecordRowViewModel> Outbox { get; }

    public ObservableCollection<RecordRowViewModel> Conflicts { get; }

    public ObservableCollection<RecordRowViewModel> Sent { get; }

    public SyncSummary Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string Header => $"Drafts {Summary.Drafts} · Outbox {Summary.Outbox} · Conflicts {Summary.Conflicts} · Sent {Summary.Sent}";

    public void Refresh()
    {
        FillBox(Drafts, RecordBox.Drafts);
        FillBox(Outbox, RecordBox.Outbox);
        FillBox(Conflicts, RecordBox.Conflicts);
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
