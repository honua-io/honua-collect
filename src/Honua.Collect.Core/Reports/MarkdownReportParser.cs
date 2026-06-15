namespace Honua.Collect.Core.Reports;

/// <summary>The kind of a parsed report block.</summary>
public enum ReportBlockKind
{
    /// <summary>A heading; <see cref="ReportBlock.Level"/> is 1–3.</summary>
    Heading,

    /// <summary>A body paragraph.</summary>
    Paragraph,

    /// <summary>A bullet-list item.</summary>
    Bullet,
}

/// <summary>One block of a report.</summary>
/// <param name="Kind">The block kind.</param>
/// <param name="Level">Heading level (1–3) for headings; 0 otherwise.</param>
/// <param name="Text">The block's plain text (inline markdown stripped).</param>
public sealed record ReportBlock(ReportBlockKind Kind, int Level, string Text);

/// <summary>
/// Parses the Markdown produced by <see cref="RecordReportRenderer"/> into a small,
/// presentation-neutral block list (headings / paragraphs / bullets) that the DOCX
/// and PDF writers render. Keeping the parse here means both binary writers stay
/// consistent with the Markdown report and with each other.
/// </summary>
public static class MarkdownReportParser
{
    /// <summary>Parses report Markdown into blocks; blank lines are dropped.</summary>
    /// <param name="markdown">The Markdown report.</param>
    /// <returns>The parsed blocks, in order.</returns>
    public static IReadOnlyList<ReportBlock> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var blocks = new List<ReportBlock>();
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                blocks.Add(new ReportBlock(ReportBlockKind.Heading, 3, StripInline(line[4..])));
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                blocks.Add(new ReportBlock(ReportBlockKind.Heading, 2, StripInline(line[3..])));
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                blocks.Add(new ReportBlock(ReportBlockKind.Heading, 1, StripInline(line[2..])));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                blocks.Add(new ReportBlock(ReportBlockKind.Bullet, 0, StripInline(line[2..])));
            }
            else
            {
                blocks.Add(new ReportBlock(ReportBlockKind.Paragraph, 0, StripInline(line)));
            }
        }

        return blocks;
    }

    // Removes the inline emphasis markers the report uses (**bold**), keeping the text.
    private static string StripInline(string text) => text.Replace("**", string.Empty, StringComparison.Ordinal).Trim();
}
