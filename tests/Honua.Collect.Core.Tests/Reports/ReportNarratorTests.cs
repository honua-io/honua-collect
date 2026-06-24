using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Reports;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Reports;

public class ReportNarratorTests
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
                Fields = [new FormField { FieldId = "poleId", Label = "Pole ID", Type = FormFieldType.Text }],
            },
        ],
    };

    private static FieldRecord Record()
    {
        var r = new FieldRecord { RecordId = "r1", FormId = "pole", Status = RecordStatus.Submitted };
        r.Values["poleId"] = "P-7";
        return r;
    }

    private static RecordReportRenderer Pro() => new(new CollectEntitlements(CollectEdition.Pro));

    private sealed class StubNarrator : IReportNarrator
    {
        public Task<ReportNarrative?> DraftAsync(FormDefinition form, FieldRecord record, CancellationToken ct = default)
            => Task.FromResult<ReportNarrative?>(new ReportNarrative(
                "Pole P-7 is in service.",
                ["No rot observed."],
                ["Re-inspect in 12 months."]));
    }

    [Fact]
    public async Task Null_narrator_returns_the_deterministic_report_unchanged()
    {
        var template = new ReportTemplate { IncludeNarrative = true };

        var withNarrator = await Pro().RenderMarkdownAsync(Form(), Record(), NullReportNarrator.Instance, template);
        var plain = Pro().RenderMarkdown(Form(), Record(), template);

        Assert.Equal(plain, withNarrator);
        Assert.DoesNotContain("## Summary", withNarrator);
    }

    [Fact]
    public async Task Narrative_is_folded_in_when_template_opts_in()
    {
        var template = new ReportTemplate { IncludeNarrative = true };

        var md = await Pro().RenderMarkdownAsync(Form(), Record(), new StubNarrator(), template);

        Assert.Contains("## Summary", md);
        Assert.Contains("Pole P-7 is in service.", md);
        Assert.Contains("### Findings", md);
        Assert.Contains("- No rot observed.", md);
        Assert.Contains("### Recommended actions", md);
        Assert.Contains("- Re-inspect in 12 months.", md);

        // Narrative leads, before the form's Header section.
        Assert.True(md.IndexOf("## Summary", StringComparison.Ordinal)
            < md.IndexOf("## Header", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Narrative_is_omitted_when_template_opts_out()
    {
        var md = await Pro().RenderMarkdownAsync(Form(), Record(), new StubNarrator(), new ReportTemplate());

        Assert.DoesNotContain("## Summary", md);
    }
}
