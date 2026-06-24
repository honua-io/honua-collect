using System.Collections.ObjectModel;
using Honua.Collect.Core.History;

namespace Honua.Collect.Presentation.History;

/// <summary>View-model for one field-level change shown in a record's edit history.</summary>
public sealed class FieldChangeViewModel
{
    internal FieldChangeViewModel(FieldChange change)
    {
        FieldId = change.FieldId;
        BeforeText = change.OldValue?.ToString() ?? string.Empty;
        AfterText = change.NewValue?.ToString() ?? string.Empty;
    }

    /// <summary>Identifier of the field that changed.</summary>
    public string FieldId { get; }

    /// <summary>Prior value rendered for display.</summary>
    public string BeforeText { get; }

    /// <summary>New value rendered for display.</summary>
    public string AfterText { get; }

    /// <summary>A compact "field: before → after" summary for a single-line binding.</summary>
    public string Summary => $"{FieldId}: {BeforeText} → {AfterText}";
}

/// <summary>View-model for one version (recorded edit) in a record's edit history.</summary>
public sealed class RecordEditVersionViewModel
{
    internal RecordEditVersionViewModel(RecordEdit edit)
    {
        // Sequence is 0-based; show a 1-based, user-facing version number.
        Version = (int)edit.Sequence + 1;
        TimestampUtc = edit.TimestampUtc;
        Author = string.IsNullOrWhiteSpace(edit.EditorUserId) ? "Unknown" : edit.EditorUserId;
        AfterSync = edit.AfterSync;
        Note = edit.Note;
        Changes = edit.Changes.Select(c => new FieldChangeViewModel(c)).ToList();
    }

    /// <summary>User-facing version number (1-based).</summary>
    public int Version { get; }

    /// <summary>When the edit was committed.</summary>
    public DateTimeOffset TimestampUtc { get; }

    /// <summary>Author of the edit.</summary>
    public string Author { get; }

    /// <summary>Whether the edit was made after the record had already synced.</summary>
    public bool AfterSync { get; }

    /// <summary>Optional human note attached to the edit.</summary>
    public string? Note { get; }

    /// <summary>Field-level changes in this version.</summary>
    public IReadOnlyList<FieldChangeViewModel> Changes { get; }

    /// <summary>A header line for the version, e.g. "v3 — soleil — 2026-06-21 (post-sync)".</summary>
    public string Header
        => $"v{Version} — {Author} — {TimestampUtc:yyyy-MM-dd HH:mm} UTC{(AfterSync ? " (post-sync)" : string.Empty)}";
}

/// <summary>
/// View-model that lists the offline edit-history versions for a single record,
/// newest first (BACKLOG #38). Backed by the durable
/// <see cref="IRecordHistoryStore"/>; load is async so it can be bound and refreshed
/// without blocking the UI.
/// </summary>
public sealed class RecordHistoryViewModel
{
    private readonly IRecordHistoryStore _history;
    private readonly string _recordId;

    /// <summary>Creates the history view-model for a record.</summary>
    /// <param name="history">The durable edit-history store.</param>
    /// <param name="recordId">The record whose history to show.</param>
    public RecordHistoryViewModel(IRecordHistoryStore history, string recordId)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        _recordId = recordId;
    }

    /// <summary>The record whose history this view-model shows.</summary>
    public string RecordId => _recordId;

    /// <summary>Versions for the record, newest first; populated by <see cref="LoadAsync"/>.</summary>
    public ObservableCollection<RecordEditVersionViewModel> Versions { get; } = [];

    /// <summary>Whether the record has any recorded edit history.</summary>
    public bool HasHistory => Versions.Count > 0;

    /// <summary>Loads (or reloads) the record's history into <see cref="Versions"/>, newest first.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var edits = await _history.GetHistoryAsync(_recordId, ct).ConfigureAwait(false);
        Versions.Clear();
        foreach (var edit in edits.OrderByDescending(e => e.Sequence))
        {
            Versions.Add(new RecordEditVersionViewModel(edit));
        }
    }
}
