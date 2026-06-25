using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Field.Related;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Related;

public class RelatedRecordsTests
{
    private static FormField LinkField() => new()
    {
        FieldId = "deficiencies",
        Label = "Deficiencies",
        Type = FormFieldType.RecordLink,
        ReferencedFormId = "deficiency",
    };

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
                Fields = [LinkField()],
            },
        ],
    };

    private static FieldRecord Parent() => new() { RecordId = "p1", FormId = "inspection" };

    [Fact]
    public void Link_list_and_unlink_children()
    {
        var related = new RelatedRecords(Parent(), LinkField());

        Assert.True(related.Link("d1", label: "Cracked pole"));
        Assert.True(related.Link("d2"));
        Assert.False(related.Link("d1")); // duplicate ignored

        Assert.Equal(2, related.Count);
        Assert.Equal(["d1", "d2"], related.Children.Select(c => c.RecordId));

        Assert.True(related.Unlink("d1"));
        Assert.False(related.Unlink("missing"));
        Assert.Equal(["d2"], related.Children.Select(c => c.RecordId));
    }

    [Fact]
    public void Create_mints_a_child_against_the_referenced_form_and_links_it()
    {
        var related = new RelatedRecords(Parent(), LinkField());

        var child = related.Create("d1", label: "New deficiency");

        Assert.Equal("d1", child.RecordId);
        Assert.Equal("deficiency", child.FormId); // referenced form id
        Assert.Equal(1, related.Count);
        Assert.Equal("New deficiency", related.Children[0].Label);
    }

    [Fact]
    public void Links_are_stored_on_the_parent_record_value()
    {
        var parent = Parent();
        var related = new RelatedRecords(parent, LinkField());
        related.Link("d1");

        var stored = Assert.IsType<List<FieldRecordLinkValue>>(parent.Values["deficiencies"]);
        Assert.Single(stored);
        Assert.Equal("d1", stored[0].RecordId);
    }

    [Fact]
    public void Default_behavior_is_cascade()
        => Assert.Equal(RecordLinkBehavior.Cascade, new RelatedRecords(Parent(), LinkField()).Behavior);

    [Fact]
    public void Form_session_surfaces_the_related_collection_for_a_link_field()
    {
        var session = FormSession.CreateForNewRecord(
            Form(),
            "p1",
            linkBehaviors: new Dictionary<string, RecordLinkBehavior> { ["deficiencies"] = RecordLinkBehavior.Restrict });

        Assert.Equal(["deficiencies"], session.RelatedFields);

        var related = session.GetRelated("deficiencies");
        Assert.Equal(RecordLinkBehavior.Restrict, related.Behavior);
        Assert.Same(related, session.GetRelated("deficiencies")); // cached, stays in sync

        related.Link("d1");
        // Linking through the model writes onto the session's own record.
        var stored = Assert.IsType<List<FieldRecordLinkValue>>(session.Record.Values["deficiencies"]);
        Assert.Single(stored);
    }

    [Fact]
    public void Get_related_rejects_a_non_link_field()
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
                    Fields = [new FormField { FieldId = "name", Label = "Name", Type = FormFieldType.Text }],
                },
            ],
        };
        var session = FormSession.CreateForNewRecord(form, "p1");

        Assert.Throws<ArgumentException>(() => session.GetRelated("name"));
    }
}
