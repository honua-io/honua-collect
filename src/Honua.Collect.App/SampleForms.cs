using Honua.Sdk.Field.Forms;

namespace Honua.Collect.App;

/// <summary>
/// Built-in sample forms so the app launches into a working capture experience.
/// In production these come from the server's form-package service; this keeps
/// the app self-contained for a first run / demo.
/// </summary>
public static class SampleForms
{
    /// <summary>A field asset inspection with conditional fields and a repeatable section.</summary>
    public static FormDefinition AssetInspection() => new()
    {
        FormId = "asset-inspection",
        Name = "Asset Inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "details",
                Label = "Details",
                Fields =
                [
                    new FormField { FieldId = "assetId", Label = "Asset ID", Type = FormFieldType.Text, Required = true },
                    new FormField
                    {
                        FieldId = "condition",
                        Label = "Condition",
                        Type = FormFieldType.SingleChoice,
                        Required = true,
                        Choices =
                        [
                            new FieldChoice { Value = "good", Label = "Good" },
                            new FieldChoice { Value = "fair", Label = "Fair" },
                            new FieldChoice { Value = "poor", Label = "Poor" },
                        ],
                    },
                    new FormField { FieldId = "serviceable", Label = "Serviceable?", Type = FormFieldType.YesNo },
                    new FormField
                    {
                        FieldId = "notes",
                        Label = "Notes",
                        Type = FormFieldType.Text,
                        Required = true,
                        VisibilityRule = new FieldVisibilityRule
                        {
                            DependsOnFieldId = "serviceable",
                            Operator = ComparisonOperator.Equals,
                            MatchValue = false,
                        },
                    },
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                ],
            },
            new FormSection
            {
                SectionId = "deficiencies",
                Label = "Deficiency",
                Repeatable = true,
                Fields =
                [
                    new FormField { FieldId = "kind", Label = "Kind", Type = FormFieldType.Text, Required = true },
                    new FormField { FieldId = "severity", Label = "Severity (1-5)", Type = FormFieldType.Numeric },
                ],
            },
        ],
    };
}
