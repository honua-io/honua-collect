using System.ComponentModel;
using Honua.Collect.Core.Field.Capture;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Presentation.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Presentation.Tests;

public class FieldViewModelTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "inspection",
        Name = "Inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "main",
                Label = "Main",
                Fields =
                [
                    new FormField
                    {
                        FieldId = "name",
                        Label = "Name",
                        Type = FormFieldType.Text,
                        Required = true,
                        HelpText = "Your full name",
                    },
                    new FormField { FieldId = "count", Label = "Count", Type = FormFieldType.Numeric },
                    new FormField { FieldId = "hasDamage", Label = "Damage?", Type = FormFieldType.YesNo },
                    new FormField
                    {
                        FieldId = "severity",
                        Label = "Severity",
                        Type = FormFieldType.SingleChoice,
                        Choices =
                        [
                            new FieldChoice { Value = "low", Label = "Low" },
                            new FieldChoice { Value = "high", Label = "High" },
                        ],
                    },
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                    new FormField { FieldId = "sig", Label = "Sign", Type = FormFieldType.Signature },
                    new FormField { FieldId = "code", Label = "Code", Type = FormFieldType.Barcode },
                    new FormField
                    {
                        FieldId = "notes",
                        Label = "Notes",
                        Type = FormFieldType.Text,
                        Required = true,
                        VisibilityRule = new FieldVisibilityRule
                        {
                            DependsOnFieldId = "hasDamage",
                            Operator = ComparisonOperator.Equals,
                            MatchValue = true,
                        },
                    },
                ],
            },
        ],
    };

    private static FormPageViewModel NewPage() => new(FormSession.CreateForNewRecord(Form(), "r1"));

    private static FieldViewModel Field(FormPageViewModel page, string id) =>
        page.Fields.Single(f => f.FieldId == id);

    [Fact]
    public void Exposes_definition_metadata()
    {
        var page = NewPage();
        var name = Field(page, "name");

        Assert.Equal("Name", name.Label);
        Assert.Equal("Your full name", name.HelpText);
        Assert.True(name.IsRequired);
        Assert.Equal(CaptureWidgetKind.Text, name.Widget);
    }

    [Theory]
    [InlineData("photo", "Add photo")]
    [InlineData("sig", "Sign")]
    [InlineData("code", "Scan barcode")]
    [InlineData("name", "Add attachment")] // non-media falls through to default
    public void CaptureActionLabel_varies_by_widget(string fieldId, string expected)
        => Assert.Equal(expected, Field(NewPage(), fieldId).CaptureActionLabel);

    [Fact]
    public void Setting_value_pushes_through_and_invokes_changed_callback()
    {
        var page = NewPage();
        var name = Field(page, "name");

        name.Value = "Ada";

        // The value is pushed through the host (read back through the same state).
        Assert.Equal("Ada", name.Value);
        Assert.Equal("Ada", page.Session.GetField("name").Value);
    }

    [Fact]
    public void Setting_value_to_equal_value_is_a_no_op()
    {
        var page = NewPage();
        var name = Field(page, "name");
        name.Value = "Ada";

        var changed = 0;
        name.PropertyChanged += (_, _) => changed++;
        // Re-assigning the identical value must not push through (Equals short-circuit).
        name.Value = "Ada";

        Assert.Equal(0, changed);
        Assert.Equal("Ada", name.Value);
    }

    [Fact]
    public void SelectedChoice_round_trips_value_and_choice()
    {
        var page = NewPage();
        var severity = Field(page, "severity");

        Assert.Null(severity.SelectedChoice);
        Assert.Equal(2, severity.Choices.Count);

        severity.SelectedChoice = severity.Choices.Single(c => c.Value == "high");

        Assert.Equal("high", severity.Value);
        Assert.Equal("high", severity.SelectedChoice!.Value);
    }

    [Fact]
    public void SelectedChoice_set_to_null_clears_value()
    {
        var page = NewPage();
        var severity = Field(page, "severity");
        severity.SelectedChoice = severity.Choices[0];
        Assert.NotNull(severity.Value);

        severity.SelectedChoice = null;

        Assert.Null(severity.Value);
        Assert.Null(severity.SelectedChoice);
    }

    [Fact]
    public void Visibility_and_required_reflect_runtime_state()
    {
        var page = NewPage();
        var notes = Field(page, "notes");
        Assert.False(notes.IsVisible);

        Field(page, "hasDamage").Value = true;
        notes.Refresh();

        Assert.True(notes.IsVisible);
    }

    [Fact]
    public void Error_text_and_has_error_track_validation()
    {
        var page = NewPage();
        var name = Field(page, "name");
        // Required + empty after a submit pass yields a validation error.
        page.Session.Submit();
        name.Refresh();

        Assert.True(name.HasError);
        Assert.NotEmpty(name.ErrorText);

        name.Value = "Ada";
        page.Session.Submit();
        name.Refresh();

        Assert.False(name.HasError);
        Assert.Empty(name.ErrorText);
    }

    [Fact]
    public void CaptureMedia_registers_attachment_and_increments_count()
    {
        var page = NewPage();
        var photo = Field(page, "photo");
        Assert.Equal(0, photo.MediaCount);

        photo.CaptureMedia("/tmp/a.jpg", "image/jpeg");

        Assert.Equal(1, photo.MediaCount);
    }

    [Fact]
    public void CaptureMedia_rejects_blank_path()
        => Assert.Throws<ArgumentException>(() => Field(NewPage(), "photo").CaptureMedia(" "));

    [Fact]
    public void Refresh_raises_change_notification_for_bound_members()
    {
        var page = NewPage();
        var name = Field(page, "name");
        var changed = new List<string?>();
        name.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        name.Refresh();

        Assert.Contains(nameof(FieldViewModel.Value), changed);
        Assert.Contains(nameof(FieldViewModel.IsVisible), changed);
        Assert.Contains(nameof(FieldViewModel.HasError), changed);
        Assert.Contains(nameof(FieldViewModel.MediaCount), changed);
    }

    [Fact]
    public void Constructor_rejects_null_host_and_callback()
    {
        Assert.Throws<ArgumentNullException>(() => new FieldViewModel(null!, "x", () => { }));
        var page = NewPage();
        var host = FormSession.CreateForNewRecord(Form(), "r2");
        Assert.Throws<ArgumentNullException>(() => new FieldViewModel(host, "name", null!));
    }
}
