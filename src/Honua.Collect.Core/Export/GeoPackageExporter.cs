using System.Buffers.Binary;
using System.Globalization;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Export;

/// <summary>
/// Exports captured records to an OGC GeoPackage (BACKLOG R2) — a single-file,
/// SQLite-based GIS format that QGIS, ArcGIS, and most tooling read natively. A
/// point feature table holds each record's location as GeoPackageBinary geometry
/// with its field values as attribute columns; records without a location are
/// written with null geometry so none are dropped.
/// </summary>
/// <remarks>
/// Binary output (a <see cref="byte"/> array), unlike the text formats on
/// <see cref="RecordExporter"/>; it reuses that type's shared value formatting so
/// attribute strings match the CSV/GeoJSON/KML exports.
/// </remarks>
public static class GeoPackageExporter
{
    private const int Wgs84 = 4326;
    private const string GeometryColumn = "geom";

    // "GPKG" little-endian, per the GeoPackage application_id requirement.
    private const int ApplicationId = 0x47504B47;
    private const int UserVersion = 10300; // GeoPackage 1.3

    /// <summary>Exports records to a GeoPackage file as a byte array.</summary>
    /// <param name="form">Form whose fields define the attribute columns.</param>
    /// <param name="records">Records to export.</param>
    /// <param name="tableName">The feature table name.</param>
    /// <returns>The GeoPackage (.gpkg) file bytes.</returns>
    public static byte[] Export(FormDefinition form, IEnumerable<FieldRecord> records, string tableName = "records")
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var fields = form.Sections.SelectMany(s => s.Fields).ToList();
        var recordList = records.ToList();

        var path = Path.Combine(Path.GetTempPath(), $"gpkg-{Guid.NewGuid():N}.gpkg");
        try
        {
            WriteDatabase(path, tableName, fields, recordList);
            return File.ReadAllBytes(path);
        }
        finally
        {
            SqliteConnection.ClearAllPools(); // release the file handle so it can be deleted
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void WriteDatabase(string path, string tableName, IReadOnlyList<FormField> fields, IReadOnlyList<FieldRecord> records)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();

        Execute(connection, $"PRAGMA application_id = {ApplicationId};");
        Execute(connection, $"PRAGMA user_version = {UserVersion};");
        CreateMetadataTables(connection);
        SeedSpatialRefSys(connection);

        var quotedTable = Quote(tableName);
        var columnDefs = string.Join(", ", fields.Select(f => $"{Quote(f.FieldId)} TEXT"));
        Execute(connection,
            $"CREATE TABLE {quotedTable} (id INTEGER PRIMARY KEY AUTOINCREMENT, {Quote(GeometryColumn)} BLOB, " +
            $"record_id TEXT, status TEXT{(columnDefs.Length == 0 ? string.Empty : ", " + columnDefs)});");

        RegisterFeatureTable(connection, tableName, records);
        InsertFeatures(connection, tableName, fields, records);
    }

    private static void CreateMetadataTables(SqliteConnection connection)
    {
        Execute(connection,
            "CREATE TABLE gpkg_spatial_ref_sys (srs_name TEXT NOT NULL, srs_id INTEGER PRIMARY KEY, " +
            "organization TEXT NOT NULL, organization_coordsys_id INTEGER NOT NULL, definition TEXT NOT NULL, description TEXT);");

        Execute(connection,
            "CREATE TABLE gpkg_contents (table_name TEXT PRIMARY KEY, data_type TEXT NOT NULL, identifier TEXT UNIQUE, " +
            "description TEXT DEFAULT '', last_change TEXT NOT NULL, min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE, " +
            "srs_id INTEGER, CONSTRAINT fk_gc_r_srs_id FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id));");

        Execute(connection,
            "CREATE TABLE gpkg_geometry_columns (table_name TEXT NOT NULL, column_name TEXT NOT NULL, " +
            "geometry_type_name TEXT NOT NULL, srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL, " +
            "CONSTRAINT pk_geom_cols PRIMARY KEY (table_name, column_name));");
    }

