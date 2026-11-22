// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Inspections;

/// <summary>Evaluation status of an inspection step or overall inspection workflow.</summary>
public enum InspectionStatus
{
    /// <summary>The step or workflow has not been started yet.</summary>
    NotStarted = 0,

    /// <summary>Work is underway or required fields still have validation errors.</summary>
    InProgress = 1,

    /// <summary>All required inputs are valid and every pass criterion succeeded.</summary>
    Passed = 2,

    /// <summary>Inputs are valid, but one or more pass criteria failed.</summary>
    Failed = 3,
}

/// <summary>
/// A single pass/fail criterion evaluated against the captured record values
/// for an inspection step.
/// </summary>
public sealed record InspectionCriterion
{
    /// <summary>Stable identifier for the criterion.</summary>
    public required string CriterionId { get; init; }

    /// <summary>Human-readable label describing the check.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Boolean expression (for example <c>${pressure} &gt;= 40 and ${pressure} &lt;= 80</c>)
    /// that must evaluate to <see langword="true"/> for the criterion to pass.
    /// </summary>
    public required string PassExpression { get; init; }

    /// <summary>Optional explanation reported when the criterion fails.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// When <see langword="true"/>, failing this criterion flags the step and
    /// overall inspection with a critical failure.
    /// </summary>
    public bool IsCritical { get; init; }
}

/// <summary>Definition of one ordered step inside a multi-step inspection workflow.</summary>
public sealed record InspectionStepDefinition
{
    /// <summary>Stable step identifier.</summary>
    public required string StepId { get; init; }

    /// <summary>Display title shown to the inspector.</summary>
    public required string Title { get; init; }

    /// <summary>Optional instructions for this step.</summary>
    public string? Description { get; init; }

    /// <summary>Form section identifiers included in this step.</summary>
    public IReadOnlyList<string> SectionIds { get; init; } = [];

    /// <summary>Individual field identifiers included in this step.</summary>
    public IReadOnlyList<string> FieldIds { get; init; } = [];

    /// <summary>Pass/fail criteria evaluated once this step's fields are valid.</summary>
    public IReadOnlyList<InspectionCriterion> PassCriteria { get; init; } = [];

    /// <summary>
    /// Whether the inspector can advance past this step when its pass criteria
    /// fail (as long as all required fields and validation rules are satisfied).
    /// Defaults to <see langword="true"/> so inspectors can record all findings.
    /// </summary>
    public bool AllowAdvanceOnFail { get; init; } = true;
}

/// <summary>Definition of a multi-step inspection workflow layered over a form.</summary>
public sealed record InspectionWorkflowDefinition
{
    /// <summary>Stable workflow identifier.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>Display name of the inspection workflow.</summary>
    public required string Name { get; init; }

    /// <summary>Ordered steps the inspector progresses through.</summary>
    public required IReadOnlyList<InspectionStepDefinition> Steps { get; init; }

    /// <summary>
    /// When <see langword="true"/> (default), every step must pass for the overall
    /// inspection to pass. When <see langword="false"/>, the inspection passes if
    /// at least one step passes and no critical criterion fails.
    /// </summary>
    public bool RequireAllStepsPass { get; init; } = true;

    /// <summary>
    /// Optional field id on the underlying <see cref="Honua.Sdk.Field.Records.FieldRecord"/>
    /// where the final outcome (<c>"passed"</c> or <c>"failed"</c>) is written upon completion.
    /// </summary>
    public string? StatusOutputFieldId { get; init; }
}

/// <summary>Result of evaluating a single <see cref="InspectionCriterion"/>.</summary>
/// <param name="CriterionId">Criterion identifier.</param>
/// <param name="Label">Criterion label.</param>
/// <param name="Passed">Whether the criterion passed.</param>
/// <param name="IsCritical">Whether the criterion is critical.</param>
/// <param name="FailureReason">Failure explanation when <paramref name="Passed"/> is <see langword="false"/>.</param>
public sealed record InspectionCriterionResult(
    string CriterionId,
    string Label,
    bool Passed,
    bool IsCritical,
    string? FailureReason);

/// <summary>Evaluation snapshot of one step in an inspection workflow.</summary>
/// <param name="StepId">Step identifier.</param>
/// <param name="Title">Step display title.</param>
/// <param name="Status">Computed step status.</param>
/// <param name="IsValid">Whether all visible fields in this step passed validation.</param>
/// <param name="ValidationErrors">Validation errors scoped to this step.</param>
/// <param name="CriteriaResults">Evaluated pass/fail criteria for this step.</param>
/// <param name="HasCriticalFailure">Whether any critical criterion failed.</param>
public sealed record InspectionStepResult(
    string StepId,
    string Title,
    InspectionStatus Status,
    bool IsValid,
    IReadOnlyList<FormValidationError> ValidationErrors,
    IReadOnlyList<InspectionCriterionResult> CriteriaResults,
    bool HasCriticalFailure);

/// <summary>Aggregate evaluation snapshot across all steps of an inspection workflow.</summary>
/// <param name="WorkflowId">Workflow identifier.</param>
/// <param name="Status">Overall inspection status.</param>
/// <param name="PassedStepCount">Number of steps currently in <see cref="InspectionStatus.Passed"/>.</param>
/// <param name="FailedStepCount">Number of steps currently in <see cref="InspectionStatus.Failed"/>.</param>
/// <param name="TotalStepCount">Total number of steps in the workflow.</param>
/// <param name="HasCriticalFailure">Whether any step recorded a critical failure.</param>
/// <param name="StepResults">Per-step evaluation results in workflow order.</param>
/// <param name="FailedReasons">Human-readable failure reasons across all failed criteria.</param>
public sealed record InspectionOutcome(
    string WorkflowId,
    InspectionStatus Status,
    int PassedStepCount,
    int FailedStepCount,
    int TotalStepCount,
    bool HasCriticalFailure,
    IReadOnlyList<InspectionStepResult> StepResults,
    IReadOnlyList<string> FailedReasons);

/// <summary>Result returned by <see cref="InspectionWorkflowSession.Complete"/>.</summary>
/// <param name="Succeeded">Whether validation passed and the inspection was completed.</param>
/// <param name="Validation">Full form validation result.</param>
/// <param name="Outcome">Evaluated inspection outcome.</param>
public sealed record InspectionCompletionResult(
    bool Succeeded,
    FormValidationResult Validation,
    InspectionOutcome Outcome);

/// <summary>Single-pass evaluation snapshot for presentation bindings.</summary>
/// <param name="Outcome">Aggregate inspection outcome.</param>
/// <param name="CanMovePrevious">Whether moving to the previous step is permitted.</param>
/// <param name="CanMoveNext">Whether advancing to the next step is permitted.</param>
/// <param name="CanComplete">Whether completing the inspection is permitted.</param>
public sealed record InspectionWorkflowSnapshot(
    InspectionOutcome Outcome,
    bool CanMovePrevious,
    bool CanMoveNext,
    bool CanComplete);
