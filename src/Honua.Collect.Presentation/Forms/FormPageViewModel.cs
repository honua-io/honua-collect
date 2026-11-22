// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Forms;

public sealed class FormPageViewModel : ObservableObject
{
    private readonly FormSession _session;
    private string _errorSummary = string.Empty;
    private bool _canSubmit;
    private bool _isSubmitted;

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

    public string Title => _session.Form.Name;

    public IReadOnlyList<FieldViewModel> Fields { get; }

    public IReadOnlyList<RepeatGroupViewModel> RepeatGroups { get; }

    public IEnumerable<FieldViewModel> VisibleFields => Fields.Where(f => f.IsVisible);

    public string ErrorSummary
    {
        get => _errorSummary;
        private set => SetProperty(ref _errorSummary, value);
    }

    public bool CanSubmit
    {
        get => _canSubmit;
        private set => SetProperty(ref _canSubmit, value);
    }

    public bool IsSubmitted
    {
        get => _isSubmitted;
        private set => SetProperty(ref _isSubmitted, value);
    }

    public FieldRecord Record => _session.Record;

    public FormSession Session => _session;

    public void RefreshFields() => RefreshAll();

    public ICommand SubmitCommand { get; }

    public ICommand SaveDraftCommand { get; }

    public event EventHandler<FieldRecord>? SubmitSucceeded;

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
