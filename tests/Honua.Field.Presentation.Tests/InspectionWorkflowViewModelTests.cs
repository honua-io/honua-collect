// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Field.Forms;
using Honua.Field.Core.Field.Forms.Validation;
using Honua.Field.Core.Field.Inspections;
using Honua.Field.Presentation.Forms;
using Honua.Field.Presentation.Inspections;
using Honua.Sdk.Field.Forms;

namespace Honua.Field.Presentation.Tests;

public class InspectionWorkflowViewModelTests
{
    private static InspectionWorkflowViewModel CreateViewModel()
    {
        var form = new FormDefinition
        {
            FormId = "valve-inspection",
            Name = "Valve Inspection",
            Sections =
            [
                new FormSection
                {
                    SectionId = "id",
                    Label = "Identification",
                    Fields =
                    [
                        new FormField { FieldId = "valveId", Label = "Valve ID", Type = FormFieldType.Text, Required = true },
                    ],
                },
                new FormSection
                {
                    SectionId = "checks",
                    Label = "Checks",
                    Fields =
                    [
                        new FormField { FieldId = "leaking", Label = "Leaking?", Type = FormFieldType.YesNo, Required = true },
                        new FormField { FieldId = "leakNotes", Label = "Leak Notes", Type = FormFieldType.Text },
                    ],
                },
            ],
        };

        FormFieldValidationRule[] rules =
        [
            new FormFieldValidationRule { FieldId = "valveId", Pattern = "^VLV-[0-9]{3}$" },
            new FormFieldValidationRule { FieldId = "leakNotes", RequiredWhen = "${leaking} = true" },
        ];

        var workflow = new InspectionWorkflowDefinition
        {
            WorkflowId = "valve-wf",
            Name = "Valve Inspection Workflow",
            Steps =
            [
                new InspectionStepDefinition
                {
                    StepId = "s1",
                    Title = "Identify Valve",
                    Description = "Enter the stamped valve identifier.",
                    SectionIds = ["id"],
                },
                new InspectionStepDefinition
                {
                    StepId = "s2",
                    Title = "Leak Check",
                    SectionIds = ["checks"],
                    PassCriteria =
                    [
                        new InspectionCriterion
                        {
                            CriterionId = "no-leak",
                            Label = "No active leak",
                            PassExpression = "${leaking} = false",
                            FailureReason = "Valve has an active leak.",
                        },
                    ],
                },
            ],
        };

        var formSession = FormSession.CreateForNewRecord(form, "rec-1", validationRules: rules);
        return new InspectionWorkflowViewModel(new InspectionWorkflowSession(formSession, workflow));
    }

    [Fact]
    public void Step_progression_updates_fields_commands_and_completes_passing_inspection()
    {
        var vm = CreateViewModel();

        Assert.Equal("Valve Inspection Workflow", vm.Title);
        Assert.Equal("Identify Valve", vm.CurrentStepTitle);
        Assert.Equal("Step 1 of 2", vm.StepProgressText);
        Assert.False(vm.CanMovePrevious);
        Assert.False(vm.CanMoveNext);
        Assert.NotEmpty(vm.ValidationSummary);

        vm.CurrentStepFields.Single(f => f.FieldId == "valveId").Value = "VLV-101";

        Assert.True(vm.CanMoveNext);
        Assert.Equal(string.Empty, vm.ValidationSummary);
        Assert.Equal(InspectionStatus.Passed, vm.CurrentStepStatus);

        vm.NextStepCommand.Execute(null);

        Assert.Equal("Leak Check", vm.CurrentStepTitle);
        Assert.Equal("Step 2 of 2", vm.StepProgressText);
        Assert.True(vm.IsLastStep);
        Assert.True(vm.CanMovePrevious);
        Assert.False(vm.CanComplete);

        vm.CurrentStepFields.Single(f => f.FieldId == "leaking").Value = false;

        Assert.True(vm.CanComplete);
        Assert.Equal(InspectionStatus.Passed, vm.CurrentStepStatus);

        InspectionCompletionResult? completed = null;
        vm.InspectionCompleted += (_, r) => completed = r;

        vm.CompleteCommand.Execute(null);

        Assert.True(vm.IsCompleted);
        Assert.Equal(InspectionStatus.Passed, vm.OverallStatus);
        Assert.NotNull(completed);
        Assert.True(completed!.Succeeded);
    }

    [Fact]
    public void Conditional_requiredness_notifies_field_vm_and_tracks_failure_summary()
    {
        var vm = CreateViewModel();
        vm.CurrentStepFields.Single(f => f.FieldId == "valveId").Value = "VLV-202";
        vm.NextStepCommand.Execute(null);

        var leakNotes = vm.CurrentStepFields.Single(f => f.FieldId == "leakNotes");
        var changedProps = new List<string?>();
        leakNotes.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        Assert.False(leakNotes.IsRequired);

        vm.CurrentStepFields.Single(f => f.FieldId == "leaking").Value = true;

        Assert.True(leakNotes.IsRequired);
        Assert.Contains(nameof(FieldViewModel.IsRequired), changedProps);
        Assert.False(vm.CanComplete);

        leakNotes.Value = "Drip at packing nut";

        Assert.True(vm.CanComplete);
        Assert.Equal(InspectionStatus.Failed, vm.CurrentStepStatus);
        Assert.Contains("Valve has an active leak.", vm.FailureSummary);

        // Previous command works before completion.
        vm.PreviousStepCommand.Execute(null);
        Assert.Equal(1, vm.CurrentStepNumber);
        vm.NextStepCommand.Execute(null);

        vm.CompleteCommand.Execute(null);

        Assert.True(vm.IsCompleted);
        Assert.Equal(InspectionStatus.Failed, vm.OverallStatus);
        Assert.False(vm.CanMovePrevious);
    }

    [Fact]
    public void Field_edits_preserve_CurrentStepFields_collection_reference()
    {
        var vm = CreateViewModel();
        var initialFields = vm.CurrentStepFields;

        vm.CurrentStepFields.Single(f => f.FieldId == "valveId").Value = "VLV-303";

        Assert.Same(initialFields, vm.CurrentStepFields);
        Assert.Equal(InspectionStatus.InProgress, vm.OverallStatus);
        Assert.Equal(string.Empty, vm.FailureSummary);
    }
}
