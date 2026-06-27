using Honua.Collect.Core.Migration;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Migration;

/// <summary>
/// Verifies the Survey123 / ArcGIS feature-layer schema → Collect form mapping
/// (epic #37 migration guide) against sample layer-schema fixtures.
/// </summary>
public sealed class Survey123LayerImporterTests
{
    // A representative published feature-layer schema: a point layer with a
    // coded-value domain, an alias, a non-nullable (required) field, an unsupported
    // type, and Esri system/editor-tracking columns that must be dropped.
    private const string LayerSchema = """
    {
      "name": "Damage Assessment",
      "geometryType": "esriGeometryPoint",
      "objectIdField": "objectid",
      "globalIdField": "globalid",
      "fields": [
        { "name": "objectid", "type": "esriFieldTypeOID", "alias": "OBJECTID" },
        { "name": "globalid", "type": "esriFieldTypeGlobalID", "alias": "GlobalID" },
        { "name": "site_name", "type": "esriFieldTypeString", "alias": "Site Name", "nullable": false },
        { "name": "inspected_on", "type": "esriFieldTypeDate", "alias": "Inspected On" },
        { "name": "structures", "type": "esriFieldTypeInteger", "alias": "Structure Count" },
        {
          "name": "damage_level",
          "type": "esriFieldTypeString",
          "alias": "Damage Level",
          "domain": {
            "type": "codedValue",
            "name": "damage_levels",
            "codedValues": [
              { "name": "None", "code": "none" },
              { "name": "Minor", "code": "minor" },
              { "name": "Major", "code": "major" }
            ]
          }
        },
        { "name": "raster_blob", "type": "esriFieldTypeRaster", "alias": "Raster" },
        { "name": "weird", "type": "esriFieldTypeGeometry", "alias": "Weird" },
        { "name": "last_edited_date", "type": "esriFieldTypeDate", "alias": "Last Edited" }
      ]
    }
    """;

    private static FormField Field(FormDefinition form, string id)
        => form.Sections.SelectMany(s => s.Fields).Single(f => f.FieldId == id);

    [Fact]
    public void Imports_layer_name_and_slugified_form_id()
    {
        var result = Survey123LayerImporter.Import(LayerSchema);

        Assert.Equal("Damage Assessment", result.Form.Name);
        Assert.Equal("damage_assessment", result.Form.FormId);
    }

    [Fact]
    public void Maps_esri_field_types_to_form_field_types()
    {
        var form = Survey123LayerImporter.Import(LayerSchema).Form;

        Assert.Equal(FormFieldType.Text, Field(form, "site_name").Type);
        Assert.Equal(FormFieldType.DateTime, Field(form, "inspected_on").Type);
        Assert.Equal(FormFieldType.Numeric, Field(form, "structures").Type);
        Assert.Equal(FormFieldType.File, Field(form, "raster_blob").Type);
    }

    [Fact]
    public void Preserves_alias_as_label_and_source_field_name()
    {
        var field = Field(Survey123LayerImporter.Import(LayerSchema).Form, "structures");

        Assert.Equal("Structure Count", field.Label);
        Assert.Equal("structures", field.SourceFieldName);
    }

    [Fact]
    public void Non_nullable_field_becomes_required()
    {
        Assert.True(Field(Survey123LayerImporter.Import(LayerSchema).Form, "site_name").Required);
        Assert.False(Field(Survey123LayerImporter.Import(LayerSchema).Form, "structures").Required);
    }

    [Fact]
    public void Coded_value_domain_becomes_single_choice_with_choices()
    {
        var field = Field(Survey123LayerImporter.Import(LayerSchema).Form, "damage_level");

        Assert.Equal(FormFieldType.SingleChoice, field.Type);
        Assert.Equal(3, field.Choices.Count);
        Assert.Equal("none", field.Choices[0].Value);
        Assert.Equal("None", field.Choices[0].Label);
    }

    [Fact]
    public void Point_geometry_adds_a_leading_location_field()
    {
        var form = Survey123LayerImporter.Import(LayerSchema).Form;
        var first = form.Sections[0].Fields[0];

        Assert.Equal("location", first.FieldId);
        Assert.Equal(FormFieldType.Location, first.Type);
    }

    [Fact]
    public void Drops_system_columns_and_reports_unknown_types_as_skipped()
    {
        var result = Survey123LayerImporter.Import(LayerSchema);
        var ids = result.Form.Sections.SelectMany(s => s.Fields).Select(f => f.FieldId).ToHashSet();

        Assert.DoesNotContain("objectid", ids);
        Assert.DoesNotContain("globalid", ids);
        Assert.DoesNotContain("last_edited_date", ids);
        Assert.DoesNotContain("weird", ids); // esriFieldTypeGeometry is unsupported

        Assert.Contains(result.Skipped, s => s.Contains("weird", StringComparison.Ordinal));
        Assert.Contains(result.Skipped, s => s.Contains("objectid", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("esriGeometryPolyline", FormFieldType.GeoTrace)]
    [InlineData("esriGeometryPolygon", FormFieldType.GeoShape)]
    public void Maps_polyline_and_polygon_geometry(string geometryType, FormFieldType expected)
    {
        var schema = $$"""{ "name": "L", "geometryType": "{{geometryType}}", "fields": [ { "name": "x", "type": "esriFieldTypeString" } ] }""";

        var first = Survey123LayerImporter.Import(schema).Form.Sections[0].Fields[0];

        Assert.Equal("location", first.FieldId);
        Assert.Equal(expected, first.Type);
    }

    [Fact]
    public void Resolves_layer_from_a_service_document_with_layers_array()
    {
        var service = """{ "layers": [ { "name": "First", "fields": [ { "name": "a", "type": "esriFieldTypeString" } ] } ] }""";

        Assert.Equal("First", Survey123LayerImporter.Import(service).Form.Name);
    }

    [Fact]
    public void Honors_explicit_form_id()
        => Assert.Equal("custom_id", Survey123LayerImporter.Import(LayerSchema, "custom_id").Form.FormId);

    [Fact]
    public void Maps_date_time_variants_distinctly()
    {
        // A date-only field has no time component → Date; a timestamp / timestamp-offset
        // both carry a time → DateTime.
        const string schema = """
            {
              "name": "dates",
              "fields": [
                { "name": "d", "type": "esriFieldTypeDateOnly" },
                { "name": "ts", "type": "esriFieldTypeDate" },
                { "name": "tso", "type": "esriFieldTypeTimestampOffset" },
                { "name": "t", "type": "esriFieldTypeTimeOnly" }
              ]
            }
            """;
        var form = Survey123LayerImporter.Import(schema).Form;

        Assert.Equal(FormFieldType.Date, Field(form, "d").Type);
        Assert.Equal(FormFieldType.DateTime, Field(form, "ts").Type);
        Assert.Equal(FormFieldType.DateTime, Field(form, "tso").Type);
        Assert.Equal(FormFieldType.Time, Field(form, "t").Type);
    }

    [Fact]
    public void Invalid_json_throws_migration_import_exception()
        => Assert.Throws<MigrationImportException>(() => Survey123LayerImporter.Import("{ not json"));

    [Fact]
    public void Schema_without_fields_throws()
        => Assert.Throws<MigrationImportException>(() => Survey123LayerImporter.Import("""{ "name": "x" }"""));

    [Fact]
    public void Blank_input_throws_argument_exception()
        => Assert.Throws<ArgumentException>(() => Survey123LayerImporter.Import("  "));
}
