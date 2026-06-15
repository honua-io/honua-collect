using System.IO.Compression;
using System.Xml.Linq;
using Honua.Collect.Core.Reports;

namespace Honua.Collect.Core.Tests.Reports;

public class DocxReportWriterTests
{
    private const string Markdown =
        "# Site Report\n\n- **Record:** r1\n- **Status:** Submitted\n\n## Site details\n\n- **Site name:** Alpha\n";

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        });
    }

    // --- parser ---------------------------------------------------------------

    [Fact]
    public void Parser_classifies_headings_bullets_and_strips_emphasis()
    {
        var blocks = MarkdownReportParser.Parse(Markdown);

        Assert.Equal(5, blocks.Count); // blank lines dropped
        Assert.Equal(new ReportBlock(ReportBlockKind.Heading, 1, "Site Report"), blocks[0]);
        Assert.Equal(new ReportBlock(ReportBlockKind.Bullet, 0, "Record: r1"), blocks[1]);
        Assert.Equal(new ReportBlock(ReportBlockKind.Heading, 2, "Site details"), blocks[3]);
        Assert.Equal(new ReportBlock(ReportBlockKind.Bullet, 0, "Site name: Alpha"), blocks[4]);
    }

    // --- docx -----------------------------------------------------------------

    [Fact]
    public void Writes_a_minimal_valid_opc_package()
    {
        var files = Unzip(DocxReportWriter.Write(Markdown));

        Assert.Contains("[Content_Types].xml", files.Keys);
        Assert.Contains("_rels/.rels", files.Keys);
        Assert.Contains("word/document.xml", files.Keys);
        // Every part is well-formed XML.
        foreach (var xml in files.Values)
        {
            XDocument.Parse(System.Text.Encoding.UTF8.GetString(xml));
        }
    }

    [Fact]
    public void Document_body_carries_the_report_text()
    {
        var files = Unzip(DocxReportWriter.Write(Markdown));
        var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(files["word/document.xml"]));

        var texts = doc.Descendants(W + "t").Select(t => t.Value).ToList();
        Assert.Contains("Site Report", texts);
        Assert.Contains("Site details", texts);
        Assert.Contains("• Record: r1", texts); // bullet prefix
        Assert.Contains("• Site name: Alpha", texts);
    }

    [Fact]
    public void Headings_are_styled_bold_inline()
    {
        var files = Unzip(DocxReportWriter.Write(Markdown));
        var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(files["word/document.xml"]));

        // The paragraph whose run text is the title must carry a bold run property.
        var titleRun = doc.Descendants(W + "r")
            .First(r => r.Element(W + "t")?.Value == "Site Report");
        Assert.NotNull(titleRun.Element(W + "rPr")!.Element(W + "b"));
        Assert.Equal("32", titleRun.Element(W + "rPr")!.Element(W + "sz")!.Attribute(W + "val")!.Value);
    }

    [Fact]
    public void Write_and_parse_guard_null()
    {
        Assert.Throws<ArgumentNullException>(() => DocxReportWriter.Write(null!));
        Assert.Throws<ArgumentNullException>(() => MarkdownReportParser.Parse(null!));
    }
}
