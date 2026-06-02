using System.Windows.Input;
using Honua.Collect.Core.Sync;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Sync;

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

    /// <summary>Creates the review over a detected conflict.</summary>
    /// <param name="conflict">The record conflict to resolve.</param>
    /// <param name="defaultResolution">Initial per-field choice.</param>
    public ConflictReviewViewModel(RecordConflict conflict, ConflictResolution defaultResolution = ConflictResolution.KeepServer)
    {
        _conflict = conflict ?? throw new ArgumentNullException(nameof(conflict));
        Conflicts = conflict.FieldConflicts.Select(c => new FieldConflictViewModel(c, defaultResolution)).ToList();
        KeepAllLocalCommand = new RelayCommand(() => SetAll(ConflictResolution.KeepLocal));
        KeepAllServerCommand = new RelayCommand(() => SetAll(ConflictResolution.KeepServer));
    }

    /// <summary>Field-level conflicts.</summary>
    public IReadOnlyList<FieldConflictViewModel> Conflicts { get; }

    /// <summary>Whether there is anything to resolve.</summary>
    public bool HasConflicts => _conflict.HasConflicts;

    /// <summary>Sets every field to keep the local value.</summary>
    public ICommand KeepAllLocalCommand { get; }

    /// <summary>Sets every field to keep the server value.</summary>
    public ICommand KeepAllServerCommand { get; }

    /// <summary>Builds the merged record from the current choices.</summary>
    /// <returns>The resolved record.</returns>
    public FieldRecord Resolve()
    {
        var choices = Conflicts.ToDictionary(c => c.FieldId, c => c.Resolution, StringComparer.OrdinalIgnoreCase);
        return _conflict.Resolve(choices);
    }

    private void SetAll(ConflictResolution resolution)
    {
        foreach (var conflict in Conflicts)
        {
            conflict.Resolution = resolution;
        }
    }
}
