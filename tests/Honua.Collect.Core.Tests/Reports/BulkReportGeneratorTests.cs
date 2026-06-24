using System.Text;
using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Reports;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Reports;

public class BulkReportGeneratorTests
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
                ],
            },
        ],
    };

    private static FieldRecord Record(string id, string poleId)
    {
        var r = new FieldRecord { RecordId = id, FormId = "pole", Status = RecordStatus.Submitted };
        r.Values["poleId"] = poleId;
        r.Values["ok"] = true;
        return r;
    }

    private static BulkReportGenerator Pro() => new(new CollectEntitlements(CollectEdition.Pro));

    [Fact]
    public void Bulk_render_produces_one_output_per_record_in_order()
    {
        var records = new[] { Record("r1", "P-1"), Record("r2", "P-2"), Record("r3", "P-3") };

        var manifest = Pro().Generate(Form(), records);

        Assert.Equal(3, manifest.Count);
        Assert.Equal(new[] { "r1", "r2", "r3" }, manifest.Entries.Select(e => e.RecordId));
        Assert.All(manifest.Entries, e => Assert.Contains("Pole inspection", Encoding.UTF8.GetString(e.Content)));
    }

    [Fact]
    public void Empty_set_yields_an_empty_manifest()
    {
        var manifest = Pro().Generate(Form(), []);

        Assert.Equal(0, manifest.Count);
        Assert.Empty(manifest.Entries);
    }

    [Fact]
    public void Single_record_yields_one_entry()
    {
        var manifest = Pro().Generate(Form(), [Record("solo", "P-9")]);

        Assert.Single(manifest.Entries);
        Assert.Equal("solo", manifest.Entries[0].RecordId);
    }

    [Fact]
    public void Template_is_applied_across_every_record()
    {
        var records = new[] { Record("r1", "P-1"), Record("r2", "P-2") };
        var template = new ReportTemplate { TitleTemplate = "Pole {poleId}" };

        var manifest = Pro().Generate(Form(), records, template);

        Assert.Contains("# Pole P-1", Encoding.UTF8.GetString(manifest.Entries[0].Content));
        Assert.Contains("# Pole P-2", Encoding.UTF8.GetString(manifest.Entries[1].Content));
    }

    [Fact]
    public void File_names_use_the_template_and_are_unique_and_sanitised()
    {
        // Two records resolve to the same stem -> the second is de-duplicated.
        var records = new[] { Record("r1", "P/1"), Record("r2", "P/1") };

        var manifest = Pro().Generate(Form(), records, fileNameTemplate: "pole-{poleId}", format: ReportOutputFormat.Markdown);

        Assert.Equal("pole-P-1.md", manifest.Entries[0].FileName);
        Assert.Equal("pole-P-1-2.md", manifest.Entries[1].FileName);
    }

    [Fact]
    public void File_name_falls_back_to_record_id()
    {
        var manifest = Pro().Generate(Form(), [Record("rec-7", "P-7")]);

        Assert.Equal("rec-7.md", manifest.Entries[0].FileName);
    }

    [Fact]
    public void Pdf_format_emits_pdf_bytes_with_pdf_extension()
    {
        var manifest = Pro().Generate(Form(), [Record("r1", "P-1")], format: ReportOutputFormat.Pdf);

        var entry = manifest.Entries[0];
        Assert.Equal(ReportOutputFormat.Pdf, manifest.Format);
        Assert.EndsWith(".pdf", entry.FileName);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(entry.Content, 0, 4));
    }

    [Fact]
    public void Docx_format_emits_a_zip_with_docx_extension()
    {
        var manifest = Pro().Generate(Form(), [Record("r1", "P-1")], format: ReportOutputFormat.Docx);

        var entry = manifest.Entries[0];
        Assert.EndsWith(".docx", entry.FileName);
        Assert.Equal(0x50, entry.Content[0]); // 'P'
        Assert.Equal(0x4B, entry.Content[1]); // 'K' -> ZIP local file header
    }

    [Fact]
    public void Bulk_render_requires_pro_entitlement()
    {
        var generator = new BulkReportGenerator(CollectEntitlements.Community);

        Assert.Throws<FeatureNotEntitledException>(() => generator.Generate(Form(), [Record("r1", "P-1")]));
    }
}
