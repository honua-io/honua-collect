using Honua.Collect.Core.Field;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Forms;

public class FormSessionTests
{
    private static FormDefinition SampleForm() => new()
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
                    new FormField { FieldId = "name", Label = "Name", Type = FormFieldType.Text, Required = true },
                    new FormField
                    {
                        FieldId = "hasDamage",
                        Label = "Has damage?",
                        Type = FormFieldType.YesNo,
                    },
                    new FormField
                    {
                        FieldId = "damageNotes",
                        Label = "Damage notes",
                        Type = FormFieldType.Text,
                        Required = true,
                        VisibilityRule = new FieldVisibilityRule
                        {
                            DependsOnFieldId = "hasDamage",
                            Operator = ComparisonOperator.Equals,
                            MatchValue = true,
                        },
                    },
                    new FormField
                    {
                        FieldId = "severity",
                        Label = "Severity",
                        Type = FormFieldType.Numeric,
                        Required = true,
                        // Chained: only visible when damageNotes is visible AND has a value.
                        VisibilityRule = new FieldVisibilityRule
                        {
                            DependsOnFieldId = "damageNotes",
                            Operator = ComparisonOperator.NotEquals,
                            MatchValue = null,
                        },
                    },
                ],
            },
        ],
    };

    [Fact]
    public void New_record_is_a_draft_bound_to_the_form()
    {
        var session = FormSession.CreateForNewRecord(SampleForm(), "r1");

        Assert.Equal("r1", session.Record.RecordId);
        Assert.Equal("inspection", session.Record.FormId);
        Assert.Equal(RecordStatus.Draft, session.Record.Status);
    }

    [Fact]
    public void Conditional_field_is_hidden_until_its_controller_matches()
    {
        var session = FormSession.CreateForNewRecord(SampleForm(), "r1");

        Assert.False(session.GetField("damageNotes").IsVisible);

        session.SetValue("hasDamage", true);

        Assert.True(session.GetField("damageNotes").IsVisible);
    }

    [Fact]
    public void Visibility_cascades_through_dependency_chains()
    {
        var session = FormSession.CreateForNewRecord(SampleForm(), "r1");

        // severity depends on damageNotes, which depends on hasDamage. While
        // hasDamage is unset, both downstream fields stay hidden.
        Assert.False(session.GetField("damageNotes").IsVisible);
        Assert.False(session.GetField("severity").IsVisible);

        session.SetValue("hasDamage", true);
        // damageNotes now visible, but still has no value -> severity hidden.
        Assert.True(session.GetField("damageNotes").IsVisible);
        Assert.False(session.GetField("severity").IsVisible);

        session.SetValue("damageNotes", "cracked frame");
        Assert.True(session.GetField("severity").IsVisible);
    }

    [Fact]
    public void Hidden_required_fields_do_not_block_submission()
    {
        var session = FormSession.CreateForNewRecord(SampleForm(), "r1");
        session.SetValue("name", "Bridge 7");

        // damageNotes and severity are required but hidden -> form is submittable.
        Assert.True(session.CanSubmit);
    }

    [Fact]
    public void Revealed_required_field_blocks_submission_until_filled()
    {
        var session = FormSession.CreateForNewRecord(SampleForm(), "r1");
        session.SetValue("name", "Bridge 7");
        session.SetValue("hasDamage", true);

        Assert.False(session.CanSubmit);
        Assert.Contains(session.GetField("damageNotes").Errors, m => m.Contains("required"));

        session.SetValue("damageNotes", "cracked frame");
        session.SetValue("severity", 3);

        Assert.True(session.CanSubmit);
    }

    [Fact]
    public void Submit_transitions_only_when_valid()
    {
        var session = FormSession.CreateForNewRecord(SampleForm(), "r1");

        var blocked = session.Submit();
        Assert.False(blocked.IsValid);
        Assert.Equal(RecordStatus.Draft, session.Record.Status);

        session.SetValue("name", "Bridge 7");
        var ok = session.Submit();

        Assert.True(ok.IsValid);
        Assert.Equal(RecordStatus.Submitted, session.Record.Status);
        Assert.NotNull(session.Record.SubmittedAtUtc);
    }

    [Fact]
    public void Calculated_fields_recompute_on_value_change()
    {
        var form = new FormDefinition
        {
            FormId = "f",
            Name = "f",
            Sections =
            [
                new FormSection
                {
                    SectionId = "s",
                    Label = "s",
                    Fields =
                    [
                        new FormField { FieldId = "first", Label = "First", Type = FormFieldType.Text },
                        new FormField { FieldId = "last", Label = "Last", Type = FormFieldType.Text },
                        new FormField
                        {
                            FieldId = "full",
                            Label = "Full",
                            Type = FormFieldType.Calculated,
                            CalculatedExpression = "concat($first, ' ', $last)",
                        },
                    ],
                },
            ],
        };

        var session = FormSession.CreateForNewRecord(form, "r1");
        session.SetValue("first", "Ada");
        session.SetValue("last", "Lovelace");

        Assert.Equal("Ada Lovelace", session.GetValue("full"));
    }

    [Fact]
    public void SetValue_raises_FieldChanged_only_on_actual_change()
    {
        var session = FormSession.CreateForNewRecord(SampleForm(), "r1");
        var changes = new List<string>();
        session.FieldChanged += (_, e) => changes.Add(e.FieldId);

        session.SetValue("name", "A");
        session.SetValue("name", "A"); // no-op
        session.SetValue("name", "B");

        Assert.Equal(["name", "name"], changes);
    }

    [Fact]
    public void Media_is_counted_for_validation_and_mirrored_into_the_record()
    {
        var form = new FormDefinition
        {
            FormId = "f",
            Name = "f",
            Sections =
            [
                new FormSection
                {
                    SectionId = "s",
                    Label = "s",
                    Fields =
                    [
                        new FormField
                        {
                            FieldId = "photo",
                            Label = "Photo",
                            Type = FormFieldType.Photo,
                            Required = true,
                            Validation = new FieldValidationRule { MinMediaCount = 1 },
                        },
                    ],
                },
            ],
        };

        var session = FormSession.CreateForNewRecord(form, "r1");
        Assert.False(session.CanSubmit);

        session.AddMedia(new CapturedMediaAttachment
        {
            AttachmentId = "a1",
            FieldId = "photo",
            LocalPath = "/tmp/p.jpg",
            MediaType = FieldMediaType.Photo,
        });

        Assert.True(session.CanSubmit);
        Assert.Single(session.Record.Media);
        Assert.Equal("p.jpg", session.Record.Media[0].FileName);

        session.RemoveMedia("photo", "a1");
        Assert.False(session.CanSubmit);
        Assert.Empty(session.Record.Media);
    }

    [Fact]
    public void CreateForNewRecord_seeds_defaults_from_a_previous_record()
    {
        var previous = new FieldRecord { RecordId = "prev", FormId = "inspection" };
        previous.Values["name"] = "Inspector Gadget";
        previous.Values["hasDamage"] = true;

        var session = FormSession.CreateForNewRecord(
            SampleForm(), "r2", seedFrom: previous, seedFieldIds: ["name"]);

        Assert.Equal("Inspector Gadget", session.GetValue("name"));
        Assert.Null(session.GetValue("hasDamage")); // not in the allow-list
    }

    [Fact]
    public void Seeding_skips_calculated_and_media_fields()
    {
        var form = new FormDefinition
        {
            FormId = "f",
            Name = "f",
            Sections =
            [
                new FormSection
                {
                    SectionId = "s",
                    Label = "s",
                    Fields =
                    [
                        new FormField { FieldId = "calc", Label = "C", Type = FormFieldType.Calculated, CalculatedExpression = "concat('x')" },
                        new FormField { FieldId = "pic", Label = "P", Type = FormFieldType.Photo },
                        new FormField { FieldId = "note", Label = "N", Type = FormFieldType.Text },
                    ],
                },
            ],
        };

        var previous = new FieldRecord { RecordId = "prev", FormId = "f" };
        previous.Values["calc"] = "stale";
        previous.Values["pic"] = "stale";
        previous.Values["note"] = "keep";

        var session = FormSession.CreateForNewRecord(form, "r2", seedFrom: previous);

        Assert.Equal("keep", session.GetValue("note"));
        Assert.Null(session.GetValue("pic"));
        // calc is recomputed from its expression, not seeded from the stale value.
        Assert.Equal("x", session.GetValue("calc"));
    }

    [Fact]
    public void Open_rehydrates_state_from_an_existing_draft()
    {
        var draft = new FieldRecord { RecordId = "d1", FormId = "inspection" };
        draft.Values["name"] = "Saved";
        draft.Values["hasDamage"] = true;
        draft.Values["damageNotes"] = "note";

        var session = FormSession.Open(SampleForm(), draft);

        Assert.Equal("Saved", session.GetValue("name"));
        Assert.True(session.GetField("damageNotes").IsVisible);
        Assert.Equal("note", session.GetValue("damageNotes"));
    }
}
