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
}
