// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Forms.Expressions;

namespace Honua.Collect.Core.Field.Inspections;

/// <summary>
/// Stateful multi-step inspection driver layered over a <see cref="FormSession"/>.
/// Enforces per-step validation and required-field gates before advancing, tracks
/// step and overall <see cref="InspectionStatus"/> (<c>NotStarted</c>,
/// <c>InProgress</c>, <c>Passed</c>, <c>Failed</c>), and records the final
/// pass/fail outcome when the inspection completes.
/// </summary>
public sealed class InspectionWorkflowSession
{
    private readonly HashSet<string> _visitedSteps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completedSteps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an inspection workflow session over a live form session.</summary>
    /// <param name="formSession">The underlying form capture session.</param>
    /// <param name="workflow">The multi-step inspection definition.</param>
    public InspectionWorkflowSession(FormSession formSession, InspectionWorkflowDefinition workflow)
    {
        FormSession = formSession ?? throw new ArgumentNullException(nameof(formSession));
        Workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));

        if (workflow.Steps is null || workflow.Steps.Count == 0)
        {
            throw new ArgumentException("An inspection workflow must define at least one step.", nameof(workflow));
        }

        _visitedSteps.Add(workflow.Steps[0].StepId);
    }

    /// <summary>The underlying form capture session.</summary>
    public FormSession FormSession { get; }

    /// <summary>The workflow definition driving step order and pass/fail criteria.</summary>
    public InspectionWorkflowDefinition Workflow { get; }

    /// <summary>Zero-based index of the active step.</summary>
    public int CurrentStepIndex { get; private set; }

    /// <summary>The currently active step definition.</summary>
    public InspectionStepDefinition CurrentStep => Workflow.Steps[CurrentStepIndex];

    /// <summary>Whether the active step is the final step in the workflow.</summary>
    public bool IsLastStep => CurrentStepIndex == Workflow.Steps.Count - 1;

    /// <summary>Whether the inspection has been completed and submitted.</summary>
    public bool IsCompleted { get; private set; }

    /// <summary>Whether the inspector can navigate back to the previous step.</summary>
    public bool CanMovePrevious => !IsCompleted && CurrentStepIndex > 0;

    /// <summary>
    /// Whether the inspector can advance from the current step to the next step.
    /// Requires the current step's fields to be valid and, when
    /// <see cref="InspectionStepDefinition.AllowAdvanceOnFail"/> is
    /// <see langword="false"/>, all pass criteria to succeed.
    /// </summary>
    public bool CanMoveNext => !IsCompleted && !IsLastStep && CanLeaveStep(CurrentStepIndex, FormSession.Validate());

    /// <summary>Whether the inspection can be completed right now.</summary>
    public bool CanComplete => !IsCompleted && CanCompleteWith(FormSession.Validate());

    /// <summary>Raised when <see cref="CurrentStepIndex"/> changes.</summary>
    public event EventHandler<int>? StepChanged;

    /// <summary>Raised when the inspection completes successfully.</summary>
    public event EventHandler<InspectionCompletionResult>? Completed;

    /// <summary>Returns the scalar fields belonging to <paramref name="step"/>, in form order.</summary>
    /// <param name="step">The step definition.</param>
    /// <returns>The step's scalar field states.</returns>
    public IReadOnlyList<FieldState> GetFieldsForStep(InspectionStepDefinition step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var sectionIds = new HashSet<string>(step.SectionIds, StringComparer.OrdinalIgnoreCase);
        var fieldIds = new HashSet<string>(step.FieldIds, StringComparer.OrdinalIgnoreCase);

        return FormSession.Fields
            .Where(f => sectionIds.Contains(f.Section.SectionId) || fieldIds.Contains(f.FieldId))
            .ToList();
    }

    /// <summary>Evaluates a single step's validation errors and pass/fail criteria.</summary>
    /// <param name="stepIndex">Zero-based step index.</param>
    /// <returns>The step evaluation snapshot.</returns>
    public InspectionStepResult EvaluateStep(int stepIndex)
    {
        ValidateStepIndex(stepIndex);
        return EvaluateStepCore(stepIndex, FormSession.Validate(), forceEvaluate: IsCompleted);
    }

    /// <summary>Evaluates every step and returns the aggregate inspection outcome.</summary>
    /// <returns>The aggregate outcome.</returns>
    public InspectionOutcome EvaluateOutcome()
        => EvaluateOutcomeCore(FormSession.Validate(), forceEvaluateAll: IsCompleted);

    /// <summary>
    /// Validates the form once and returns the aggregate outcome alongside
    /// current navigation and completion flags.
    /// </summary>
    /// <returns>A consistent single-pass snapshot.</returns>
    public InspectionWorkflowSnapshot CaptureSnapshot()
    {
        var validation = FormSession.Validate();
        var outcome = EvaluateOutcomeCore(validation, forceEvaluateAll: IsCompleted);
        var canNext = !IsCompleted && !IsLastStep && CanLeaveStep(CurrentStepIndex, validation);
        var canComplete = !IsCompleted && CanCompleteWith(validation);
        return new InspectionWorkflowSnapshot(outcome, CanMovePrevious, canNext, canComplete);
    }

    /// <summary>
    /// Advances to the next step if the current step satisfies all required-field,
    /// validation, and (when configured) pass-criteria gates.
    /// </summary>
    /// <returns><see langword="true"/> if navigation succeeded; otherwise <see langword="false"/>.</returns>
    public bool MoveNext()
    {
        if (IsCompleted || IsLastStep)
        {
            return false;
        }

        var validation = FormSession.Validate();
        if (!CanLeaveStep(CurrentStepIndex, validation))
        {
            return false;
        }

        _completedSteps.Add(CurrentStep.StepId);
        CurrentStepIndex++;
        _visitedSteps.Add(CurrentStep.StepId);
        StepChanged?.Invoke(this, CurrentStepIndex);
        return true;
    }

    /// <summary>Navigates back to the previous step.</summary>
    /// <returns><see langword="true"/> if navigation succeeded; otherwise <see langword="false"/>.</returns>
    public bool MovePrevious()
    {
        if (!CanMovePrevious)
        {
            return false;
        }

        CurrentStepIndex--;
        StepChanged?.Invoke(this, CurrentStepIndex);
        return true;
    }

    /// <summary>
    /// Jumps to <paramref name="stepIndex"/>. Moving backwards is always allowed
    /// while in progress; moving forward requires every intervening step to pass
    /// its leave gate before any step state is mutated.
    /// </summary>
    /// <param name="stepIndex">Zero-based target step index.</param>
    /// <returns><see langword="true"/> if navigation succeeded; otherwise <see langword="false"/>.</returns>
    public bool GoToStep(int stepIndex)
    {
        ValidateStepIndex(stepIndex);

        if (IsCompleted || stepIndex == CurrentStepIndex)
        {
            return false;
        }

        if (stepIndex > CurrentStepIndex)
        {
            var validation = FormSession.Validate();
            for (var i = CurrentStepIndex; i < stepIndex; i++)
            {
                if (!CanLeaveStep(i, validation, forceEvaluate: true))
                {
                    return false;
                }
            }

            for (var i = CurrentStepIndex; i < stepIndex; i++)
            {
                _visitedSteps.Add(Workflow.Steps[i].StepId);
                _completedSteps.Add(Workflow.Steps[i].StepId);
            }
        }

        CurrentStepIndex = stepIndex;
        _visitedSteps.Add(CurrentStep.StepId);
        StepChanged?.Invoke(this, CurrentStepIndex);
        return true;
    }

    /// <summary>
    /// Validates the entire form, enforces any blocking intermediate step gates,
    /// evaluates every step's pass/fail criteria, writes the optional
    /// <see cref="InspectionWorkflowDefinition.StatusOutputFieldId"/>, and
    /// submits the underlying record when valid.
    /// </summary>
    /// <param name="transitionTimeUtc">Optional submission timestamp.</param>
    /// <returns>The completion result with validation errors and pass/fail outcome.</returns>
    public InspectionCompletionResult Complete(DateTimeOffset? transitionTimeUtc = null)
    {
        var validation = FormSession.Validate();
        if (IsCompleted)
        {
            return new InspectionCompletionResult(
                false,
                validation,
                EvaluateOutcomeCore(validation, forceEvaluateAll: true));
        }

        if (!CanCompleteWith(validation))
        {
            var pendingOutcome = EvaluateOutcomeCore(validation, forceEvaluateAll: false);
            return new InspectionCompletionResult(false, validation, pendingOutcome);
        }

        var outcome = EvaluateOutcomeCore(validation, forceEvaluateAll: true);

        if (!string.IsNullOrWhiteSpace(Workflow.StatusOutputFieldId))
        {
            var statusValue = outcome.Status == InspectionStatus.Passed ? "passed" : "failed";
            if (FormSession.Fields.Any(f => string.Equals(f.FieldId, Workflow.StatusOutputFieldId, StringComparison.OrdinalIgnoreCase)))
            {
                FormSession.SetValue(Workflow.StatusOutputFieldId, statusValue);
            }
            else
            {
                FormSession.Record.Values[Workflow.StatusOutputFieldId] = statusValue;
            }
        }

        var submitValidation = FormSession.Submit(transitionTimeUtc);
        if (!submitValidation.IsValid)
        {
            return new InspectionCompletionResult(false, submitValidation, outcome);
        }

        foreach (var step in Workflow.Steps)
        {
            _visitedSteps.Add(step.StepId);
            _completedSteps.Add(step.StepId);
        }

        IsCompleted = true;
        var completion = new InspectionCompletionResult(true, submitValidation, outcome);
        Completed?.Invoke(this, completion);
        return completion;
    }

    private bool CanCompleteWith(FormValidationResult validation)
    {
        if (!validation.IsValid)
        {
            return false;
        }

        // Prevent bypassing a non-final step whose AllowAdvanceOnFail is false.
        for (var i = 0; i < Workflow.Steps.Count - 1; i++)
        {
            if (!CanLeaveStep(i, validation, forceEvaluate: true))
            {
                return false;
            }
        }

        return true;
    }

    private bool CanLeaveStep(int stepIndex, FormValidationResult validation, bool forceEvaluate = false)
    {
        var result = EvaluateStepCore(stepIndex, validation, forceEvaluate);
        if (!result.IsValid)
        {
            return false;
        }

        var step = Workflow.Steps[stepIndex];
        return step.AllowAdvanceOnFail || result.Status != InspectionStatus.Failed;
    }

    private InspectionStepResult EvaluateStepCore(
        int stepIndex,
        FormValidationResult validation,
        bool forceEvaluate = false)
    {
        var step = Workflow.Steps[stepIndex];
        var stepFieldIds = new HashSet<string>(
            GetFieldsForStep(step).Select(f => f.FieldId),
            StringComparer.OrdinalIgnoreCase);
        var stepSectionIds = new HashSet<string>(step.SectionIds, StringComparer.OrdinalIgnoreCase);

        var stepErrors = validation.Errors
            .Where(error => ErrorBelongsToStep(error.FieldId, stepFieldIds, stepSectionIds))
            .ToList();

        var isVisited = forceEvaluate || _visitedSteps.Contains(step.StepId) || _completedSteps.Contains(step.StepId);

        // Pass criteria only evaluate once the step has been visited and its inputs are valid,
        // so unvisited or incomplete steps never surface premature failures.
        if (!isVisited)
        {
            return new InspectionStepResult(
                step.StepId,
                step.Title,
                InspectionStatus.NotStarted,
                stepErrors.Count == 0,
                stepErrors,
                [],
                HasCriticalFailure: false);
        }

        if (stepErrors.Count > 0)
        {
            return new InspectionStepResult(
                step.StepId,
                step.Title,
                InspectionStatus.InProgress,
                IsValid: false,
                stepErrors,
                [],
                HasCriticalFailure: false);
        }

        var criteriaResults = step.PassCriteria
            .Select(criterion => EvaluateCriterion(criterion, FormSession.Record.Values))
            .ToList();

        var hasCriticalFailure = criteriaResults.Any(r => !r.Passed && r.IsCritical);
        var hasAnyFailure = criteriaResults.Any(r => !r.Passed);
        var status = hasAnyFailure ? InspectionStatus.Failed : InspectionStatus.Passed;

        return new InspectionStepResult(
            step.StepId,
            step.Title,
            status,
            IsValid: true,
            stepErrors,
            criteriaResults,
            hasCriticalFailure);
    }

    private InspectionOutcome EvaluateOutcomeCore(FormValidationResult validation, bool forceEvaluateAll)
    {
        var stepResults = new List<InspectionStepResult>(Workflow.Steps.Count);
        for (var i = 0; i < Workflow.Steps.Count; i++)
        {
            stepResults.Add(EvaluateStepCore(i, validation, forceEvaluateAll));
        }

        var passedCount = stepResults.Count(s => s.Status == InspectionStatus.Passed);
        var failedCount = stepResults.Count(s => s.Status == InspectionStatus.Failed);
        var hasCriticalFailure = stepResults.Any(s => s.HasCriticalFailure);
        var failedReasons = stepResults
            .SelectMany(s => s.CriteriaResults)
            .Where(c => !c.Passed)
            .Select(c => c.FailureReason ?? $"{c.Label} failed.")
            .ToList();

        InspectionStatus overall;
        if (hasCriticalFailure)
        {
            overall = InspectionStatus.Failed;
        }
        else if (forceEvaluateAll || stepResults.All(s => s.Status is InspectionStatus.Passed or InspectionStatus.Failed))
        {
            if (Workflow.RequireAllStepsPass)
            {
                overall = stepResults.All(s => s.Status == InspectionStatus.Passed)
                    ? InspectionStatus.Passed
                    : InspectionStatus.Failed;
            }
            else
            {
                overall = passedCount > 0 ? InspectionStatus.Passed : InspectionStatus.Failed;
            }
        }
        else
        {
            overall = InspectionStatus.InProgress;
        }

        return new InspectionOutcome(
            Workflow.WorkflowId,
            overall,
            passedCount,
            failedCount,
            Workflow.Steps.Count,
            hasCriticalFailure,
            stepResults,
            failedReasons);
    }

    private static InspectionCriterionResult EvaluateCriterion(
        InspectionCriterion criterion,
        IReadOnlyDictionary<string, object?> values)
    {
        var passed = !string.IsNullOrWhiteSpace(criterion.PassExpression) &&
            ExpressionEvaluator.EvaluateBoolean(criterion.PassExpression, values);

        return new InspectionCriterionResult(
            criterion.CriterionId,
            criterion.Label,
            passed,
            criterion.IsCritical,
            passed ? null : (criterion.FailureReason ?? $"{criterion.Label} failed."));
    }

    private static bool ErrorBelongsToStep(
        string errorFieldId,
        HashSet<string> stepFieldIds,
        HashSet<string> stepSectionIds)
    {
        if (stepFieldIds.Contains(errorFieldId) || stepSectionIds.Contains(errorFieldId))
        {
            return true;
        }

        // Repeat-row errors are keyed as "sectionId[index].fieldId".
        var bracketIndex = errorFieldId.IndexOf('[', StringComparison.Ordinal);
        if (bracketIndex > 0)
        {
            var sectionId = errorFieldId[..bracketIndex];
            return stepSectionIds.Contains(sectionId);
        }

        return false;
    }

    private void ValidateStepIndex(int stepIndex)
    {
        if (stepIndex < 0 || stepIndex >= Workflow.Steps.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stepIndex),
                stepIndex,
                $"Step index must be between 0 and {Workflow.Steps.Count - 1}.");
        }
    }
}
