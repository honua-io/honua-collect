namespace Honua.Collect.Core.Export;

/// <summary>
/// Bulk export formats for captured records (BACKLOG R2). Shapefile/GeoPackage
/// export builds on the same field flattening and is layered separately.
/// </summary>
public enum ExportFormat
{
    /// <summary>Comma-separated values, one row per record.</summary>
    Csv,

    /// <summary>RFC 7946 GeoJSON <c>FeatureCollection</c>.</summary>
    GeoJson,

    /// <summary>OGC KML 2.2 <c>Document</c> of placemarks (Google Earth / GIS).</summary>
    Kml,

    /// <summary>
    /// Microsoft Excel <c>.xlsx</c> workbook, one row per record with typed cells.
    /// Binary output via <see cref="ExcelExporter"/> (not the text
    /// <see cref="RecordExporter.Export(Honua.Sdk.Field.Forms.FormDefinition, System.Collections.Generic.IEnumerable{Honua.Sdk.Field.Records.FieldRecord}, ExportFormat)"/>).
    /// </summary>
    Xlsx,
}
