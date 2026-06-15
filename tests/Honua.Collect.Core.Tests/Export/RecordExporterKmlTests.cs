using System.Xml.Linq;
using Honua.Collect.Core.Export;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Export;

public class RecordExporterKmlTests
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

    private static FormDefinition Form() => new()
    {
        FormId = "field-site",
        Name = "Field Site",
        Sections =
        [
            new FormSection
            {
                SectionId = "details",
                Label = "Site details",
                Fields = [new FormField { FieldId = "site_name", Label = "Site name", Type = FormFieldType.Text }],
            },
        ],
    };

    private static FieldRecord Record(string id, string siteName, FieldGeoPoint? location) => new()
    {
        RecordId = id,
        FormId = "field-site",
        Status = RecordStatus.Submitted,
        Location = location,
        Values = { ["site_name"] = siteName },
    };

    [Fact]
    public void Produces_well_formed_kml_with_a_placemark_per_record()
    {
        var records = new[]
        {
            Record("r1", "Alpha", new FieldGeoPoint(10, 20)), // lat 10, lon 20
            Record("r2", "Beta", null),
        };

        var kml = RecordExporter.ToKml(Form(), records);
        var doc = XDocument.Parse(kml); // throws if not well-formed

        var placemarks = doc.Descendants(Kml + "Placemark").ToList();
        Assert.Equal(2, placemarks.Count);
        Assert.Equal("r1", placemarks[0].Element(Kml + "name")!.Value);
    }

    [Fact]
    public void Located_records_carry_a_point_with_lon_lat_order()
    {
        var kml = RecordExporter.ToKml(Form(), [Record("r1", "Alpha", new FieldGeoPoint(10, 20))]);
        var placemark = XDocument.Parse(kml).Descendants(Kml + "Placemark").Single();

        var coordinates = placemark.Element(Kml + "Point")!.Element(Kml + "coordinates")!.Value;
        Assert.Equal("20,10", coordinates); // lon,lat
    }

    [Fact]
    public void Records_without_a_location_have_no_point()
    {
        var kml = RecordExporter.ToKml(Form(), [Record("r2", "Beta", null)]);
        var placemark = XDocument.Parse(kml).Descendants(Kml + "Placemark").Single();

        Assert.Null(placemark.Element(Kml + "Point"));
    }

    [Fact]
    public void Field_values_and_status_become_extended_data()
    {
        var kml = RecordExporter.ToKml(Form(), [Record("r1", "Alpha", null)]);
        var data = XDocument.Parse(kml).Descendants(Kml + "Data").ToList();

        var byName = data.ToDictionary(d => d.Attribute("name")!.Value, d => d.Element(Kml + "value")!.Value);
        Assert.Equal("Submitted", byName["status"]);
        Assert.Equal("Alpha", byName["site_name"]);
    }

    [Fact]
    public void Special_characters_are_xml_escaped()
    {
        // A value with XML-significant characters round-trips through the parser.
        var kml = RecordExporter.ToKml(Form(), [Record("r1", "A & B <site>", null)]);
        var doc = XDocument.Parse(kml); // would throw if not escaped

        var value = doc.Descendants(Kml + "Data")
            .First(d => d.Attribute("name")!.Value == "site_name")
            .Element(Kml + "value")!.Value;
        Assert.Equal("A & B <site>", value);
    }

    [Fact]
    public void Export_dispatches_kml_to_ToKml()
    {
        var form = Form();
        var records = new[] { Record("r1", "Alpha", new FieldGeoPoint(10, 20)) };

        Assert.Equal(RecordExporter.ToKml(form, records), RecordExporter.Export(form, records, ExportFormat.Kml));
    }

    [Fact]
    public void ToKml_guards_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => RecordExporter.ToKml(null!, []));
        Assert.Throws<ArgumentNullException>(() => RecordExporter.ToKml(Form(), null!));
    }
}
