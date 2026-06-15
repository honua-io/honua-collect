using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Honua.Collect.Core.Export;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Export;

public class ShapefileExporterTests
{
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

    private static Dictionary<string, byte[]> Unzip(byte[] zipBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.Name, e =>
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        });
    }

    [Fact]
    public void Bundles_the_three_shapefile_members()
    {
        var files = Unzip(ShapefileExporter.Export(Form(), [Record("r1", "Alpha", new FieldGeoPoint(10, 20))]));

        Assert.Contains("records.shp", files.Keys);
        Assert.Contains("records.shx", files.Keys);
        Assert.Contains("records.dbf", files.Keys);
    }

    [Fact]
    public void Shp_header_has_the_file_code_and_point_shape_type()
    {
        var shp = Unzip(ShapefileExporter.Export(Form(), [Record("r1", "Alpha", new FieldGeoPoint(10, 20))]))["records.shp"];

        Assert.Equal(9994, BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(0, 4)));   // file code
        Assert.Equal(1000, BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(28, 4))); // version
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(32, 4)));   // shape type Point
        Assert.Equal(shp.Length / 2, BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(24, 4))); // length in words
    }

    [Fact]
    public void First_point_record_decodes_to_lon_lat()
    {
        var shp = Unzip(ShapefileExporter.Export(Form(), [Record("r1", "Alpha", new FieldGeoPoint(10, 20))]))["records.shp"];

        // Record header (8 bytes) then content at offset 108.
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(100, 4)));      // record number 1
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(108, 4)));   // shape type Point
        Assert.Equal(20.0, BinaryPrimitives.ReadDoubleLittleEndian(shp.AsSpan(112, 8))); // X = longitude
        Assert.Equal(10.0, BinaryPrimitives.ReadDoubleLittleEndian(shp.AsSpan(120, 8))); // Y = latitude
    }

    [Fact]
    public void Unlocated_record_is_written_as_a_null_shape()
    {
        var shp = Unzip(ShapefileExporter.Export(Form(), [Record("r2", "Beta", null)]))["records.shp"];

        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(108, 4))); // null shape type
    }

    [Fact]
    public void Shx_indexes_every_record()
    {
        var files = Unzip(ShapefileExporter.Export(Form(),
            [Record("r1", "Alpha", new FieldGeoPoint(10, 20)), Record("r2", "Beta", null)]));
        var shx = files["records.shx"];

        Assert.Equal(100 + (2 * 8), shx.Length);                                   // header + 2 index records
        Assert.Equal(50, BinaryPrimitives.ReadInt32BigEndian(shx.AsSpan(100, 4))); // first record offset (words)
    }

    [Fact]
    public void Dbf_has_the_record_count_and_attribute_values()
    {
        var dbf = Unzip(ShapefileExporter.Export(Form(),
            [Record("r1", "Alpha", new FieldGeoPoint(10, 20)), Record("r2", "Beta", null)]))["records.dbf"];

        Assert.Equal(0x03, dbf[0]); // dBASE III
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(dbf.AsSpan(4, 4))); // record count

        var headerLength = BinaryPrimitives.ReadInt16LittleEndian(dbf.AsSpan(8, 2));
        var recordLength = BinaryPrimitives.ReadInt16LittleEndian(dbf.AsSpan(10, 2));
        // First data record: skip header + 1-byte deletion flag, read the 50-char record_id field.
        var recordId = Encoding.ASCII.GetString(dbf, headerLength + 1, 50).TrimEnd();
        Assert.Equal("r1", recordId);
        Assert.True(recordLength > 50);
    }

    [Fact]
    public void Guards_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => ShapefileExporter.Export(null!, []));
        Assert.Throws<ArgumentNullException>(() => ShapefileExporter.Export(Form(), null!));
        Assert.Throws<ArgumentException>(() => ShapefileExporter.Export(Form(), [], " "));
    }
}
