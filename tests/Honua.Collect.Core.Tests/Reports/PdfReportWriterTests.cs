using System.Globalization;
using System.Text;
using Honua.Collect.Core.Reports;

namespace Honua.Collect.Core.Tests.Reports;

public class PdfReportWriterTests
{
    private const string Markdown =
        "# Site Report\n\n- **Record:** r1\n- **Status:** Submitted\n\n## Site details\n\n- **Site name:** Alpha\n";

    // Latin1 keeps byte offset == string index, so we can reason about xref offsets as substring positions.
    private static string AsLatin1(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    [Fact]
    public void Has_pdf_header_and_eof_and_core_structure()
    {
        var text = AsLatin1(PdfReportWriter.Write(Markdown));

        Assert.StartsWith("%PDF-1.", text);
        Assert.EndsWith("%%EOF", text);
        Assert.Contains("/Type /Catalog", text);
        Assert.Contains("/BaseFont /Helvetica", text);
        Assert.Contains("/BaseFont /Helvetica-Bold", text);
    }

    [Fact]
    public void Content_stream_contains_the_report_text()
    {
        var text = AsLatin1(PdfReportWriter.Write(Markdown));

        Assert.Contains("(Site Report) Tj", text);
        Assert.Contains("(- Record: r1) Tj", text);
        Assert.Contains("(Site details) Tj", text);
    }

    [Fact]
    public void Startxref_points_at_the_xref_table()
    {
        var pdf = PdfReportWriter.Write(Markdown);
        var text = AsLatin1(pdf);

        var marker = text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length;
        var end = text.IndexOf('\n', marker);
        var offset = int.Parse(text[marker..end], CultureInfo.InvariantCulture);

        Assert.Equal("xref", text.Substring(offset, 4));
    }

    [Fact]
    public void First_xref_offset_resolves_to_object_one()
    {
        var text = AsLatin1(PdfReportWriter.Write(Markdown));

        // xref\n 0 N\n {free entry, 20 bytes} {obj1 entry...}
        var xref = text.IndexOf("xref\n", StringComparison.Ordinal);
        var afterCount = text.IndexOf('\n', xref + "xref\n".Length) + 1; // end of the "0 N" line
        var firstEntry = afterCount + 20; // skip the 20-byte free entry
        var obj1Offset = int.Parse(text.Substring(firstEntry, 10), CultureInfo.InvariantCulture);

        Assert.Equal("1 0 obj", text.Substring(obj1Offset, 7));
    }

    [Fact]
    public void Long_reports_paginate_across_multiple_pages()
    {
        var big = new StringBuilder("# Big Report\n\n");
        for (var i = 0; i < 120; i++)
        {
            big.Append("Line ").Append(i).Append(" of the report body.\n\n");
        }

        var text = AsLatin1(PdfReportWriter.Write(big.ToString()));

        // The Pages object must report more than one page.
        var countMarker = text.IndexOf("/Count ", StringComparison.Ordinal) + "/Count ".Length;
        var count = int.Parse(text[countMarker..text.IndexOf(' ', countMarker)], CultureInfo.InvariantCulture);
        Assert.True(count >= 2, $"expected multiple pages, got {count}");
    }

    [Fact]
    public void Escapes_parentheses_and_backslashes()
    {
        var text = AsLatin1(PdfReportWriter.Write("Value (a) with \\ backslash"));
        Assert.Contains("Value \\(a\\) with \\\\ backslash", text);
    }

    [Fact]
    public void Write_guards_null()
    {
        Assert.Throws<ArgumentNullException>(() => PdfReportWriter.Write(null!));
    }
}
