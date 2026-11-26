// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;
using Honua.Field.Core.Field.Forms;
using Honua.Field.Presentation.Mvvm;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Presentation.Forms;

/// <summary>
/// View-model for the dynamic form-capture screen. It wraps a <see cref="FormSession"/> and exposes the scalar fields
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
    private bool _editingEnabled = true;

    /// <summary>Creates the page view-model over a form session.</summary>
    /// <param name="session">The capture session to drive.</param>
    /// <param name="canSaveDraft">Whether the host provides durable draft storage.</param>
    public FormPageViewModel(FormSession session, bool canSaveDraft = false)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));

        CanSaveDraft = canSaveDraft;

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

    /// <summary>The underlying capture session (for AI-assisted fill, etc.).</summary>
    public FormSession Session => _session;

    /// <summary>Re-reads field state after the session is mutated externally (e.g. AI fill).</summary>
    public void RefreshFields() => RefreshAll();

    /// <summary>Submits the form when valid.</summary>
    public ICommand SubmitCommand { get; }

    /// <summary>Saves the current state as a draft.</summary>
    public ICommand SaveDraftCommand { get; }

    /// <summary>Raised when the record is successfully submitted.</summary>
    public event EventHandler<FieldRecord>? SubmitSucceeded;

    /// <summary>Raised when a draft is saved.</summary>
    public event EventHandler<FieldRecord>? DraftSaved;

    /// <summary>Whether this screen has durable storage for drafts.</summary>
    public bool CanSaveDraft { get; }

    /// <summary>Controls editing while an online submission awaits a definitive receipt.</summary>
    public bool EditingEnabled
    {
        get => _editingEnabled;
        set => SetProperty(ref _editingEnabled, value);
    }

    /// <summary>Restores the editable draft when its transition into the local outbox fails.</summary>
    public void RestoreDraftAfterSaveFailure()
    {
        _session.Record.Status = RecordStatus.Draft;
        _session.Record.SubmittedAtUtc = null;
        IsSubmitted = false;
        RefreshAll();
    }

    private void Submit()
    {
        var result = IsSubmitted ? _session.Validate() : _session.Submit();
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
        // Validate first so scalar FieldState values (including record-link collections
        // mutated through RelatedRecords) and per-field validation errors are recomputed
        // before bound FieldViewModels notify the UI.
        var validation = _session.Validate();

        foreach (var field in Fields)
        {
            field.Refresh();
        }

        foreach (var group in RepeatGroups)
        {
            group.Refresh();
        }

        OnPropertyChanged(nameof(VisibleFields));

        CanSubmit = validation.IsValid;
        ErrorSummary = validation.IsValid
            ? string.Empty
            : $"{validation.Errors.Count} field(s) need attention.";
        (SubmitCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
