using System.Windows.Input;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Forms;

/// <summary>
/// View-model for the dynamic form-capture screen — the central Survey123/Fulcrum
/// experience. It wraps a <see cref="FormSession"/> and exposes the scalar fields
/// and repeat groups the XAML renders, a live validation summary, and the
/// save-draft / submit commands. All field edits flow back through the runtime,
/// which recomputes visibility, calculated fields, and validation; the page then
/// refreshes so dependent fields show/hide and errors update live.
/// </summary>
public sealed class FormPageViewModel : ObservableObject
{
    private readonly FormSession _session;
    private string _errorSummary = string.Empty;
    private bool _canSubmit;
    private bool _isSubmitted;

    /// <summary>Creates the page view-model over a form session.</summary>
    /// <param name="session">The capture session to drive.</param>
    public FormPageViewModel(FormSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));

        Fields = _session.Fields
            .Select(state => new FieldViewModel(_session, state.FieldId, RefreshAll))
            .ToList();

        RepeatGroups = _session.RepeatGroups
            .Select(group => new RepeatGroupViewModel(group, RefreshAll))
            .ToList();

        SubmitCommand = new RelayCommand(Submit, () => CanSubmit);
        SaveDraftCommand = new RelayCommand(SaveDraft);

        RefreshAll();
    }

    /// <summary>Form title.</summary>
    public string Title => _session.Form.Name;

    /// <summary>Scalar (non-repeating) field view-models, in form order.</summary>
    public IReadOnlyList<FieldViewModel> Fields { get; }

    /// <summary>Repeatable-section view-models.</summary>
    public IReadOnlyList<RepeatGroupViewModel> RepeatGroups { get; }

    /// <summary>Visible field view-models (for renderers that hide rather than disable).</summary>
    public IEnumerable<FieldViewModel> VisibleFields => Fields.Where(f => f.IsVisible);

    /// <summary>A human-readable summary of outstanding validation errors.</summary>
    public string ErrorSummary
    {
        get => _errorSummary;
        private set => SetProperty(ref _errorSummary, value);
    }

    /// <summary>Whether the form is complete and can be submitted.</summary>
    public bool CanSubmit
    {
        get => _canSubmit;
        private set => SetProperty(ref _canSubmit, value);
    }

    /// <summary>Whether the form has been submitted.</summary>
    public bool IsSubmitted
    {
        get => _isSubmitted;
        private set => SetProperty(ref _isSubmitted, value);
    }

    /// <summary>The underlying record (for persistence / navigation).</summary>
    public FieldRecord Record => _session.Record;

    /// <summary>Submits the form when valid.</summary>
    public ICommand SubmitCommand { get; }

    /// <summary>Saves the current state as a draft.</summary>
    public ICommand SaveDraftCommand { get; }

    /// <summary>Raised when the record is successfully submitted.</summary>
    public event EventHandler<FieldRecord>? SubmitSucceeded;

    /// <summary>Raised when a draft is saved.</summary>
    public event EventHandler<FieldRecord>? DraftSaved;

    private void Submit()
    {
        var result = _session.Submit();
        RefreshAll();

        if (result.IsValid)
        {
            IsSubmitted = true;
            SubmitSucceeded?.Invoke(this, _session.Record);
        }
    }

    private void SaveDraft()
    {
        _session.Validate(); // persists repeat rows into the record
        DraftSaved?.Invoke(this, _session.Record);
    }

    private void RefreshAll()
    {
        foreach (var field in Fields)
        {
            field.Refresh();
        }

        foreach (var group in RepeatGroups)
        {
            group.Refresh();
        }

        OnPropertyChanged(nameof(VisibleFields));

        var validation = _session.Validate();
        CanSubmit = validation.IsValid;
        ErrorSummary = validation.IsValid
            ? string.Empty
            : $"{validation.Errors.Count} field(s) need attention.";
        (SubmitCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
