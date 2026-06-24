using Honua.Collect.Core.Export;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Export;

public class MappedCsvExportTests
{
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
                    new FormField { FieldId = "tags", Label = "Tags", Type = FormFieldType.MultipleChoice },
                ],
            },
        ],
    };

    private static FieldRecord Record()
    {
        var r = new FieldRecord { RecordId = "r1", FormId = "f", Status = RecordStatus.Submitted };
        r.Values["name"] = "Bridge, North";
        r.Values["count"] = 3;
        r.Values["tags"] = new[] { "a", "b" };
        r.Location = new FieldGeoPoint(45.5, -122.6);
        return r;
    }

    private static string[] Lines(string csv) => csv.TrimEnd()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public void Mapping_subsets_and_renames_columns_in_order()
    {
        var mapping = ExportFieldMapping.Of(
            ("name", "Asset"),
            ("record_id", "ID"),
            ("count", "Qty"));

        var lines = Lines(RecordExporter.ToMappedCsv(Form(), [Record()], mapping));

        Assert.Equal("Asset,ID,Qty", lines[0]);
        Assert.Equal("\"Bridge, North\",r1,3", lines[1]);
    }

    [Fact]
    public void Mapping_can_pull_fixed_geometry_columns()
    {
        var mapping = ExportFieldMapping.Of(
            ("latitude", "lat"),
            ("longitude", "lon"),
            ("status", "state"));

        var lines = Lines(RecordExporter.ToMappedCsv(Form(), [Record()], mapping));

        Assert.Equal("lat,lon,state", lines[0]);
        Assert.Equal("45.5,-122.6,Submitted", lines[1]);
    }

    [Fact]
    public void Header_defaults_to_source_when_unset_and_multichoice_is_joined()
    {
        var mapping = new ExportFieldMapping([new ExportColumn("tags")]);

        var lines = Lines(RecordExporter.ToMappedCsv(Form(), [Record()], mapping));

        Assert.Equal("tags", lines[0]);
        Assert.Equal("a;b", lines[1]);
    }

    [Fact]
    public void Unknown_source_emits_a_blank_cell()
    {
        var mapping = ExportFieldMapping.Of(("record_id", "id"), ("missing", "Missing"));

        var lines = Lines(RecordExporter.ToMappedCsv(Form(), [Record()], mapping));

        Assert.Equal("id,Missing", lines[0]);
        Assert.Equal("r1,", lines[1]);
    }

    [Fact]
    public void Empty_set_emits_only_the_header_row()
    {
        var mapping = ExportFieldMapping.Of(("record_id", "id"), ("name", "Name"));

        var lines = Lines(RecordExporter.ToMappedCsv(Form(), [], mapping));

        Assert.Single(lines);
        Assert.Equal("id,Name", lines[0]);
    }

    [Fact]
    public void Empty_mapping_is_rejected()
    {
        var mapping = new ExportFieldMapping([]);

        Assert.Throws<ArgumentException>(() => RecordExporter.ToMappedCsv(Form(), [Record()], mapping));
    }
}
