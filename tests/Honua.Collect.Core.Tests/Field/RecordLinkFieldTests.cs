using Honua.Collect.Core.Field;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field;

public class RecordLinkFieldTests
{
    private static FormField LinkField() => new()
    {
        FieldId = "deficiencies",
        Label = "Deficiencies",
        Type = FormFieldType.RecordLink,
        ReferencedFormId = "deficiency",
    };

    private static FieldRecord Parent() => new() { RecordId = "p1", FormId = "inspection" };

    [Fact]
    public void Rejects_non_link_fields()
    {
        var textField = new FormField { FieldId = "x", Label = "X", Type = FormFieldType.Text };
        Assert.Throws<ArgumentException>(() => new RecordLinkField(Parent(), textField));
    }

    [Fact]
    public void Add_links_a_child_and_stores_them_on_the_parent_record()
    {
        var parent = Parent();
        var links = new RecordLinkField(parent, LinkField());

        Assert.True(links.Add(new FieldRecord { RecordId = "d1", FormId = "deficiency" }, label: "Cracked pole"));

        Assert.Equal(1, links.Count);
        Assert.Equal("d1", links.Links[0].RecordId);
        Assert.Equal("deficiency", links.Links[0].FormId);
        Assert.Equal("Cracked pole", links.Links[0].Label);

        // Stored on the parent record so it travels with validation/export/sync.
        var stored = Assert.IsType<List<FieldRecordLinkValue>>(parent.Values["deficiencies"]);
        Assert.Single(stored);
    }

    [Fact]
    public void Add_is_idempotent_by_record_id()
    {
        var links = new RecordLinkField(Parent(), LinkField());

        Assert.True(links.Add("d1"));
        Assert.False(links.Add("d1")); // duplicate ignored
        Assert.Equal(1, links.Count);
    }

    [Fact]
    public void Remove_deletes_a_link_and_clears_the_value_when_empty()
    {
        var parent = Parent();
        var links = new RecordLinkField(parent, LinkField());
        links.Add("d1");
        links.Add("d2");

        Assert.True(links.Remove("d1"));
        Assert.Equal(1, links.Count);
        Assert.False(links.Remove("missing"));

        links.Remove("d2");
        Assert.Null(parent.Values["deficiencies"]); // empties to null, not an empty list
    }

    [Fact]
    public void Rehydrates_existing_links_from_the_parent_record()
    {
        var parent = Parent();
        parent.Values["deficiencies"] = new List<FieldRecordLinkValue>
        {
            new() { RecordId = "d1", Label = "First" },
            new() { RecordId = "d2", Label = "Second" },
        };

        var links = new RecordLinkField(parent, LinkField());

        Assert.Equal(2, links.Count);
        Assert.Equal(["d1", "d2"], links.Links.Select(l => l.RecordId));
    }

    [Fact]
    public void Rehydrates_a_single_link_value()
    {
        var parent = Parent();
        parent.Values["deficiencies"] = new FieldRecordLinkValue { RecordId = "only" };

        var links = new RecordLinkField(parent, LinkField());

        Assert.Equal(1, links.Count);
        Assert.Equal("only", links.Links[0].RecordId);
    }
}
