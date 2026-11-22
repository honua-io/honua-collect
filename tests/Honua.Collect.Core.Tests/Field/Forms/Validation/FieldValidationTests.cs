// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Field;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Field.Forms.Validation;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Forms.Validation;

public class FieldValidationTests
{
    private static FormDefinition HydrantForm() => new()
    {
        FormId = "hydrant-inspection",
        Name = "Hydrant Inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "main",
                Label = "Main",
                Fields =
                [
                    new FormField { FieldId = "assetCode", Label = "Asset Code", Type = FormFieldType.Text, Required = true },
                    new FormField { FieldId = "pressure", Label = "Pressure (PSI)", Type = FormFieldType.Numeric },
                    new FormField { FieldId = "condition", Label = "Condition", Type = FormFieldType.Text },
                    new FormField { FieldId = "repairNotes", Label = "Repair Notes", Type = FormFieldType.Text },
                    new FormField { FieldId = "photo", Label = "Defect Photo", Type = FormFieldType.Photo },
                    new FormField
                    {
                        FieldId = "hiddenNote",
                        Label = "Hidden Note",
                        Type = FormFieldType.Text,
                        VisibilityRule = new FieldVisibilityRule
                        {
                            DependsOnFieldId = "condition",
                            Operator = ComparisonOperator.Equals,
                            MatchValue = "critical",
                        },
                    },
                ],
            },
        ],
    };

    [Fact]
    public void Conditional_requiredness_activates_and_clears_live_in_session()
    {
        FormFieldValidationRule[] rules =
        [
            new FormFieldValidationRule
            {
                FieldId = "repairNotes",
                RequiredWhen = "${condition} = 'poor'",
                RequiredMessage = "Explain the required repair.",
            },
        ];

        var session = FormSession.CreateForNewRecord(HydrantForm(), "r1", validationRules: rules);
        session.SetValue("assetCode", "HYD-1001");
        session.SetValue("condition", "good");

        Assert.False(session.GetField("repairNotes").IsRequired);
        Assert.True(session.CanSubmit);

        session.SetValue("condition", "poor");

        Assert.True(session.GetField("repairNotes").IsRequired);
        Assert.False(session.CanSubmit);
        Assert.Contains("Explain the required repair.", session.GetField("repairNotes").Errors);

        session.SetValue("repairNotes", "Replace bonnet gasket");

        Assert.True(session.CanSubmit);
        Assert.Empty(session.GetField("repairNotes").Errors);
    }

    [Fact]
    public void Pattern_validation_enforces_regex_and_handles_invalid_pattern_safely()
    {
        FormFieldValidationRule[] rules =
        [
            new FormFieldValidationRule
            {
                FieldId = "assetCode",
                Pattern = "^HYD-[0-9]{4}$",
                PatternMessage = "Asset code must match HYD-0000.",
            },
            new FormFieldValidationRule
            {
                FieldId = "repairNotes",
                Pattern = "[unclosed",
            },
        ];

        var session = FormSession.CreateForNewRecord(HydrantForm(), "r2", validationRules: rules);
        session.SetValue("assetCode", "BAD-1");

        Assert.False(session.CanSubmit);
        Assert.Contains("Asset code must match HYD-0000.", session.GetField("assetCode").Errors);

        session.SetValue("assetCode", "HYD-2048");
        Assert.True(session.CanSubmit);

        session.SetValue("repairNotes", "anything");
        Assert.False(session.CanSubmit);
        Assert.Contains("could not be validated", session.GetField("repairNotes").Errors[0]);
    }

    [Fact]
    public void Range_validation_enforces_inclusive_exclusive_and_numeric_bounds()
    {
        FormFieldValidationRule[] rules =
        [
            new FormFieldValidationRule
            {
                FieldId = "pressure",
                MinValue = 20,
                MaxValue = 120,
                MinExclusive = true,
                MaxExclusive = false,
            },
        ];

        var session = FormSession.CreateForNewRecord(HydrantForm(), "r3", validationRules: rules);
        session.SetValue("assetCode", "HYD-3000");

        session.SetValue("pressure", "not-a-number");
        Assert.False(session.CanSubmit);
        Assert.Contains(session.GetField("pressure").Errors, e => e.Contains("valid number", StringComparison.Ordinal));

        session.SetValue("pressure", 20); // MinExclusive = true -> fails
        Assert.False(session.CanSubmit);
        Assert.Contains("between 20 and 120", session.GetField("pressure").Errors[0]);

        session.SetValue("pressure", 120); // MaxExclusive = false -> passes
        Assert.True(session.CanSubmit);

        session.SetValue("pressure", 120.1);
        Assert.False(session.CanSubmit);
    }

    [Fact]
    public void Length_and_when_guard_apply_only_when_guard_matches()
    {
        FormFieldValidationRule[] rules =
        [
            new FormFieldValidationRule
            {
                FieldId = "repairNotes",
                When = "${condition} = 'poor'",
                MinLength = 10,
                MaxLength = 40,
            },
        ];

        var session = FormSession.CreateForNewRecord(HydrantForm(), "r4", validationRules: rules);
        session.SetValue("assetCode", "HYD-4000");
        session.SetValue("condition", "good");
        session.SetValue("repairNotes", "ok");

        // Guard is false, so short note is allowed.
        Assert.True(session.CanSubmit);

        session.SetValue("condition", "poor");
        Assert.False(session.CanSubmit);
        Assert.Contains("between 10 and 40 characters", session.GetField("repairNotes").Errors[0]);

        session.SetValue("repairNotes", "Valve stem leaking at 45 PSI");
        Assert.True(session.CanSubmit);
    }

    [Fact]
    public void Media_field_conditional_requiredness_is_satisfied_by_captured_attachment()
    {
        FormFieldValidationRule[] rules =
        [
            new FormFieldValidationRule
            {
                FieldId = "photo",
                RequiredWhen = "${condition} = 'poor'",
            },
        ];

        var session = FormSession.CreateForNewRecord(HydrantForm(), "r5", validationRules: rules);
        session.SetValue("assetCode", "HYD-5000");
        session.SetValue("condition", "poor");

        Assert.True(session.GetField("photo").IsRequired);
        Assert.False(session.CanSubmit);

        session.AddMedia(new CapturedMediaAttachment
        {
            AttachmentId = "att-1",
            FieldId = "photo",
            LocalPath = "/tmp/defect.jpg",
            ContentType = "image/jpeg",
        });

        Assert.True(session.CanSubmit);
    }

    [Fact]
    public void Hidden_field_skips_rule_requiredness_and_static_required_is_not_duplicated()
    {
        FormFieldValidationRule[] rules =
        [
            new FormFieldValidationRule { FieldId = "assetCode", Required = true },
            new FormFieldValidationRule { FieldId = "hiddenNote", Required = true },
        ];

        var session = FormSession.CreateForNewRecord(HydrantForm(), "r6", validationRules: rules);

        // assetCode is statically required on FormField AND in rule -> only one error.
        Assert.Single(session.GetField("assetCode").Errors);
        // hiddenNote is hidden -> zero errors.
        Assert.Empty(session.GetField("hiddenNote").Errors);

        session.SetValue("assetCode", "HYD-6000");
        Assert.True(session.CanSubmit);
    }

    [Fact]
    public void Single_bound_range_and_length_default_messages_format_clearly()
    {
        FormFieldValidationRule[] rules =
        [
            new FormFieldValidationRule { FieldId = "pressure", MinValue = 10 },
            new FormFieldValidationRule { FieldId = "repairNotes", MaxLength = 5 },
        ];

        var session = FormSession.CreateForNewRecord(HydrantForm(), "r7", validationRules: rules);
        session.SetValue("assetCode", "HYD-7000");
        session.SetValue("pressure", 5);
        session.SetValue("repairNotes", "too long");

        Assert.Contains("at least 10", session.GetField("pressure").Errors[0]);
        Assert.Contains("at most 5", session.GetField("repairNotes").Errors[0]);

        var maxOnlySession = FormSession.CreateForNewRecord(
            HydrantForm(),
            "r8",
            validationRules:
            [
                new FormFieldValidationRule { FieldId = "pressure", MaxValue = 100, MaxExclusive = true },
                new FormFieldValidationRule { FieldId = "repairNotes", MinLength = 8 },
            ]);
        maxOnlySession.SetValue("assetCode", "HYD-8000");
        maxOnlySession.SetValue("pressure", 100);
        maxOnlySession.SetValue("repairNotes", "short");

        Assert.Contains("less than 100", maxOnlySession.GetField("pressure").Errors[0]);
        Assert.Contains("at least 8", maxOnlySession.GetField("repairNotes").Errors[0]);
    }

    [Fact]
    public void Open_applies_multiple_validation_rules_on_same_field_and_empty_enumerables()
    {
        var record = new FieldRecord { RecordId = "r9", FormId = "hydrant-inspection" };
        record.Values["assetCode"] = "bad";
        record.Values["repairNotes"] = new HashSet<string>();

        FormFieldValidationRule[] rules =
        [
            new FormFieldValidationRule { FieldId = "assetCode", Pattern = "^HYD-[0-9]{4}$", PatternMessage = "Bad prefix." },
            new FormFieldValidationRule { FieldId = "assetCode", MinLength = 8, LengthMessage = "Too short." },
            new FormFieldValidationRule { FieldId = "repairNotes", Required = true, RequiredMessage = "Need notes." },
        ];

        var session = FormSession.Open(HydrantForm(), record, validationRules: rules);

        Assert.Contains("Bad prefix.", session.GetField("assetCode").Errors);
        Assert.Contains("Too short.", session.GetField("assetCode").Errors);
        Assert.Contains("Need notes.", session.GetField("repairNotes").Errors);
    }
}
