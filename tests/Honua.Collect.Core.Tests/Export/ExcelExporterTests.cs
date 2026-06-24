using System.IO.Compression;
using System.Xml.Linq;
using Honua.Collect.Core.Export;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Export;

public class ExcelExporterTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static FormDefinition Form() => new()
    {
        FormId = "f",
        Name = "f",
        Sections =
        [
            new FormSection
            {
                SectionId = "s",
                Label = "s",
                Fields =
                [
                    new FormField { FieldId = "name", Label = "Name", Type = FormFieldType.Text },
                    new FormField { FieldId = "count", Label = "Count", Type = FormFieldType.Numeric },
                    new FormField { FieldId = "ok", Label = "OK", Type = FormFieldType.YesNo },
                    new FormField { FieldId = "tags", Label = "Tags", Type = FormFieldType.MultipleChoice },
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                ],
            },
        ],
    };

    private static FieldRecord Record()
    {
        var r = new FieldRecord { RecordId = "r1", FormId = "f", Status = RecordStatus.Submitted };
        r.Values["name"] = "Bridge, North";
        r.Values["count"] = 3;
        r.Values["ok"] = true;
        r.Values["tags"] = new[] { "a", "b" };
        r.Location = new FieldGeoPoint(45.5, -122.6); // lat, lon
        r.Media.Add(new FieldMediaAttachment { AttachmentId = "m1", FieldId = "photo", FileName = "p.jpg", MediaType = FieldMediaType.Photo });
        return r;
    }

    /// <summary>Opens the workbook bytes and returns the named part's parsed XML.</summary>
    private static XDocument Part(byte[] xlsx, string path)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        var entry = zip.GetEntry(path) ?? throw new Xunit.Sdk.XunitException($"missing part {path}");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static string[] PartNames(byte[] xlsx)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }

    /// <summary>All cells of a worksheet row, in order, as (type, value) pairs.</summary>
    private static (string? Type, string Value)[] Row(XDocument sheet, int rowIndex)
    {
        var row = sheet.Descendants(Ns + "row")
            .Single(r => (string?)r.Attribute("r") == rowIndex.ToString());
        return row.Elements(Ns + "c").Select(c =>
        {
            var type = (string?)c.Attribute("t");
            var value = type == "inlineStr"
                ? c.Element(Ns + "is")?.Element(Ns + "t")?.Value ?? string.Empty
                : c.Element(Ns + "v")?.Value ?? string.Empty;
            return (type, value);
        }).ToArray();
    }

    [Fact]
    public void Output_is_a_valid_openxml_package_with_the_required_parts()
    {
        var xlsx = ExcelExporter.Export(Form(), [Record()]);

        var parts = PartNames(xlsx);
        Assert.Contains("[Content_Types].xml", parts);
        Assert.Contains("_rels/.rels", parts);
        Assert.Contains("xl/workbook.xml", parts);
        Assert.Contains("xl/_rels/workbook.xml.rels", parts);
        Assert.Contains("xl/worksheets/sheet1.xml", parts);

        // Every part is well-formed XML and the workbook references the sheet.
        var workbook = Part(xlsx, "xl/workbook.xml");
        Assert.Equal("Records", (string?)workbook.Descendants(Ns + "sheet").Single().Attribute("name"));
    }

    [Fact]
    public void Header_row_lists_fixed_columns_then_form_fields()
    {
        var sheet = Part(ExcelExporter.Export(Form(), [Record()]), "xl/worksheets/sheet1.xml");

        var header = Row(sheet, 1).Select(c => c.Value).ToArray();
        Assert.Equal(
            new[] { "record_id", "status", "latitude", "longitude", "name", "count", "ok", "tags", "photo" },
            header);
        Assert.All(Row(sheet, 1), c => Assert.Equal("inlineStr", c.Type));
    }

    [Fact]
    public void Cells_are_typed_numbers_booleans_and_strings()
    {
        var sheet = Part(ExcelExporter.Export(Form(), [Record()]), "xl/worksheets/sheet1.xml");
        var cells = Row(sheet, 2);

        // record_id, status -> strings
        Assert.Equal(("inlineStr", "r1"), cells[0]);
        Assert.Equal(("inlineStr", "Submitted"), cells[1]);

        // latitude, longitude -> real numbers
        Assert.Null(cells[2].Type);
        Assert.Equal(45.5, double.Parse(cells[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(-122.6, double.Parse(cells[3].Value, System.Globalization.CultureInfo.InvariantCulture));

        // name (string, with comma preserved verbatim — no CSV escaping needed)
        Assert.Equal(("inlineStr", "Bridge, North"), cells[4]);

        // count -> number
        Assert.Null(cells[5].Type);
        Assert.Equal(3.0, double.Parse(cells[5].Value, System.Globalization.CultureInfo.InvariantCulture));

        // ok -> boolean cell (t="b", value 1)
        Assert.Equal(("b", "1"), cells[6]);

        // tags (multi-choice) joined; photo media file name -> strings
        Assert.Equal(("inlineStr", "a;b"), cells[7]);
        Assert.Equal(("inlineStr", "p.jpg"), cells[8]);
    }

    [Fact]
    public void Missing_values_and_no_location_produce_blank_cells()
    {
        var sparse = new FieldRecord { RecordId = "r2", FormId = "f" };
        var sheet = Part(ExcelExporter.Export(Form(), [sparse]), "xl/worksheets/sheet1.xml");
        var cells = Row(sheet, 2);

        Assert.Equal(("inlineStr", "r2"), cells[0]);
        Assert.Equal(("inlineStr", string.Empty), cells[2]); // latitude blank
        Assert.Equal(("inlineStr", string.Empty), cells[3]); // longitude blank
        Assert.Equal(("inlineStr", string.Empty), cells[4]); // name blank
    }

    [Fact]
    public void Each_record_becomes_a_row_in_order()
    {
        var r2 = new FieldRecord { RecordId = "r2", FormId = "f", Status = RecordStatus.Draft };
        var sheet = Part(ExcelExporter.Export(Form(), [Record(), r2]), "xl/worksheets/sheet1.xml");

        Assert.Equal(3, sheet.Descendants(Ns + "row").Count()); // header + 2 records
        Assert.Equal("r1", Row(sheet, 2)[0].Value);
        Assert.Equal("r2", Row(sheet, 3)[0].Value);
    }

    [Fact]
    public void Empty_record_set_yields_a_header_only_workbook()
    {
        var sheet = Part(ExcelExporter.Export(Form(), []), "xl/worksheets/sheet1.xml");
        Assert.Single(sheet.Descendants(Ns + "row")); // header only
    }

    [Fact]
    public void Sheet_name_is_sanitised_and_capped_at_31_chars()
    {
        var workbook = Part(ExcelExporter.Export(Form(), [], "Bad/Name:With*Chars [and a very long suffix here]"), "xl/workbook.xml");
        var name = (string)workbook.Descendants(Ns + "sheet").Single().Attribute("name")!;

        Assert.True(name.Length <= 31);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('*', name);
    }

    [Fact]
    public void ExportBinary_routes_xlsx_and_export_text_rejects_it()
    {
        var bytes = RecordExporter.ExportBinary(Form(), [Record()], ExportFormat.Xlsx);
        Assert.True(RecordExporter.IsBinary(ExportFormat.Xlsx));
        Assert.Contains("xl/workbook.xml", PartNames(bytes));

        Assert.Throws<ArgumentException>(() => RecordExporter.Export(Form(), [Record()], ExportFormat.Xlsx));
        Assert.Throws<ArgumentException>(() => RecordExporter.ExportBinary(Form(), [Record()], ExportFormat.Csv));
    }

    [Fact]
    public void Guards_null_and_blank_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => ExcelExporter.Export(null!, []));
        Assert.Throws<ArgumentNullException>(() => ExcelExporter.Export(Form(), null!));
        Assert.Throws<ArgumentException>(() => ExcelExporter.Export(Form(), [], "  "));
    }
}
