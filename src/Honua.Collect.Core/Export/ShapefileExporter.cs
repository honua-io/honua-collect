using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Export;

/// <summary>
/// Exports captured records to an ESRI Shapefile (BACKLOG R2), bundled as a zip of
/// the <c>.shp</c> (point geometry), <c>.shx</c> (index) and <c>.dbf</c>
/// (attributes) members. Shapefile is still the lingua franca of government and
/// utility GIS, so first-class export lowers switching cost. Records without a
/// location are written as null shapes so none are dropped.
/// </summary>
public static class ShapefileExporter
{
    private const int FileCode = 9994;
    private const int Version = 1000;
    private const int PointShapeType = 1;
    private const int NullShapeType = 0;

    /// <summary>Exports records to a zipped Shapefile (.shp/.shx/.dbf).</summary>
    /// <param name="form">Form whose fields define the dBASE attribute columns.</param>
    /// <param name="records">Records to export.</param>
    /// <param name="layerName">Base name for the member files inside the zip.</param>
    /// <returns>Zip archive bytes containing the Shapefile members.</returns>
    public static byte[] Export(FormDefinition form, IEnumerable<FieldRecord> records, string layerName = "records")
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        var fields = form.Sections.SelectMany(s => s.Fields).ToList();
        var recordList = records.ToList();

