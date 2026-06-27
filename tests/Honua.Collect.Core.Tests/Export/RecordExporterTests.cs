using System.Text.Json;
using Honua.Collect.Core.Export;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Export;

public class RecordExporterTests
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
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                ],
            },
        ],
    };

    private static FieldRecord Record()
    {
        var r = new FieldRecord { RecordId = "r1", FormId = "f", Status = RecordStatus.Submitted };
        r.Values["name"] = "Bridge, North";   // embedded comma forces CSV quoting
        r.Values["count"] = 3;
        r.Values["tags"] = new[] { "a", "b" };
        r.Location = new FieldGeoPoint(45.5, -122.6);
        r.Media.Add(new FieldMediaAttachment { AttachmentId = "m1", FieldId = "photo", FileName = "p.jpg", MediaType = FieldMediaType.Photo });
        return r;
    }

    [Fact]
    public void Csv_has_header_and_quotes_values_containing_commas()
    {
        var csv = RecordExporter.ToCsv(Form(), [Record()]);
        var lines = csv.TrimEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();

        Assert.Equal("record_id,status,latitude,longitude,name,count,tags,photo", lines[0]);
        Assert.Contains("\"Bridge, North\"", lines[1]);
        Assert.Contains("a;b", lines[1]);   // multi-choice joined
        Assert.Contains("p.jpg", lines[1]); // media filename
        Assert.Contains("45.5,-122.6", lines[1]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tleading tab")]
    public void Csv_neutralizes_formula_injection_in_untrusted_cells(string payload)
    {
        var record = new FieldRecord { RecordId = "r1", FormId = "f", Status = RecordStatus.Submitted };
        record.Values["name"] = payload;

        var csv = RecordExporter.ToCsv(Form(), [record]);
        var dataLine = csv.TrimEnd().Split('\n').Last().TrimEnd('\r');

        // The dangerous cell must be neutralized with a leading apostrophe so a
        // spreadsheet renders it as text rather than evaluating it (CWE-1236). The
        // apostrophe sits inside the quotes when the cell is also CSV-quoted.
        Assert.Contains("'" + payload[0], dataLine);
        Assert.DoesNotContain($",{payload[0]}", dataLine); // no bare risky leading char after a delimiter
    }

    [Fact]
    public void Csv_leaves_safe_values_unprefixed()
    {
        var record = new FieldRecord { RecordId = "r1", FormId = "f", Status = RecordStatus.Submitted };
        record.Values["name"] = "Normal Site";

        var csv = RecordExporter.ToCsv(Form(), [record]);
        var dataLine = csv.TrimEnd().Split('\n').Last().TrimEnd('\r');

        Assert.Contains("Normal Site", dataLine);
        Assert.DoesNotContain("'Normal Site", dataLine);
    }

    [Fact]
    public void Csv_emits_a_row_per_record_with_blank_cells_for_missing_values()
    {
        var sparse = new FieldRecord { RecordId = "r2", FormId = "f" };
        var csv = RecordExporter.ToCsv(Form(), [Record(), sparse]);
        var lines = csv.TrimEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length); // header + 2 records
        Assert.StartsWith("r2,Draft,,,,,,", lines[2].TrimEnd('\r'));
    }

    [Fact]
    public void GeoJson_is_a_feature_collection_with_point_geometry_and_typed_properties()
    {
        var geoJson = RecordExporter.ToGeoJson(Form(), [Record()]);
        using var doc = JsonDocument.Parse(geoJson);
        var root = doc.RootElement;

        Assert.Equal("FeatureCollection", root.GetProperty("type").GetString());
        var feature = root.GetProperty("features")[0];
        Assert.Equal("Feature", feature.GetProperty("type").GetString());

        var coords = feature.GetProperty("geometry").GetProperty("coordinates");
        Assert.Equal(-122.6, coords[0].GetDouble()); // lon first
        Assert.Equal(45.5, coords[1].GetDouble());

        var props = feature.GetProperty("properties");
        Assert.Equal("r1", props.GetProperty("record_id").GetString());
        Assert.Equal("Bridge, North", props.GetProperty("name").GetString());
        Assert.Equal(3, props.GetProperty("count").GetInt32());                 // numeric stays numeric
        Assert.Equal(JsonValueKind.Array, props.GetProperty("tags").ValueKind); // multi-choice stays an array
        Assert.Equal("p.jpg", props.GetProperty("photo")[0].GetString());       // media filenames
    }

    [Fact]
    public void GeoJson_writes_null_geometry_when_record_has_no_location()
    {
        var noLoc = new FieldRecord { RecordId = "r3", FormId = "f" };
        var geoJson = RecordExporter.ToGeoJson(Form(), [noLoc]);
        using var doc = JsonDocument.Parse(geoJson);

        var feature = doc.RootElement.GetProperty("features")[0];
        Assert.Equal(JsonValueKind.Null, feature.GetProperty("geometry").ValueKind);
    }

    [Fact]
    public void Export_dispatches_on_format()
    {
        Assert.StartsWith("record_id", RecordExporter.Export(Form(), [Record()], ExportFormat.Csv));
        Assert.Contains("FeatureCollection", RecordExporter.Export(Form(), [Record()], ExportFormat.GeoJson));
    }
}
