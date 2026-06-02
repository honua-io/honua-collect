using Honua.Sdk.Field.Forms;

namespace Honua.Collect.App;

/// <summary>
/// Built-in sample forms so the app launches into a working capture experience.
/// In production these come from the server's form-package service; this keeps
/// the app self-contained for a first run / demo. Field ids map 1:1 to the
/// server's "Offline Field Sites" feature layer so a submission applies cleanly
/// via GeoServices applyEdits.
/// </summary>
public static class SampleForms
{
    /// <summary>A field-site inspection matching the server's editable layer schema.</summary>
    public static FormDefinition FieldSite() => new()
    {
        FormId = "field-site",
        Name = "Field Site",
        Sections =
        [
            new FormSection
            {
                SectionId = "details",
                Label = "Site details",
                Fields =
                [
                    new FormField { FieldId = "site_name", Label = "Site name", Type = FormFieldType.Text, Required = true },
                    new FormField
                    {
                        FieldId = "status",
                        Label = "Status",
                        Type = FormFieldType.SingleChoice,
                        Required = true,
                        Choices =
                        [
                            new FieldChoice { Value = "new", Label = "New" },
                            new FieldChoice { Value = "in_progress", Label = "In progress" },
                            new FieldChoice { Value = "done", Label = "Done" },
                        ],
                    },
                    new FormField
                    {
                        FieldId = "priority",
                        Label = "Priority",
                        Type = FormFieldType.SingleChoice,
                        Choices =
                        [
                            new FieldChoice { Value = "low", Label = "Low" },
                            new FieldChoice { Value = "high", Label = "High" },
                        ],
                    },
                    new FormField { FieldId = "assigned_to", Label = "Assigned to", Type = FormFieldType.Text },
                    new FormField { FieldId = "notes", Label = "Notes", Type = FormFieldType.Text },
                ],
            },
        ],
    };
}
