// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Windows.Input;
using Honua.Collect.Core.History;
using Honua.Collect.Core.Records;
using Honua.Collect.Presentation.Mvvm;

namespace Honua.Collect.Presentation.History;

public sealed class FieldChangeViewModel
{
    internal FieldChangeViewModel(FieldChange change)
    {
        FieldId = change.FieldId;
        BeforeText = change.OldValue?.ToString() ?? string.Empty;
        AfterText = change.NewValue?.ToString() ?? string.Empty;
    }

    public string FieldId { get; }

    public string BeforeText { get; }

    public string AfterText { get; }

    public string Summary => $"{FieldId}: {BeforeText} → {AfterText}";
}

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

    public long Sequence { get; }

    public int Version { get; }

    public DateTimeOffset TimestampUtc { get; }

    public string Author { get; }

    public bool AfterSync { get; }

    public string? Note { get; }

    public IReadOnlyList<FieldChangeViewModel> Changes { get; }

    public string Header
        => $"v{Version} — {Author} — {TimestampUtc:yyyy-MM-dd HH:mm} UTC{(AfterSync ? " (post-sync)" : string.Empty)}";

    public string RevertLabel => $"Revert to v{Version}";
}

public sealed class RecordHistoryViewModel : ObservableObject
{
    private readonly IRecordHistoryStore _history;
    private readonly string _recordId;
    private readonly CollectRecordEntry? _entry;
    private readonly RecordRevertService? _revertService;
    private readonly string? _editorUserId;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

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

    public RecordHistoryViewModel(
        IRecordHistoryStore history,
        CollectRecordEntry entry,
        RecordRevertService revertService,
        string editorUserId)
        : this(history, (entry ?? throw new ArgumentNullException(nameof(entry))).Record.RecordId)
    {
        _entry = entry;
        _revertService = revertService ?? throw new ArgumentNullException(nameof(revertService));
        ArgumentException.ThrowIfNullOrWhiteSpace(editorUserId);
        _editorUserId = editorUserId;
    }

    public event EventHandler<RecordRevertResult>? RecordReverted;

    public string RecordId => _recordId;

    public ObservableCollection<RecordEditVersionViewModel> Versions { get; } = [];

    public bool HasHistory => Versions.Count > 0;

    public bool CanRevert => _entry is not null && _revertService is not null && HasHistory && !IsBusy;

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

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ICommand UndoLastChangeCommand { get; }

    public ICommand RevertToVersionCommand { get; }

    public ICommand RevertToOriginalCommand { get; }

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