        var shp = BuildShp(recordList);
        var shx = BuildShx(recordList);
        var dbf = BuildDbf(fields, recordList);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, $"{layerName}.shp", shp);
            WriteEntry(zip, $"{layerName}.shx", shx);
            WriteEntry(zip, $"{layerName}.dbf", dbf);
        }

        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(content);
    }

    // --- .shp main file -------------------------------------------------------

    private static byte[] BuildShp(IReadOnlyList<FieldRecord> records)
    {
        var body = new MemoryStream();
        var recordNumber = 1;
        Span<byte> header = stackalloc byte[8];
        foreach (var record in records)
        {
            var content = ShapeContent(record);
            BinaryPrimitives.WriteInt32BigEndian(header[..4], recordNumber++);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], content.Length / 2); // content length in 16-bit words
            body.Write(header);
            body.Write(content);
        }

        var bodyBytes = body.ToArray();
        var bounds = Bounds(records);
        return WithHeader(bodyBytes, bounds);
    }

    private static byte[] ShapeContent(FieldRecord record)
    {
        if (record.Location is not { } location)
        {
            var nullShape = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(nullShape, NullShapeType);
            return nullShape;
        }

        var point = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(point.AsSpan(0, 4), PointShapeType);
        BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan(4, 8), location.Longitude);  // X
        BinaryPrimitives.WriteDoubleLittleEndian(point.AsSpan(12, 8), location.Latitude);   // Y
        return point;
    }

    private static byte[] WithHeader(byte[] body, (double MinX, double MinY, double MaxX, double MaxY) bounds)
    {
        var file = new byte[100 + body.Length];
        WriteMainHeader(file, file.Length, bounds);
        body.CopyTo(file, 100);
        return file;
    }

    // --- .shx index file ------------------------------------------------------

    private static byte[] BuildShx(IReadOnlyList<FieldRecord> records)
    {
        var file = new byte[100 + (records.Count * 8)];
        var offsetWords = 50; // record offsets are measured in 16-bit words from file start; header is 50 words
        var index = 100;
        foreach (var record in records)
        {
            var contentWords = ShapeContent(record).Length / 2;
            BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(index, 4), offsetWords);
            BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(index + 4, 4), contentWords);
            offsetWords += 4 + contentWords; // 4-word record header + content
            index += 8;
        }

        WriteMainHeader(file, file.Length, Bounds(records));
        return file;
    }

    // The 100-byte header shared by .shp and .shx. Length is the total file length in 16-bit words.
    private static void WriteMainHeader(byte[] file, int fileLengthBytes, (double MinX, double MinY, double MaxX, double MaxY) bounds)
    {
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(0, 4), FileCode);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(24, 4), fileLengthBytes / 2);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(28, 4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(32, 4), PointShapeType);
        BinaryPrimitives.WriteDoubleLittleEndian(file.AsSpan(36, 8), bounds.MinX);
        BinaryPrimitives.WriteDoubleLittleEndian(file.AsSpan(44, 8), bounds.MinY);
        BinaryPrimitives.WriteDoubleLittleEndian(file.AsSpan(52, 8), bounds.MaxX);
        BinaryPrimitives.WriteDoubleLittleEndian(file.AsSpan(60, 8), bounds.MaxY);
        // Z and M ranges (68..99) stay zero for 2D points.
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyList<FieldRecord> records)
    {
        var points = records.Where(r => r.Location is not null).Select(r => r.Location!).ToList();
        return points.Count == 0
            ? (0, 0, 0, 0)
            : (points.Min(p => p.Longitude), points.Min(p => p.Latitude), points.Max(p => p.Longitude), points.Max(p => p.Latitude));
    }

    // --- .dbf attribute file (dBASE III) --------------------------------------

    private static byte[] BuildDbf(IReadOnlyList<FormField> fields, IReadOnlyList<FieldRecord> records)
    {
        var columns = BuildColumns(fields);
        var recordLength = 1 + columns.Sum(c => c.Length); // 1 = deletion flag
        var headerLength = 32 + (columns.Count * 32) + 1;

        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[32];
        header[0] = 0x03; // dBASE III, no memo
        header[1] = 99; // last-update year (1999) — fixed so output is deterministic
        header[2] = 1;
        header[3] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), records.Count);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(8, 2), (short)headerLength);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(10, 2), (short)recordLength);
        output.Write(header);

        Span<byte> descriptor = stackalloc byte[32];
        foreach (var column in columns)
        {
            descriptor.Clear();
            var nameBytes = Encoding.ASCII.GetBytes(column.Name);
            nameBytes.AsSpan(0, Math.Min(nameBytes.Length, 11)).CopyTo(descriptor);
            descriptor[11] = (byte)'C'; // character field
            descriptor[16] = (byte)column.Length;
            output.Write(descriptor);
        }

        output.WriteByte(0x0D); // field-descriptor terminator

        foreach (var record in records)
        {
            output.WriteByte(0x20); // not deleted
            foreach (var column in columns)
            {
                output.Write(FixedAscii(column.Value(record), column.Length));
            }
        }

        output.WriteByte(0x1A); // EOF
        return output.ToArray();
    }

    private static List<DbfColumn> BuildColumns(IReadOnlyList<FormField> fields)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<DbfColumn>
        {
            new("record_id", 50, r => r.RecordId),
            new("status", 20, r => r.Status.ToString()),
        };
        columns.ForEach(c => used.Add(c.Name));

        foreach (var field in fields)
        {
            columns.Add(new DbfColumn(UniqueName(field.FieldId, used), 80, r => RecordExporter.CellText(r, field)));
        }

        return columns;
    }

    // dBASE field names are <=10 ASCII chars and unique; sanitise and de-duplicate.
    private static string UniqueName(string fieldId, HashSet<string> used)
    {
        var cleaned = new string(fieldId.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
        if (cleaned.Length == 0)
        {
            cleaned = "field";
        }

        var baseName = cleaned.Length > 10 ? cleaned[..10] : cleaned;
        var name = baseName;
        var suffix = 1;
        while (!used.Add(name))
        {
            var tag = (suffix++).ToString(CultureInfo.InvariantCulture);
            name = string.Concat(baseName.AsSpan(0, Math.Min(baseName.Length, 10 - tag.Length)), tag);
        }

        return name;
    }

    private static byte[] FixedAscii(string value, int length)
    {
        var buffer = new byte[length];
        buffer.AsSpan().Fill((byte)' ');
        for (var i = 0; i < value.Length && i < length; i++)
        {
            var c = value[i];
            buffer[i] = char.IsAscii(c) ? (byte)c : (byte)'?';
        }

        return buffer;
    }

    private sealed record DbfColumn(string Name, int Length, Func<FieldRecord, string> Value);
}
