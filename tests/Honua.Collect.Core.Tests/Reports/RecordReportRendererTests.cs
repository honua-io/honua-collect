using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Reports;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Reports;

public class RecordReportRendererTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "pole",
        Name = "Pole inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "header",
                Label = "Header",
                Fields =
                [
                    new FormField { FieldId = "poleId", Label = "Pole ID", Type = FormFieldType.Text },
                    new FormField { FieldId = "ok", Label = "Serviceable", Type = FormFieldType.YesNo },
                    new FormField { FieldId = "notes", Label = "Notes", Type = FormFieldType.Text },
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                ],
            },
            new FormSection
            {
                SectionId = "attachments",
                Label = "Attachment",
                Repeatable = true,
                Fields = [new FormField { FieldId = "kind", Label = "Kind", Type = FormFieldType.Text }],
            },
        ],
    };

    private static FieldRecord Record()
    {
        var r = new FieldRecord { RecordId = "r1", FormId = "pole", Status = RecordStatus.Submitted };
        r.Values["poleId"] = "P-7";
        r.Values["ok"] = true;
        // notes left empty
        r.Repeats["attachments"] = new List<FieldRepeatInstance>
        {
            new() { Values = { ["kind"] = "transformer" } },
            new() { Values = { ["kind"] = "crossarm" } },
        };
        r.Media.Add(new FieldMediaAttachment { AttachmentId = "m1", FieldId = "photo", FileName = "p.jpg", MediaType = FieldMediaType.Photo });
        return r;
    }

    private static RecordReportRenderer ProRenderer() => new(new CollectEntitlements(CollectEdition.Pro));

    [Fact]
    public void Reports_require_pro_entitlement()
    {
        var renderer = new RecordReportRenderer(CollectEntitlements.Community);
        Assert.Throws<FeatureNotEntitledException>(() => renderer.RenderMarkdown(Form(), Record()));
    }

    [Fact]
    public void Title_template_substitutes_field_placeholders()
    {
        var md = ProRenderer().RenderMarkdown(Form(), Record(),
            new ReportTemplate { TitleTemplate = "Pole inspection {poleId}" });

        Assert.StartsWith("# Pole inspection P-7", md);
    }

    [Fact]
    public void Default_template_renders_fields_metadata_and_media()
    {
        var md = ProRenderer().RenderMarkdown(Form(), Record());

        Assert.Contains("# Pole inspection", md);          // falls back to form name
        Assert.Contains("**Record:** r1", md);
        Assert.Contains("**Status:** Submitted", md);
        Assert.Contains("**Pole ID:** P-7", md);
        Assert.Contains("**Serviceable:** Yes", md);        // bool rendered Yes/No
        Assert.Contains("**Photo:** p.jpg", md);            // media filenames
        Assert.DoesNotContain("Notes", md);                 // empty field omitted by default
    }

    [Fact]
    public void Repeatable_section_renders_each_row()
    {
        var md = ProRenderer().RenderMarkdown(Form(), Record());

        Assert.Contains("### Attachment 1", md);
        Assert.Contains("### Attachment 2", md);
        Assert.Contains("**Kind:** transformer", md);
        Assert.Contains("**Kind:** crossarm", md);
    }

    [Fact]
    public void IncludeEmptyFields_and_section_filter_are_respected()
    {
        var md = ProRenderer().RenderMarkdown(Form(), Record(),
            new ReportTemplate { IncludeEmptyFields = true, SectionIds = ["header"] });

        Assert.Contains("**Notes:**", md);          // empty now shown
        Assert.DoesNotContain("### Attachment", md); // attachments section filtered out
    }
}
