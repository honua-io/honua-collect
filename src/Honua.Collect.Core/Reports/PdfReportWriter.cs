using System.Globalization;
using System.Text;

namespace Honua.Collect.Core.Reports;

/// <summary>
/// Renders a Markdown report (from <see cref="RecordReportRenderer"/>) to a PDF —
/// the other half of BACKLOG R1 — without a third-party rendering library. PDF is a
/// documented object/xref text format, so a functional, paginated text report is
/// hand-emittable: headings (Helvetica-Bold, sized by level), paragraphs and
/// bullets (Helvetica) laid out on US-Letter pages with simple wrapping. Rich
/// layout/templating is intentionally out of scope.
/// </summary>
public static class PdfReportWriter
{
    private const int PageWidth = 612;   // US Letter, points
    private const int PageHeight = 792;
    private const int Margin = 50;
    private const int BodySize = 11;
    private const int MaxLineChars = 95;  // simple character-based wrap

    private sealed record Line(string Text, int Size, bool Bold);

    /// <summary>Renders report Markdown to PDF bytes.</summary>
    /// <param name="markdown">The Markdown report.</param>
    /// <returns>The PDF file bytes.</returns>
    public static byte[] Write(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var pages = Paginate(LayOut(MarkdownReportParser.Parse(markdown)));
        return Assemble(pages);
    }

    private static List<Line> LayOut(IReadOnlyList<ReportBlock> blocks)
    {
        var lines = new List<Line>();
        foreach (var block in blocks)
        {
            var (size, bold, prefix) = block.Kind switch
            {
                ReportBlockKind.Heading => (block.Level switch { 1 => 16, 2 => 14, _ => 12 }, true, string.Empty),
                ReportBlockKind.Bullet => (BodySize, false, "- "),
                _ => (BodySize, false, string.Empty),
            };

            foreach (var wrapped in Wrap(prefix + block.Text))
            {
                lines.Add(new Line(wrapped, size, bold));
            }
        }

        return lines;
    }

    private static IEnumerable<string> Wrap(string text)
    {
        if (text.Length <= MaxLineChars)
        {
            yield return text;
            yield break;
        }

        var words = text.Split(' ');
        var current = new StringBuilder();
        foreach (var word in words)
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > MaxLineChars)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(word);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static List<List<(Line Line, int Y)>> Paginate(List<Line> lines)
    {
        var pages = new List<List<(Line, int)>>();
        var page = new List<(Line, int)>();
        var y = PageHeight - Margin;

        foreach (var line in lines)
        {
            var lineHeight = line.Size + 4;
            if (y - lineHeight < Margin && page.Count > 0)
            {
                pages.Add(page);
                page = [];
                y = PageHeight - Margin;
            }

            page.Add((line, y));
            y -= lineHeight;
        }

        pages.Add(page); // always at least one page (possibly empty)
        return pages;
    }

    private static byte[] Assemble(List<List<(Line Line, int Y)>> pages)
    {
        // Object plan: 1 Catalog, 2 Pages, 3 Helvetica, 4 Helvetica-Bold,
        // then per page: a page object and a content object.
        var pageObjectIds = new List<int>();
        var objects = new List<string>();

        // Reserve 1..4; fill bodies after we know the page object ids.
        var firstPageObject = 5;
        var contentStreams = pages.Select(BuildContentStream).ToList();

        var pageBodies = new List<string>();
        var contentBodies = new List<string>();
        for (var i = 0; i < pages.Count; i++)
        {
            var pageId = firstPageObject + (i * 2);
            var contentId = pageId + 1;
            pageObjectIds.Add(pageId);
            pageBodies.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>");
            var stream = contentStreams[i];
            contentBodies.Add($"<< /Length {Encoding.Latin1.GetByteCount(stream)} >>\nstream\n{stream}\nendstream");
        }

        var kids = string.Join(' ', pageObjectIds.Select(id => $"{id} 0 R"));
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");                                 // 1
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");            // 2
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");            // 3
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");       // 4
        for (var i = 0; i < pages.Count; i++)
        {
            objects.Add(pageBodies[i]);
            objects.Add(contentBodies[i]);
        }

        return Serialize(objects);
    }

    private static string BuildContentStream(List<(Line Line, int Y)> page)
    {
        var stream = new StringBuilder();
        foreach (var (line, y) in page)
        {
            var font = line.Bold ? "/F2" : "/F1";
            stream.Append("BT ").Append(font).Append(' ').Append(line.Size).Append(" Tf ")
                .Append(Margin).Append(' ').Append(y).Append(" Td (")
                .Append(EscapePdfText(line.Text)).Append(") Tj ET\n");
        }

        return stream.ToString();
    }

    private static byte[] Serialize(List<string> objects)
    {
        using var output = new MemoryStream();
        void Write(string s) => output.Write(Encoding.Latin1.GetBytes(s));

        Write("%PDF-1.7\n%âãÏÓ\n"); // binary-comment marker

        var offsets = new long[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = output.Position;
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefOffset = output.Position;
        Write("xref\n");
        Write($"0 {objects.Count + 1}\n");
        Write("0000000000 65535 f\r\n");
        for (var i = 1; i <= objects.Count; i++)
        {
            Write($"{offsets[i].ToString("D10", CultureInfo.InvariantCulture)} 00000 n\r\n");
        }

        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF");
        return output.ToArray();
    }

    private static string EscapePdfText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '(': builder.Append("\\("); break;
                case ')': builder.Append("\\)"); break;
                default:
                    // Keep printable WinAnsi-safe ASCII; replace the rest so the stream stays valid.
                    builder.Append(c is >= ' ' and <= '~' ? c : '?');
                    break;
            }
        }

        return builder.ToString();
    }
}
