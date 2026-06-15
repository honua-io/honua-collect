using System.Buffers.Binary;
using Honua.Collect.Core.Export;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Tests.Export;

public class GeoPackageExporterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gpkg-test-{Guid.NewGuid():N}.gpkg");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

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

    private SqliteConnection OpenExport(params FieldRecord[] records)
    {
        File.WriteAllBytes(_path, GeoPackageExporter.Export(Form(), records));
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Output_is_a_valid_geopackage_sqlite_file()
    {
        using var c = OpenExport(Record("r1", "Alpha", new FieldGeoPoint(10, 20)));

        // The GeoPackage magic: application_id == "GPKG".
        Assert.Equal(0x47504B47, Scalar(c, "PRAGMA application_id;"));
        Assert.Equal(1, Scalar(c, "SELECT COUNT(*) FROM gpkg_contents WHERE data_type = 'features' AND srs_id = 4326;"));
        Assert.Equal(1, Scalar(c, "SELECT COUNT(*) FROM gpkg_geometry_columns WHERE geometry_type_name = 'POINT';"));
        Assert.Equal(3, Scalar(c, "SELECT COUNT(*) FROM gpkg_spatial_ref_sys;")); // -1, 0, 4326
    }

    [Fact]
    public void Every_record_becomes_a_feature_row_with_its_attributes()
    {
        using var c = OpenExport(Record("r1", "Alpha", new FieldGeoPoint(10, 20)), Record("r2", "Beta", null));

        Assert.Equal(2, Scalar(c, "SELECT COUNT(*) FROM \"records\";"));

        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT site_name, status FROM \"records\" WHERE record_id = 'r1';";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Alpha", reader.GetString(0));
        Assert.Equal("Submitted", reader.GetString(1));
    }

    [Fact]
    public void Located_record_has_geopackage_point_geometry_decoding_to_lon_lat()
    {
        using var c = OpenExport(Record("r1", "Alpha", new FieldGeoPoint(10, 20))); // lat 10, lon 20

        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT geom FROM \"records\" WHERE record_id = 'r1';";
        var blob = (byte[])cmd.ExecuteScalar()!;

        // GP header: magic + srs.
        Assert.Equal((byte)'G', blob[0]);
        Assert.Equal((byte)'P', blob[1]);
        Assert.Equal(4326, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4, 4)));

        // WKB point: type 1, then X (lon) and Y (lat).
        var wkb = blob.AsSpan(8);
        Assert.Equal(0x01, wkb[0]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(wkb.Slice(1, 4)));
        Assert.Equal(20.0, BinaryPrimitives.ReadDoubleLittleEndian(wkb.Slice(5, 8)));  // longitude
        Assert.Equal(10.0, BinaryPrimitives.ReadDoubleLittleEndian(wkb.Slice(13, 8))); // latitude
    }

    [Fact]
    public void Record_without_location_has_null_geometry()
    {
        using var c = OpenExport(Record("r2", "Beta", null));
        Assert.Equal(1, Scalar(c, "SELECT COUNT(*) FROM \"records\" WHERE record_id = 'r2' AND geom IS NULL;"));
    }

    [Fact]
    public void Guards_null_and_blank_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => GeoPackageExporter.Export(null!, []));
        Assert.Throws<ArgumentNullException>(() => GeoPackageExporter.Export(Form(), null!));
        Assert.Throws<ArgumentException>(() => GeoPackageExporter.Export(Form(), [], "  "));
    }
}
