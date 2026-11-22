// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Sync;

public sealed class FieldConflictViewModel : ObservableObject
{
    private ConflictResolution _resolution;

    internal FieldConflictViewModel(FieldConflict conflict, ConflictResolution initial)
    {
        FieldId = conflict.FieldId;
        Label = conflict.Label;
        LocalText = conflict.LocalValue?.ToString() ?? string.Empty;
        ServerText = conflict.ServerValue?.ToString() ?? string.Empty;
        _resolution = initial;
    }

    public string FieldId { get; }

    public string Label { get; }

    public string LocalText { get; }

    public string ServerText { get; }

    public ConflictResolution Resolution
    {
        get => _resolution;
        set
        {
            if (SetProperty(ref _resolution, value))
            {
                OnPropertyChanged(nameof(KeepLocal));
            }
        }
    }

    public bool KeepLocal
    {
        get => _resolution == ConflictResolution.KeepLocal;
        set => Resolution = value ? ConflictResolution.KeepLocal : ConflictResolution.KeepServer;
    }
}

public sealed class ConflictReviewViewModel : ObservableObject
{
    private readonly RecordConflict _conflict;
    private readonly CollectRecordEntry? _entry;
    private bool _isResolved;

    public ConflictReviewViewModel(RecordConflict conflict, ConflictResolution defaultResolution = ConflictResolution.KeepServer)
        : this(conflict, null, defaultResolution)
    {
    }

    public ConflictReviewViewModel(CollectRecordEntry entry, ConflictResolution defaultResolution = ConflictResolution.KeepServer)
        : this(RequireConflict(entry), entry, defaultResolution)
    {
    }

    private ConflictReviewViewModel(RecordConflict conflict, CollectRecordEntry? entry, ConflictResolution defaultResolution)
    {
        _conflict = conflict ?? throw new ArgumentNullException(nameof(conflict));
        _entry = entry;
        Conflicts = conflict.FieldConflicts.Select(c => new FieldConflictViewModel(c, defaultResolution)).ToList();
        KeepAllLocalCommand = new RelayCommand(() => SetAll(ConflictResolution.KeepLocal));
        KeepAllServerCommand = new RelayCommand(() => SetAll(ConflictResolution.KeepServer));
        ApplyResolutionCommand = new RelayCommand(() => ApplyResolution(), () => CanApply);
    }

    private static RecordConflict RequireConflict(CollectRecordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Conflict
            ?? throw new ArgumentException("The record entry is not in a conflicted state.", nameof(entry));
    }

    public string RecordId => _conflict.RecordId;

    public IReadOnlyList<FieldConflictViewModel> Conflicts { get; }

    public bool HasConflicts => _conflict.HasConflicts;

    public ICommand KeepAllLocalCommand { get; }

    public ICommand KeepAllServerCommand { get; }

    public ICommand ApplyResolutionCommand { get; }

    public bool CanApply => _entry is not null && !_isResolved;

    public bool IsResolved
    {
        get => _isResolved;
        private set
        {
            if (SetProperty(ref _isResolved, value))
            {
                (ApplyResolutionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public FieldRecord Resolve()
    {
        var choices = Conflicts.ToDictionary(c => c.FieldId, c => c.Resolution, StringComparer.OrdinalIgnoreCase);
        return _conflict.Resolve(choices);
    }

    public FieldRecord ApplyResolution()
    {
        if (_entry is null)
        {
            throw new InvalidOperationException("This review is not bound to a record entry; use Resolve() instead.");
        }

        if (_isResolved)
        {
            throw new InvalidOperationException("The conflict has already been resolved.");
        }

        var merged = Resolve();
        _entry.ApplyResolution(merged);
        IsResolved = true;
        return merged;
    }

    public async Task<FieldRecord> ApplyResolutionAsync(RecordStatePersister? persist)
    {
        var merged = ApplyResolution();
        if (persist is not null && _entry is not null)
        {
            await persist(_entry).ConfigureAwait(false);
        }

        return merged;
    }

    private void SetAll(ConflictResolution resolution)
    {
        foreach (var conflict in Conflicts)
        {
            conflict.Resolution = resolution;
        }
    }
}
