using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Export;

/// <summary>
/// A single column in a field-mapped tabular export (epic #39): which source value
/// to pull and the header to write it under. The source is either a form
/// <see cref="FormField.FieldId"/> or one of the fixed record columns
/// (<c>record_id</c>, <c>status</c>, <c>latitude</c>, <c>longitude</c>), letting an
/// export be subset and renamed to a downstream system's schema.
/// </summary>
/// <param name="Source">The form field id, or a fixed column name.</param>
/// <param name="Header">The column header to emit. Defaults to <paramref name="Source"/>.</param>
public sealed record ExportColumn(string Source, string? Header = null)
{
    /// <summary>The header actually written for this column.</summary>
    public string ResolvedHeader => string.IsNullOrEmpty(Header) ? Source : Header;
}

/// <summary>
/// An ordered set of <see cref="ExportColumn"/> defining a field-mapped tabular
/// export: only the listed columns are emitted, in the listed order, under the
/// mapped headers. Lets the same record set export to a fixed downstream schema
/// without restructuring the form.
/// </summary>
/// <param name="Columns">The columns to emit, in order.</param>
public sealed record ExportFieldMapping(IReadOnlyList<ExportColumn> Columns)
{
    /// <summary>The fixed record columns selectable as a mapping source.</summary>
    public static IReadOnlyList<string> FixedSources { get; } =
        ["record_id", "status", "latitude", "longitude"];

    /// <summary>Builds a mapping from <c>(source, header)</c> pairs.</summary>
    /// <param name="columns">Source/header pairs, in emit order.</param>
    /// <returns>A field mapping.</returns>
    public static ExportFieldMapping Of(params (string Source, string Header)[] columns)
        => new(columns.Select(c => new ExportColumn(c.Source, c.Header)).ToList());
}
