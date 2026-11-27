// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;
using Honua.Field.Core.Records;
using Honua.Field.Core.Sync;
using Honua.Field.Presentation.Mvvm;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Presentation.Sync;

/// <summary>View-model for one conflicted field in the review screen.</summary>
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

    /// <summary>Field identifier.</summary>
    public string FieldId { get; }

    /// <summary>Field label.</summary>
    public string Label { get; }

    /// <summary>Local value rendered for display.</summary>
    public string LocalText { get; }

    /// <summary>Server value rendered for display.</summary>
    public string ServerText { get; }

    /// <summary>Which side the user chose to keep.</summary>
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

    /// <summary>Convenience flag for a radio/toggle binding.</summary>
    public bool KeepLocal
    {
        get => _resolution == ConflictResolution.KeepLocal;
        set => Resolution = value ? ConflictResolution.KeepLocal : ConflictResolution.KeepServer;
    }
}

/// <summary>
/// View-model for the manual conflict-review screen (BACKLOG S1): a field-by-field
/// diff of the local and server versions with per-field keep-local/keep-server
/// choices, plus bulk actions, resolving to a merged record.
/// </summary>
public sealed class ConflictReviewViewModel : ObservableObject
{
    private readonly RecordConflict _conflict;
    private readonly FieldRecordEntry? _entry;
    private bool _isResolved;
    private bool _isSaving;

    /// <summary>Creates the review over a detected conflict.</summary>
    /// <param name="conflict">The record conflict to resolve.</param>
    /// <param name="defaultResolution">Initial per-field choice.</param>
    public ConflictReviewViewModel(RecordConflict conflict, ConflictResolution defaultResolution = ConflictResolution.KeepServer)
        : this(conflict, null, defaultResolution)
    {
    }

    /// <summary>
    /// Creates the review bound to the local record entry that is in conflict, so
    /// the chosen resolution can be applied straight back onto the entry and the
    /// record re-queued for upload (BACKLOG S1 wiring).
    /// </summary>
    /// <param name="entry">The conflicted local record entry.</param>
    /// <param name="defaultResolution">Initial per-field choice.</param>
    public ConflictReviewViewModel(FieldRecordEntry entry, ConflictResolution defaultResolution = ConflictResolution.KeepServer)
        : this(RequireConflict(entry), entry, defaultResolution)
    {
    }

    private ConflictReviewViewModel(RecordConflict conflict, FieldRecordEntry? entry, ConflictResolution defaultResolution)
    {
        _conflict = conflict ?? throw new ArgumentNullException(nameof(conflict));
        _entry = entry;
        Conflicts = conflict.FieldConflicts.Select(c => new FieldConflictViewModel(c, defaultResolution)).ToList();
        KeepAllLocalCommand = new RelayCommand(() => SetAll(ConflictResolution.KeepLocal));
        KeepAllServerCommand = new RelayCommand(() => SetAll(ConflictResolution.KeepServer));
        ApplyResolutionCommand = new RelayCommand(() => ApplyResolution(), () => CanApply);
    }

    private static RecordConflict RequireConflict(FieldRecordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Conflict
            ?? throw new ArgumentException("The record entry is not in a conflicted state.", nameof(entry));
    }

    /// <summary>Identifier of the record under review.</summary>
    public string RecordId => _conflict.RecordId;

    /// <summary>Field-level conflicts.</summary>
    public IReadOnlyList<FieldConflictViewModel> Conflicts { get; }

    /// <summary>Whether there is anything to resolve.</summary>
    public bool HasConflicts => _conflict.HasConflicts;

    /// <summary>Sets every field to keep the local value.</summary>
    public ICommand KeepAllLocalCommand { get; }

    /// <summary>Sets every field to keep the server value.</summary>
    public ICommand KeepAllServerCommand { get; }

    /// <summary>
    /// Applies the current choices to the bound record entry and re-queues it. Only
    /// available when the review was created over a live <see cref="FieldRecordEntry"/>
    /// and has not been applied yet.
    /// </summary>
    public ICommand ApplyResolutionCommand { get; }

    /// <summary>Whether the resolution can be applied back to a bound entry.</summary>
    public bool CanApply => !_isSaving && !_isResolved
        && _entry?.IsConflicted == true && ReferenceEquals(_entry.Conflict, _conflict);

    /// <summary>Whether the resolution has already been applied to the bound entry.</summary>
    public bool IsResolved
    {
        get => _isResolved;
        private set
        {
            if (SetProperty(ref _isResolved, value))
            {
                NotifyCanApplyChanged();
            }
        }
    }

    /// <summary>Builds the merged record from the current choices.</summary>
    /// <returns>The resolved record.</returns>
    public FieldRecord Resolve()
    {
        var choices = Conflicts.ToDictionary(c => c.FieldId, c => c.Resolution, StringComparer.OrdinalIgnoreCase);
        return _conflict.Resolve(choices);
    }

    /// <summary>
    /// Resolves the conflict and hands the merged record back to the bound entry
    /// via <see cref="FieldRecordEntry.ApplyResolution"/>, moving it out of the
    /// Conflicts box and back into the Outbox.
    /// </summary>
    /// <returns>The merged record that was applied.</returns>
    public FieldRecord ApplyResolution()
    {
        if (_entry is null)
        {
            throw new InvalidOperationException("This review is not bound to a record entry; use Resolve() instead.");
        }

        if (!CanApply)
        {
            throw new InvalidOperationException("This conflict review is no longer current or is already being saved.");
        }

        var merged = Resolve();
        _entry.ApplyResolution(merged);
        IsResolved = true;
        return merged;
    }

    /// <summary>
    /// Persists the resolved snapshot before updating the live entry and moving it to the Outbox.
    /// A failed write leaves the original conflict and the selected choices available for retry.
    /// </summary>
    /// <param name="persist">Durable record store sink, or null for an in-memory resolution.</param>
    /// <returns>The merged record that was applied.</returns>
    public async Task<FieldRecord> ApplyResolutionAsync(RecordStatePersister? persist)
    {
        if (persist is null)
        {
            return ApplyResolution();
        }

        if (_entry is null || !CanApply)
        {
            throw new InvalidOperationException("This conflict review cannot currently be applied.");
        }

        _isSaving = true;
        try
        {
            NotifyCanApplyChanged();
            var merged = Resolve();
            await persist(_entry.CreateResolvedSnapshot(merged));
            _entry.ApplyResolution(merged);
            IsResolved = true;
            return merged;
        }
        finally
        {
            _isSaving = false;
            NotifyCanApplyChanged();
        }
    }

    private void NotifyCanApplyChanged()
    {
        OnPropertyChanged(nameof(CanApply));
        (ApplyResolutionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void SetAll(ConflictResolution resolution)
    {
        foreach (var conflict in Conflicts)
        {
            conflict.Resolution = resolution;
        }
    }
}
