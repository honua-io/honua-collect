// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Windows.Input;
using Honua.Field.Core.Field.Inspections;
using Honua.Field.Presentation.Forms;
using Honua.Field.Presentation.Mvvm;

namespace Honua.Field.Presentation.Inspections;

/// <summary>
/// View-model for a multi-step inspection screen. Binds an
/// <see cref="InspectionWorkflowSession"/> to step-scoped
/// <see cref="FieldViewModel"/>s, live validation and pass/fail status
/// summaries, and step navigation / completion commands.
/// </summary>
public sealed class InspectionWorkflowViewModel : ObservableObject
{
    private readonly InspectionWorkflowSession _workflowSession;
    private readonly Dictionary<string, FieldViewModel> _allFields;
    private int _activeStepIndex = -1;
    private IReadOnlyList<FieldViewModel> _currentStepFields = [];
    private InspectionOutcome _latestOutcome = null!;
    private InspectionStatus _currentStepStatus;
    private InspectionStatus _overallStatus;
    private string _validationSummary = string.Empty;
    private string _failureSummary = string.Empty;
    private bool _canMovePrevious;
    private bool _canMoveNext;
    private bool _canComplete;
    private bool _isCompleted;

    /// <summary>Creates a view-model over an inspection workflow session.</summary>
    /// <param name="workflowSession">The active inspection workflow session.</param>
    public InspectionWorkflowViewModel(InspectionWorkflowSession workflowSession)
    {
        _workflowSession = workflowSession ?? throw new ArgumentNullException(nameof(workflowSession));

        _allFields = _workflowSession.FormSession.Fields.ToDictionary(
            f => f.FieldId,
            f => new FieldViewModel(_workflowSession.FormSession, f.FieldId, RefreshAll),
            StringComparer.OrdinalIgnoreCase);

        NextStepCommand = new RelayCommand(MoveNext, () => CanMoveNext);
        PreviousStepCommand = new RelayCommand(MovePrevious, () => CanMovePrevious);
        CompleteCommand = new RelayCommand(Complete, () => CanComplete);

        RefreshAll();
    }

    /// <summary>Workflow title.</summary>
    public string Title => _workflowSession.Workflow.Name;

    /// <summary>Current step display title.</summary>
    public string CurrentStepTitle => _workflowSession.CurrentStep.Title;

    /// <summary>Optional instructions for the current step.</summary>
    public string? CurrentStepDescription => _workflowSession.CurrentStep.Description;

    /// <summary>One-based current step number.</summary>
    public int CurrentStepNumber => _workflowSession.CurrentStepIndex + 1;

    /// <summary>Total number of steps in the workflow.</summary>
    public int TotalSteps => _workflowSession.Workflow.Steps.Count;

    /// <summary>Formatted progress label (for example <c>Step 1 of 3</c>).</summary>
    public string StepProgressText => $"Step {CurrentStepNumber} of {TotalSteps}";

    /// <summary>Whether the active step is the final step.</summary>
    public bool IsLastStep => _workflowSession.IsLastStep;

    /// <summary>Fields belonging to the active step.</summary>
    public IReadOnlyList<FieldViewModel> CurrentStepFields
    {
        get => _currentStepFields;
        private set => SetProperty(ref _currentStepFields, value);
    }

    /// <summary>Currently visible fields belonging to the active step.</summary>
    public IEnumerable<FieldViewModel> VisibleStepFields => CurrentStepFields.Where(f => f.IsVisible);

    /// <summary>Pass/fail/in-progress status of the active step.</summary>
    public InspectionStatus CurrentStepStatus
    {
        get => _currentStepStatus;
        private set => SetProperty(ref _currentStepStatus, value);
    }

    /// <summary>Aggregate pass/fail/in-progress status of the inspection.</summary>
    public InspectionStatus OverallStatus
    {
        get => _overallStatus;
        private set => SetProperty(ref _overallStatus, value);
    }

