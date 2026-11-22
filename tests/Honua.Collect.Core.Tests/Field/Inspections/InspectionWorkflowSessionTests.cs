// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Field.Forms.Validation;
using Honua.Collect.Core.Field.Inspections;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Inspections;

public class InspectionWorkflowSessionTests
{
    private static FormDefinition InspectionForm() => new()
    {
        FormId = "bridge-inspection",
        Name = "Bridge Inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "setup",
                Label = "Asset Identification",
                Fields =
                [
                    new FormField { FieldId = "assetId", Label = "Asset ID", Type = FormFieldType.Text, Required = true },
                ],
            },
            new FormSection
            {
                SectionId = "structural",
                Label = "Structural Check",
                Fields =
                [
                    new FormField { FieldId = "loadRating", Label = "Load Rating", Type = FormFieldType.Numeric, Required = true },
                    new FormField { FieldId = "hasCracks", Label = "Visible Cracks", Type = FormFieldType.YesNo, Required = true },
                    new FormField { FieldId = "crackNotes", Label = "Crack Notes", Type = FormFieldType.Text },
                ],
            },
            new FormSection
            {
                SectionId = "signoff",
                Label = "Sign-off",
                Fields =
                [
                    new FormField { FieldId = "inspector", Label = "Inspector", Type = FormFieldType.Text, Required = true },
                    new FormField { FieldId = "inspection_status", Label = "Status", Type = FormFieldType.Text },
                ],
            },
        ],
    };

    private static FormFieldValidationRule[] Rules() =>
    [
        new FormFieldValidationRule { FieldId = "assetId", Pattern = "^BR-[0-9]{3}$" },
        new FormFieldValidationRule { FieldId = "loadRating", MinValue = 0, MaxValue = 100 },
        new FormFieldValidationRule { FieldId = "crackNotes", RequiredWhen = "${hasCracks} = true" },
    ];

    private static InspectionWorkflowDefinition Workflow(
        bool allowAdvanceOnFail = true,
        bool requireAllStepsPass = true) => new()
    {
        WorkflowId = "bridge-wf",
        Name = "Annual Bridge Inspection",
        RequireAllStepsPass = requireAllStepsPass,
        StatusOutputFieldId = "inspection_status",
        Steps =
        [
            new InspectionStepDefinition
            {
                StepId = "step-1",
                Title = "Identify Asset",
                SectionIds = ["setup"],
            },
            new InspectionStepDefinition
            {
                StepId = "step-2",
                Title = "Structural Checks",
                SectionIds = ["structural"],
                AllowAdvanceOnFail = allowAdvanceOnFail,
                PassCriteria =
                [
                    new InspectionCriterion
                    {
                        CriterionId = "c-load",
                        Label = "Minimum load rating",
                        PassExpression = "${loadRating} >= 60",
                        FailureReason = "Load rating below 60.",
                        IsCritical = true,
                    },
                    new InspectionCriterion
                    {
                        CriterionId = "c-cracks",
                        Label = "No structural cracks",
                        PassExpression = "${hasCracks} = false",
                        FailureReason = "Visible structural cracks found.",
                    },
                ],
            },
            new InspectionStepDefinition
            {
                StepId = "step-3",
                Title = "Inspector Sign-off",
                SectionIds = ["signoff"],
            },
        ],
    };

    [Fact]
    public void Step_navigation_enforces_validation_and_conditional_requiredness_gates()
    {
        var formSession = FormSession.CreateForNewRecord(InspectionForm(), "insp-1", validationRules: Rules());
        var workflow = new InspectionWorkflowSession(formSession, Workflow());

        // Step 1 invalid (assetId missing) -> cannot advance.
        Assert.False(workflow.CanMoveNext);
        Assert.False(workflow.MoveNext());
        Assert.Equal(InspectionStatus.InProgress, workflow.EvaluateStep(0).Status);
        Assert.Equal(InspectionStatus.NotStarted, workflow.EvaluateStep(1).Status);

        // Invalid pattern still blocks.
        formSession.SetValue("assetId", "BAD");
        Assert.False(workflow.MoveNext());

        // Valid assetId unlocks Step 2.
        formSession.SetValue("assetId", "BR-101");
        Assert.True(workflow.CanMoveNext);
        Assert.True(workflow.MoveNext());
        Assert.Equal(1, workflow.CurrentStepIndex);

        // Step 2: hasCracks = true makes crackNotes conditionally required -> blocks advance.
        formSession.SetValue("loadRating", 75);
        formSession.SetValue("hasCracks", true);
        Assert.False(workflow.CanMoveNext);
        Assert.False(workflow.MoveNext());

        // Filling crackNotes satisfies validation; AllowAdvanceOnFail=true lets inspector continue.
        formSession.SetValue("crackNotes", "Hairline crack on pier 2");
        Assert.True(workflow.CanMoveNext);
        Assert.Equal(InspectionStatus.Failed, workflow.EvaluateStep(1).Status);
        Assert.True(workflow.MoveNext());
        Assert.Equal(2, workflow.CurrentStepIndex);
    }

    [Fact]
    public void AllowAdvanceOnFail_false_blocks_leaving_a_failed_step()
    {
        var formSession = FormSession.CreateForNewRecord(InspectionForm(), "insp-2", validationRules: Rules());
        var workflow = new InspectionWorkflowSession(formSession, Workflow(allowAdvanceOnFail: false));

        formSession.SetValue("assetId", "BR-202");
        Assert.True(workflow.MoveNext());

        formSession.SetValue("loadRating", 40); // fails critical load criterion
        formSession.SetValue("hasCracks", false);

        Assert.False(workflow.CanMoveNext);
        Assert.False(workflow.MoveNext());
        Assert.Equal(InspectionStatus.Failed, workflow.EvaluateOutcome().Status);
        Assert.True(workflow.EvaluateOutcome().HasCriticalFailure);
    }

    [Fact]
    public void Complete_records_passed_outcome_and_submits_record()
    {
        var formSession = FormSession.CreateForNewRecord(InspectionForm(), "insp-3", validationRules: Rules());
        var workflow = new InspectionWorkflowSession(formSession, Workflow());

        formSession.SetValue("assetId", "BR-303");
        Assert.True(workflow.MoveNext());

        formSession.SetValue("loadRating", 85);
        formSession.SetValue("hasCracks", false);
        Assert.True(workflow.MoveNext());

        formSession.SetValue("inspector", "J. Rivera");

        InspectionCompletionResult? eventResult = null;
        workflow.Completed += (_, r) => eventResult = r;

        var result = workflow.Complete();

        Assert.True(result.Succeeded);
        Assert.True(workflow.IsCompleted);
        Assert.Equal(InspectionStatus.Passed, result.Outcome.Status);
        Assert.Equal(3, result.Outcome.PassedStepCount);
        Assert.Equal(0, result.Outcome.FailedStepCount);
        Assert.Empty(result.Outcome.FailedReasons);
        Assert.Equal("passed", formSession.Record.Values["inspection_status"]);
        Assert.Equal(RecordStatus.Submitted, formSession.Record.Status);
        Assert.Same(result, eventResult);
    }

    [Fact]
    public void Complete_records_failed_outcome_when_valid_inputs_fail_criteria()
    {
        var formSession = FormSession.CreateForNewRecord(InspectionForm(), "insp-4", validationRules: Rules());
        var workflow = new InspectionWorkflowSession(formSession, Workflow());

        formSession.SetValue("assetId", "BR-404");
        formSession.SetValue("loadRating", 50);
        formSession.SetValue("hasCracks", true);
        formSession.SetValue("crackNotes", "Spalling on abutment");
        formSession.SetValue("inspector", "A. Patel");

        var result = workflow.Complete();

        Assert.True(result.Succeeded);
        Assert.Equal(InspectionStatus.Failed, result.Outcome.Status);
        Assert.True(result.Outcome.HasCriticalFailure);
        Assert.Equal(2, result.Outcome.FailedReasons.Count);
        Assert.Equal("failed", formSession.Record.Values["inspection_status"]);
        Assert.Equal(RecordStatus.Submitted, formSession.Record.Status);
    }

    [Fact]
    public void Complete_rejects_when_any_required_field_is_missing()
    {
        var formSession = FormSession.CreateForNewRecord(InspectionForm(), "insp-5", validationRules: Rules());
        var workflow = new InspectionWorkflowSession(formSession, Workflow());

        formSession.SetValue("assetId", "BR-505");

        var result = workflow.Complete();

        Assert.False(result.Succeeded);
        Assert.False(workflow.IsCompleted);
        Assert.NotEmpty(result.Validation.Errors);
        Assert.Equal(RecordStatus.Draft, formSession.Record.Status);
    }

    [Fact]
    public void GoToStep_and_MovePrevious_respect_step_gates_and_bounds()
    {
        var formSession = FormSession.CreateForNewRecord(InspectionForm(), "insp-6", validationRules: Rules());
        var workflow = new InspectionWorkflowSession(formSession, Workflow());

        Assert.False(workflow.MovePrevious());
        Assert.False(workflow.GoToStep(2)); // Step 1 & 2 incomplete.

        formSession.SetValue("assetId", "BR-606");
        formSession.SetValue("loadRating", 80);
        formSession.SetValue("hasCracks", false);

        // Intervening steps 0 and 1 are valid -> jump straight to step 2.
        Assert.True(workflow.GoToStep(2));
        Assert.Equal(2, workflow.CurrentStepIndex);

        // Backwards navigation always works while in progress.
        Assert.True(workflow.MovePrevious());
        Assert.Equal(1, workflow.CurrentStepIndex);
        Assert.True(workflow.GoToStep(0));
        Assert.Equal(0, workflow.CurrentStepIndex);

        Assert.Throws<ArgumentOutOfRangeException>(() => workflow.GoToStep(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => workflow.EvaluateStep(99));
    }

    [Fact]
    public void Constructor_rejects_empty_steps()
    {
        var formSession = FormSession.CreateForNewRecord(InspectionForm(), "insp-7");
        var emptyWorkflow = new InspectionWorkflowDefinition
        {
            WorkflowId = "empty",
            Name = "Empty",
            Steps = [],
        };

        Assert.Throws<ArgumentException>(() => new InspectionWorkflowSession(formSession, emptyWorkflow));
    }

    [Fact]
    public void Unvisited_and_invalid_steps_do_not_taint_initial_outcome_with_premature_failures()
    {
        var formSession = FormSession.CreateForNewRecord(InspectionForm(), "insp-8", validationRules: Rules());
        var workflow = new InspectionWorkflowSession(formSession, Workflow());

        var initial = workflow.EvaluateOutcome();

        Assert.Equal(InspectionStatus.InProgress, initial.Status);
        Assert.False(initial.HasCriticalFailure);
        Assert.Empty(initial.FailedReasons);
        Assert.Equal(InspectionStatus.NotStarted, initial.StepResults[1].Status);
        Assert.Empty(initial.StepResults[1].CriteriaResults);
    }

    [Fact]
    public void Complete_is_idempotent_and_respects_blocking_intermediate_step_gate()
    {
        var formSession = FormSession.CreateForNewRecord(InspectionForm(), "insp-9", validationRules: Rules());
        var blockingWorkflow = new InspectionWorkflowSession(formSession, Workflow(allowAdvanceOnFail: false));

        formSession.SetValue("assetId", "BR-909");
        formSession.SetValue("loadRating", 40); // fails blocking step 2 criterion
        formSession.SetValue("hasCracks", false);
        formSession.SetValue("inspector", "M. Chen");

        Assert.False(blockingWorkflow.CanComplete);
        Assert.False(blockingWorkflow.Complete().Succeeded);
        Assert.False(blockingWorkflow.IsCompleted);

        // Fix loadRating -> completes once, second Complete() is rejected cleanly.
        formSession.SetValue("loadRating", 80);
        Assert.True(blockingWorkflow.CanComplete);
        Assert.True(blockingWorkflow.Complete().Succeeded);

        var secondAttempt = blockingWorkflow.Complete();
        Assert.False(secondAttempt.Succeeded);
        Assert.Equal(InspectionStatus.Passed, secondAttempt.Outcome.Status);
    }

    [Fact]
    public void GoToStep_partial_failure_leaves_intervening_steps_unmutated()
    {
        var formSession = FormSession.CreateForNewRecord(InspectionForm(), "insp-10", validationRules: Rules());
        var workflow = new InspectionWorkflowSession(formSession, Workflow());

        // Step 0 is valid, but Step 1 is still incomplete.
        formSession.SetValue("assetId", "BR-100");
        Assert.False(workflow.GoToStep(2));

        // Step 1 must remain NotStarted because the jump failed.
        Assert.Equal(0, workflow.CurrentStepIndex);
        Assert.Equal(InspectionStatus.NotStarted, workflow.EvaluateStep(1).Status);
    }

    [Fact]
    public void RequireAllStepsPass_false_FieldIds_and_repeat_section_errors_work()
    {
        var form = new FormDefinition
        {
            FormId = "repeat-insp",
            Name = "Repeat Inspection",
            Sections =
            [
                new FormSection
                {
                    SectionId = "general",
                    Label = "General",
                    Fields =
                    [
                        new FormField { FieldId = "code", Label = "Code", Type = FormFieldType.Text, Required = true },
                        new FormField { FieldId = "ok", Label = "OK", Type = FormFieldType.YesNo, Required = true },
                    ],
                },
                new FormSection
                {
                    SectionId = "defects",
                    Label = "Defects",
                    Repeatable = true,
                    Fields = [new FormField { FieldId = "desc", Label = "Description", Type = FormFieldType.Text, Required = true }],
                },
            ],
        };

        var bounds = new Dictionary<string, RepeatBounds> { ["defects"] = new(Min: 1, Max: 5) };
        var session = FormSession.CreateForNewRecord(form, "insp-11", repeatBounds: bounds);
        var wfDef = new InspectionWorkflowDefinition
        {
            WorkflowId = "partial-pass",
            Name = "Partial Pass",
            RequireAllStepsPass = false,
            Steps =
            [
                new InspectionStepDefinition
                {
                    StepId = "s1",
                    Title = "Check",
                    FieldIds = ["code", "ok"],
                    PassCriteria =
                    [
                        new InspectionCriterion { CriterionId = "c1", Label = "OK", PassExpression = "${ok} = true" },
                    ],
                },
                new InspectionStepDefinition
                {
                    StepId = "s2",
                    Title = "Defects",
                    SectionIds = ["defects"],
                },
            ],
        };

        var wf = new InspectionWorkflowSession(session, wfDef);
        session.SetValue("code", "A-1");
        session.SetValue("ok", false); // non-critical failure on step 1
        Assert.True(wf.MoveNext());

        // Step 2 has repeat min=1 violation attributed to it.
        Assert.False(wf.EvaluateStep(1).IsValid);
        Assert.NotEmpty(wf.EvaluateStep(1).ValidationErrors);

        var row = session.AddRepeatInstance("defects");
        Assert.False(wf.EvaluateStep(1).IsValid); // row desc required

        row.SetValue("desc", "Rust on flange");
        Assert.True(wf.EvaluateStep(1).IsValid);

        // Step 1 failed (non-critical), Step 2 passed, RequireAllStepsPass = false -> Passed!
        var result = wf.Complete();
        Assert.True(result.Succeeded);
        Assert.Equal(InspectionStatus.Passed, result.Outcome.Status);
    }
}
