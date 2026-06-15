using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Honua.Collect.Core.Reports;

/// <summary>
/// Renders a Markdown report (from <see cref="RecordReportRenderer"/>) to a
/// Microsoft Word <c>.docx</c> file — closing BACKLOG R1's "Word" output without a
/// third-party rendering library. A <c>.docx</c> is an Open Packaging zip of XML
/// parts; this writes the minimal valid set (content types, the document
/// relationship, and the body) with headings styled inline (bold + size) so no
/// separate styles part is needed. Headings, paragraphs, and bullets are honoured;
/// rich layout/templating is intentionally out of scope.
/// </summary>
public static class DocxReportWriter
{
    private const string WordMlNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Renders report Markdown to a .docx byte array.</summary>
    /// <param name="markdown">The Markdown report.</param>
    /// <returns>The .docx file bytes.</returns>
    public static byte[] Write(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var blocks = MarkdownReportParser.Parse(markdown);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "[Content_Types].xml", ContentTypes());
            AddEntry(zip, "_rels/.rels", RootRelationships());
            AddEntry(zip, "word/document.xml", Document(blocks));
        }

        return buffer.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string path, string content)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes);
    }

    private static string ContentTypes() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
        </Types>
        """;

    private static string RootRelationships() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
        </Relationships>
        """;

    private static string Document(IReadOnlyList<ReportBlock> blocks)
    {
        var output = new StringBuilder();
        var settings = new XmlWriterSettings { OmitXmlDeclaration = false, Encoding = Encoding.UTF8, Indent = true };
        using var writer = XmlWriter.Create(output, settings);

        writer.WriteStartDocument(standalone: true);
        writer.WriteStartElement("w", "document", WordMlNamespace);
        writer.WriteStartElement("w", "body", WordMlNamespace);

        foreach (var block in blocks)
        {
            WriteParagraph(writer, block);
        }

        writer.WriteEndElement(); // body
        writer.WriteEndElement(); // document
        writer.WriteEndDocument();
        writer.Flush();
        return output.ToString();
    }

    private static void WriteParagraph(XmlWriter writer, ReportBlock block)
    {
        writer.WriteStartElement("w", "p", WordMlNamespace);
        writer.WriteStartElement("w", "r", WordMlNamespace);

        // Style headings inline so no styles part is required: bold, and a size that
        // shrinks with heading depth (32/28/24 half-points = 16/14/12 pt).
        if (block.Kind == ReportBlockKind.Heading)
        {
            var size = block.Level switch { 1 => "32", 2 => "28", _ => "24" };
            writer.WriteStartElement("w", "rPr", WordMlNamespace);
            writer.WriteStartElement("w", "b", WordMlNamespace);
            writer.WriteEndElement();
            writer.WriteStartElement("w", "sz", WordMlNamespace);
            writer.WriteAttributeString("w", "val", WordMlNamespace, size);
            writer.WriteEndElement();
            writer.WriteEndElement(); // rPr
        }

        var text = block.Kind == ReportBlockKind.Bullet ? "• " + block.Text : block.Text;
        writer.WriteStartElement("w", "t", WordMlNamespace);
        writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
        writer.WriteString(text);
        writer.WriteEndElement(); // t

        writer.WriteEndElement(); // r
        writer.WriteEndElement(); // p
    }
}