    /// <summary>Summary of validation errors on the active step.</summary>
    public string ValidationSummary
    {
        get => _validationSummary;
        private set => SetProperty(ref _validationSummary, value);
    }

    /// <summary>Summary of failed pass criteria across the inspection.</summary>
    public string FailureSummary
    {
        get => _failureSummary;
        private set => SetProperty(ref _failureSummary, value);
    }

    /// <summary>Whether the inspector can navigate to the previous step.</summary>
    public bool CanMovePrevious
    {
        get => _canMovePrevious;
        private set => SetProperty(ref _canMovePrevious, value);
    }

    /// <summary>Whether the inspector can advance to the next step.</summary>
    public bool CanMoveNext
    {
        get => _canMoveNext;
        private set => SetProperty(ref _canMoveNext, value);
    }

    /// <summary>Whether the inspection can be completed.</summary>
    public bool CanComplete
    {
        get => _canComplete;
        private set => SetProperty(ref _canComplete, value);
    }

    /// <summary>Whether the inspection has been completed.</summary>
    public bool IsCompleted
    {
        get => _isCompleted;
        private set => SetProperty(ref _isCompleted, value);
    }

    /// <summary>The cached inspection outcome from the latest refresh pass.</summary>
    public InspectionOutcome LatestOutcome => _latestOutcome;

    /// <summary>Advances to the next step when the current step is valid.</summary>
    public ICommand NextStepCommand { get; }

    /// <summary>Returns to the previous step.</summary>
    public ICommand PreviousStepCommand { get; }

    /// <summary>Completes and submits the inspection when all fields are valid.</summary>
    public ICommand CompleteCommand { get; }

    /// <summary>Raised when the inspection completes.</summary>
    public event EventHandler<InspectionCompletionResult>? InspectionCompleted;

    private void MoveNext()
    {
        if (_workflowSession.MoveNext())
        {
            RefreshAll();
        }
    }

    private void MovePrevious()
    {
        if (_workflowSession.MovePrevious())
        {
            RefreshAll();
        }
    }

    private void Complete()
    {
        var result = _workflowSession.Complete();
        RefreshAll();
        if (result.Succeeded)
        {
            InspectionCompleted?.Invoke(this, result);
        }
    }

    private void RefreshAll()
    {
        var snapshot = _workflowSession.CaptureSnapshot();
        var outcome = snapshot.Outcome;
        var stepResult = outcome.StepResults[_workflowSession.CurrentStepIndex];

        foreach (var field in _allFields.Values)
        {
            field.Refresh();
        }

        if (_activeStepIndex != _workflowSession.CurrentStepIndex)
        {
            _activeStepIndex = _workflowSession.CurrentStepIndex;
            CurrentStepFields = _workflowSession.GetFieldsForStep(_workflowSession.CurrentStep)
                .Select(state => _allFields[state.FieldId])
                .ToList();

            OnPropertyChanged(nameof(CurrentStepTitle));
            OnPropertyChanged(nameof(CurrentStepDescription));
            OnPropertyChanged(nameof(CurrentStepNumber));
            OnPropertyChanged(nameof(StepProgressText));
            OnPropertyChanged(nameof(IsLastStep));
        }

        _latestOutcome = outcome;
        OnPropertyChanged(nameof(VisibleStepFields));
        OnPropertyChanged(nameof(LatestOutcome));

        CurrentStepStatus = stepResult.Status;
        OverallStatus = outcome.Status;
        ValidationSummary = stepResult.IsValid
            ? string.Empty
            : $"{stepResult.ValidationErrors.Count} field(s) need attention.";
        FailureSummary = outcome.FailedReasons.Count == 0
            ? string.Empty
            : string.Join(" ", outcome.FailedReasons);

        CanMovePrevious = snapshot.CanMovePrevious;
        CanMoveNext = snapshot.CanMoveNext;
        CanComplete = snapshot.CanComplete;
        IsCompleted = _workflowSession.IsCompleted;

        (PreviousStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CompleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