    private static void SeedSpatialRefSys(SqliteConnection connection)
    {
        // The three rows the GeoPackage spec requires, plus WGS 84.
        Execute(connection,
            "INSERT INTO gpkg_spatial_ref_sys VALUES " +
            "('Undefined cartesian SRS', -1, 'NONE', -1, 'undefined', 'undefined cartesian coordinate reference system'), " +
            "('Undefined geographic SRS', 0, 'NONE', 0, 'undefined', 'undefined geographic coordinate reference system'), " +
            "('WGS 84 geodetic', 4326, 'EPSG', 4326, " +
            "'GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0]," +
            "UNIT[\"degree\",0.0174532925199433]]', 'longitude/latitude on the WGS 84 ellipsoid');");
    }

    private static void RegisterFeatureTable(SqliteConnection connection, string tableName, IReadOnlyList<FieldRecord> records)
    {
        var located = records.Where(r => r.Location is not null).Select(r => r.Location!).ToList();
        var bbox = located.Count == 0
            ? (MinX: (double?)null, MinY: (double?)null, MaxX: (double?)null, MaxY: (double?)null)
            : (located.Min(p => p.Longitude), located.Min(p => p.Latitude), located.Max(p => p.Longitude), located.Max(p => p.Latitude));

        using (var contents = connection.CreateCommand())
        {
            contents.CommandText =
                "INSERT INTO gpkg_contents (table_name, data_type, identifier, last_change, min_x, min_y, max_x, max_y, srs_id) " +
                "VALUES ($t, 'features', $t, $now, $minx, $miny, $maxx, $maxy, $srs);";
            contents.Parameters.AddWithValue("$t", tableName);
            contents.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            contents.Parameters.AddWithValue("$minx", (object?)bbox.MinX ?? DBNull.Value);
            contents.Parameters.AddWithValue("$miny", (object?)bbox.MinY ?? DBNull.Value);
            contents.Parameters.AddWithValue("$maxx", (object?)bbox.MaxX ?? DBNull.Value);
            contents.Parameters.AddWithValue("$maxy", (object?)bbox.MaxY ?? DBNull.Value);
            contents.Parameters.AddWithValue("$srs", Wgs84);
            contents.ExecuteNonQuery();
        }

        using var geomCols = connection.CreateCommand();
        geomCols.CommandText =
            "INSERT INTO gpkg_geometry_columns VALUES ($t, $col, 'POINT', $srs, 0, 0);";
        geomCols.Parameters.AddWithValue("$t", tableName);
        geomCols.Parameters.AddWithValue("$col", GeometryColumn);
        geomCols.Parameters.AddWithValue("$srs", Wgs84);
        geomCols.ExecuteNonQuery();
    }

    private static void InsertFeatures(SqliteConnection connection, string tableName, IReadOnlyList<FormField> fields, IReadOnlyList<FieldRecord> records)
    {
        using var transaction = connection.BeginTransaction();
        var columns = new[] { GeometryColumn, "record_id", "status" }.Concat(fields.Select(f => f.FieldId)).ToList();
        var parameterNames = columns.Select((_, i) => $"$p{i}").ToList();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO {Quote(tableName)} ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", parameterNames)});";
        foreach (var name in parameterNames)
        {
            command.Parameters.Add(command.CreateParameter()).ParameterName = name;
        }

        foreach (var record in records)
        {
            command.Parameters[0].Value = record.Location is { } location
                ? EncodePoint(location.Longitude, location.Latitude)
                : DBNull.Value;
            command.Parameters[1].Value = record.RecordId;
            command.Parameters[2].Value = record.Status.ToString();
            for (var i = 0; i < fields.Count; i++)
            {
                command.Parameters[i + 3].Value = RecordExporter.CellText(record, fields[i]);
            }

            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Encodes a point as GeoPackageBinary: the GP header (magic, version, flags,
    /// srs_id) followed by little-endian WKB for a 2D point.
    /// </summary>
    private static byte[] EncodePoint(double longitude, double latitude)
    {
        var blob = new byte[8 + 21];
        blob[0] = (byte)'G';
        blob[1] = (byte)'P';
        blob[2] = 0;          // version
        blob[3] = 0x01;       // flags: little-endian header, no envelope, non-empty
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4, 4), Wgs84);

        var wkb = blob.AsSpan(8);
        wkb[0] = 0x01;        // WKB little-endian
        BinaryPrimitives.WriteUInt32LittleEndian(wkb.Slice(1, 4), 1); // geometry type: Point
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.Slice(5, 8), longitude);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.Slice(13, 8), latitude);
        return blob;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
