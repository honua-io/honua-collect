// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Field.Core.History;
using Honua.Field.Core.Records;
using Honua.Field.Presentation.Mvvm;

namespace Honua.Field.Presentation.History;

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
        Sequence = edit.Sequence;
        // Sequence is 0-based; show a 1-based, user-facing version number.
        Version = (int)edit.Sequence + 1;
        TimestampUtc = edit.TimestampUtc;
        Author = string.IsNullOrWhiteSpace(edit.EditorUserId) ? "Unknown" : edit.EditorUserId;
        AfterSync = edit.AfterSync;
        Note = edit.Note;
        Changes = edit.Changes.Select(c => new FieldChangeViewModel(c)).ToList();
    }

    /// <summary>Zero-based monotonic sequence identifier in <see cref="IRecordHistoryStore"/>.</summary>
    public long Sequence { get; }

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

    /// <summary>Action label for reverting the record to this version.</summary>
    public string RevertLabel => $"Revert to v{Version}";
}

/// <summary>
/// View-model that lists the offline edit-history versions for a single record,
/// newest first (BACKLOG #38 / #101), and exposes undo-last-change and
/// revert-to-version actions backed by <see cref="RecordRevertService"/>.
/// </summary>
public sealed class RecordHistoryViewModel : ObservableObject
{
    private readonly IRecordHistoryStore _history;
    private readonly string _recordId;
    private readonly FieldRecordEntry? _entry;
    private readonly RecordRevertService? _revertService;
    private readonly string? _editorUserId;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    /// <summary>Creates a read-only history view-model for a record.</summary>
    /// <param name="history">The durable edit-history store.</param>
    /// <param name="recordId">The record whose history to show.</param>
    public RecordHistoryViewModel(IRecordHistoryStore history, string recordId)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        _recordId = recordId;
        UndoLastChangeCommand = new RelayCommand(() => _ = RunCommandAsync(() => UndoLastChangeAsync()), () => CanRevert);
        RevertToVersionCommand = new RelayCommand<RecordEditVersionViewModel>(
            v => _ = RunCommandAsync(() => RevertToVersionAsync(v)),
            v => v is not null && CanRevert);
        RevertToOriginalCommand = new RelayCommand(() => _ = RunCommandAsync(() => RevertToOriginalAsync()), () => CanRevert);
    }

    /// <summary>Creates an interactive history view-model with undo and revert-to-version triggers.</summary>
    /// <param name="history">The durable edit-history store.</param>
    /// <param name="entry">The live record entry to revert.</param>
    /// <param name="revertService">Service that reconstructs and persists reverted values.</param>
    /// <param name="editorUserId">User performing any undo/revert (recorded on the new audit edit).</param>
    public RecordHistoryViewModel(
        IRecordHistoryStore history,
        FieldRecordEntry entry,
        RecordRevertService revertService,
        string editorUserId)
        : this(history, (entry ?? throw new ArgumentNullException(nameof(entry))).Record.RecordId)
    {
        _entry = entry;
        _revertService = revertService ?? throw new ArgumentNullException(nameof(revertService));
        ArgumentException.ThrowIfNullOrWhiteSpace(editorUserId);
        _editorUserId = editorUserId;
    }

    /// <summary>Raised after a revert or undo completes.</summary>
    public event EventHandler<RecordRevertResult>? RecordReverted;

    /// <summary>The record whose history this view-model shows.</summary>
    public string RecordId => _recordId;

    /// <summary>Versions for the record, newest first; populated by <see cref="LoadAsync"/>.</summary>
    public ObservableCollection<RecordEditVersionViewModel> Versions { get; } = [];

    /// <summary>Whether the record has any recorded edit history.</summary>
    public bool HasHistory => Versions.Count > 0;

    /// <summary>Whether undo/revert actions are configured and currently available.</summary>
    public bool CanRevert => _entry is not null && _revertService is not null && HasHistory && !IsBusy;

    /// <summary>Whether a history load or revert operation is currently running.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyRevertState();
            }
        }
    }

    /// <summary>Human-readable outcome of the latest undo or revert action.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Undoes the most recent recorded edit.</summary>
    public ICommand UndoLastChangeCommand { get; }

    /// <summary>Reverts the record to a selected historical version.</summary>
    public ICommand RevertToVersionCommand { get; }

    /// <summary>Reverts the record to its original captured values (before sequence 0).</summary>
    public ICommand RevertToOriginalCommand { get; }

    /// <summary>Loads (or reloads) the record's history into <see cref="Versions"/>, newest first.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await ReloadCoreAsync(ct);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Undoes the most recent recorded edit on the record, appends the audited
    /// revert edit, and reloads <see cref="Versions"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The revert result, or <see langword="null"/> when revert is not configured.</returns>
    public async Task<RecordRevertResult?> UndoLastChangeAsync(CancellationToken ct = default)
    {
        if (_entry is null || _revertService is null || _editorUserId is null || IsBusy)
        {
            return null;
        }

        IsBusy = true;
        try
        {
            var result = await _revertService.UndoLastChangeAsync(_entry, _editorUserId, ct: ct);
            await ReloadCoreAsync(ct);
            StatusMessage = result.RevertEdit is { } edit
                ? $"Undid last change (recorded as v{edit.Sequence + 1})."
                : "Nothing to undo — record already matches the previous state.";
            RecordReverted?.Invoke(this, result);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Reverts the record to the values it held immediately after
    /// <paramref name="version"/>, appends the audited revert edit, and reloads
    /// <see cref="Versions"/>.
    /// </summary>
    /// <param name="version">Target version row to restore.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The revert result, or <see langword="null"/> when unavailable.</returns>
    public async Task<RecordRevertResult?> RevertToVersionAsync(
        RecordEditVersionViewModel? version,
        CancellationToken ct = default)
    {
        if (version is null || _entry is null || _revertService is null || _editorUserId is null || IsBusy)
        {
            return null;
        }

        IsBusy = true;
        try
        {
            var result = await _revertService.RevertToVersionAsync(_entry, version.Sequence, _editorUserId, ct: ct);
            await ReloadCoreAsync(ct);
            StatusMessage = result.RevertEdit is { } edit
                ? $"Reverted to v{version.Version} (recorded as v{edit.Sequence + 1})."
                : $"Record already matches v{version.Version}.";
            RecordReverted?.Invoke(this, result);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Reverts the record to its original captured values (before sequence 0),
    /// appends the audited revert edit, and reloads <see cref="Versions"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The revert result, or <see langword="null"/> when unavailable.</returns>
    public async Task<RecordRevertResult?> RevertToOriginalAsync(CancellationToken ct = default)
    {
        if (_entry is null || _revertService is null || _editorUserId is null || IsBusy)
        {
            return null;
        }

        IsBusy = true;
        try
        {
            var result = await _revertService.RevertToVersionAsync(_entry, -1, _editorUserId, ct: ct);
            await ReloadCoreAsync(ct);
            StatusMessage = result.RevertEdit is { } edit
                ? $"Reverted to original capture (recorded as v{edit.Sequence + 1})."
                : "Record already matches the original capture.";
            RecordReverted?.Invoke(this, result);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadCoreAsync(CancellationToken ct)
    {
        var edits = await _history.GetHistoryAsync(_recordId, ct);
        Versions.Clear();
        foreach (var edit in edits.OrderByDescending(e => e.Sequence))
        {
            Versions.Add(new RecordEditVersionViewModel(edit));
        }

        OnPropertyChanged(nameof(HasHistory));
        NotifyRevertState();
    }

    private async Task RunCommandAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Revert failed: {ex.Message}";
        }
    }

    private void NotifyRevertState()
    {
        OnPropertyChanged(nameof(CanRevert));
        (UndoLastChangeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RevertToVersionCommand as RelayCommand<RecordEditVersionViewModel>)?.RaiseCanExecuteChanged();
        (RevertToOriginalCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
