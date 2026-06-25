using Honua.Collect.Core.Migration;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Migration;

/// <summary>
/// Verifies the Fulcrum app-schema → form mapping and Fulcrum record-export
/// (GeoJSON + CSV) → records mapping (epic #37 migration guide) against sample
/// export fixtures.
/// </summary>
public sealed class FulcrumImporterTests
{
    // A representative Fulcrum app schema: text, choice (with choices), yes/no,
    // photo, an explicit numeric field, a nested Section that must be flattened,
    // and an unknown element type that must be skipped.
    private const string AppSchema = """
    {
      "name": "Hydrant Inspection",
      "elements": [
        { "type": "TextField", "data_name": "asset_id", "label": "Asset ID", "required": true },
        {
          "type": "ChoiceField", "data_name": "condition", "label": "Condition",
          "choices": [
            { "label": "Good", "value": "good" },
            { "label": "Needs Repair", "value": "repair" }
          ]
        },
        { "type": "YesNoField", "data_name": "operational", "label": "Operational" },
        { "type": "PhotoField", "data_name": "photo", "label": "Photo" },
        { "type": "IntegerField", "data_name": "psi", "label": "Pressure (PSI)" },
        {
          "type": "Section", "data_name": "notes_section", "label": "Notes",
          "elements": [
            { "type": "TextArea", "data_name": "notes", "label": "Notes" }
          ]
        },
        { "type": "HiddenSorceryField", "data_name": "mystery", "label": "Mystery" }
      ]
    }
    """;

    private const string GeoJsonExport = """
    {
      "type": "FeatureCollection",
      "features": [
        {
          "type": "Feature",
          "geometry": { "type": "Point", "coordinates": [-157.81, 21.31] },
          "properties": {
            "fulcrum_id": "rec-1",
            "asset_id": "H-001",
            "condition": "good",
            "operational": "yes",
            "psi": 62,
            "notes": "clear",
            "_status": "submitted",
            "_created_at": "2026-01-02T00:00:00Z"
          }
        },
        {
          "type": "Feature",
          "geometry": { "type": "Point", "coordinates": [-157.9, 21.4] },
          "properties": { "fulcrum_id": "rec-2", "asset_id": "H-002", "condition": "repair" }
        },
        { "type": "Feature", "geometry": null }
      ]
    }
    """;

    private const string CsvExport =
        "fulcrum_id,asset_id,condition,psi,latitude,longitude,_status\n" +
        "rec-1,H-001,good,62,21.31,-157.81,submitted\n" +
        "rec-2,\"H-002, west\",repair,,21.40,-157.90,submitted\n";

    private static FormField Field(FormDefinition form, string id)
        => form.Sections.SelectMany(s => s.Fields).Single(f => f.FieldId == id);

    [Fact]
    public void ImportForm_maps_name_and_form_id()
    {
        var form = FulcrumImporter.ImportForm(AppSchema).Form;

        Assert.Equal("Hydrant Inspection", form.Name);
        Assert.Equal("hydrant_inspection", form.FormId);
    }

    [Fact]
    public void ImportForm_maps_element_types()
    {
        var form = FulcrumImporter.ImportForm(AppSchema).Form;

        Assert.Equal(FormFieldType.Text, Field(form, "asset_id").Type);
        Assert.Equal(FormFieldType.SingleChoice, Field(form, "condition").Type);
        Assert.Equal(FormFieldType.YesNo, Field(form, "operational").Type);
        Assert.Equal(FormFieldType.Photo, Field(form, "photo").Type);
        Assert.Equal(FormFieldType.Numeric, Field(form, "psi").Type);
    }

    [Fact]
    public void ImportForm_keeps_choices_required_and_a_location_field()
    {
        var form = FulcrumImporter.ImportForm(AppSchema).Form;

        Assert.True(Field(form, "asset_id").Required);
        var condition = Field(form, "condition");
        Assert.Equal(2, condition.Choices.Count);
        Assert.Equal("good", condition.Choices[0].Value);
        Assert.Equal(FormFieldType.Location, Field(form, "location").Type);
    }

    [Fact]
    public void ImportForm_flattens_sections_and_skips_unknown_types()
    {
        var result = FulcrumImporter.ImportForm(AppSchema);

        // The nested TextArea survives the Section flatten.
        Assert.Equal(FormFieldType.Text, Field(result.Form, "notes").Type);
        Assert.Contains(result.Skipped, s => s.Contains("notes_section", StringComparison.Ordinal));
        Assert.Contains(result.Skipped, s => s.Contains("mystery", StringComparison.Ordinal));
    }

    [Fact]
    public void ImportGeoJsonRecords_maps_geometry_values_and_record_id()
    {
        var form = FulcrumImporter.ImportForm(AppSchema).Form;
        var result = FulcrumImporter.ImportGeoJsonRecords(form, GeoJsonExport);

        Assert.Equal(2, result.Records.Count); // the geometry-less feature is skipped
        var first = result.Records[0];
        Assert.Equal("rec-1", first.RecordId);
        Assert.Equal(form.FormId, first.FormId);
        Assert.NotNull(first.Location);
        Assert.Equal(21.31, first.Location!.Latitude, 5);
        Assert.Equal(-157.81, first.Location.Longitude, 5);
        Assert.Equal("H-001", first.Values["asset_id"]);
        Assert.Equal("good", first.Values["condition"]);
        Assert.Equal(62L, first.Values["psi"]);
    }

    [Fact]
    public void ImportGeoJsonRecords_excludes_system_columns_from_values()
    {
        var form = FulcrumImporter.ImportForm(AppSchema).Form;
        var first = FulcrumImporter.ImportGeoJsonRecords(form, GeoJsonExport).Records[0];

        Assert.False(first.Values.ContainsKey("_status"));
        Assert.False(first.Values.ContainsKey("_created_at"));
        Assert.False(first.Values.ContainsKey("fulcrum_id"));
    }

    [Fact]
    public void ImportCsvRecords_maps_rows_quotes_and_location()
    {
        var form = FulcrumImporter.ImportForm(AppSchema).Form;
        var result = FulcrumImporter.ImportCsvRecords(form, CsvExport);

        Assert.Equal(2, result.Records.Count);
        var second = result.Records[1];
        Assert.Equal("rec-2", second.RecordId);
        Assert.Equal("H-002, west", second.Values["asset_id"]); // quoted comma preserved
        Assert.Equal(21.40, second.Location!.Latitude, 5);
        // psi was blank in row 2 → not set rather than empty-string
        Assert.False(second.Values.ContainsKey("psi"));
    }

    [Fact]
    public void ImportForm_resolves_wrapper_with_form_property()
    {
        var wrapped = """{ "form": { "name": "Wrapped", "elements": [ { "type": "TextField", "data_name": "a" } ] } }""";

        Assert.Equal("Wrapped", FulcrumImporter.ImportForm(wrapped).Form.Name);
    }

    [Fact]
    public void ImportForm_invalid_json_throws()
        => Assert.Throws<MigrationImportException>(() => FulcrumImporter.ImportForm("{ not json"));

    [Fact]
    public void ImportForm_without_elements_throws()
        => Assert.Throws<MigrationImportException>(() => FulcrumImporter.ImportForm("""{ "name": "x" }"""));

    [Fact]
    public void ImportGeoJsonRecords_invalid_json_throws()
        => Assert.Throws<MigrationImportException>(() =>
            FulcrumImporter.ImportGeoJsonRecords(FulcrumImporter.ImportForm(AppSchema).Form, "{ bad"));
}
