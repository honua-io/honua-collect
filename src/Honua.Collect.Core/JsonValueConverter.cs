using System.Text.Json;

namespace Honua.Collect.Core;

/// <summary>
/// Decodes a <see cref="JsonElement"/> into a boxed CLR value for storage in a
/// record's attribute bag (<c>Dictionary&lt;string, object?&gt;</c>).
/// </summary>
/// <remarks>
/// This is the single canonical wire-value decoder shared by every JSON ingest
/// path (GeoServices query pulls and the Fulcrum/GeoJSON importer). Both paths
/// populate the same <c>FieldRecord.Values</c> bag, which then flows into the
/// shared conflict/diff/export code, so they must decode the same wire value to
/// the same CLR type — otherwise the same field would compare unequal depending
/// on which path ingested it, producing spurious diffs.
/// <para>
/// The integral-vs-double boxing rule lives here and only here: an integral JSON
/// number must box as <see cref="long"/>, not widen to <see cref="double"/>. A
/// single <c>TryGetInt64(...) ? l : value.GetDouble()</c> ternary would widen the
/// <c>long</c> branch to <c>double</c> through the conditional's common type
/// (turning <c>62</c> into <c>62.0</c>), so each numeric branch is boxed
/// separately.
/// </para>
/// </remarks>
public static class JsonValueConverter
{
    /// <summary>Decodes a JSON element to a boxed CLR value.</summary>
    /// <param name="value">The element to decode.</param>
    /// <param name="includeArrays">
    /// When <see langword="true"/>, a JSON array is projected to a
    /// <c>string?[]</c> (string elements verbatim, others as raw JSON, nulls
    /// dropped). When <see langword="false"/>, an array falls through to its raw
    /// JSON text, matching the behavior callers that do not expect multi-value
    /// fields rely on.
    /// </param>
    /// <returns>The boxed CLR value, or <see langword="null"/> for JSON null.</returns>
    public static object? ToClrValue(JsonElement value, bool includeArrays = false) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        // Box each numeric branch separately: a unified ternary would widen the
        // integer branch to double, losing the integral type the encoding round-trips
        // (keeps 62 a long, not 62.0).
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : (object)value.GetDouble(),
        JsonValueKind.Array when includeArrays => value.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
            .Where(s => s is not null)
            .ToArray(),
        _ => value.GetRawText(),
    };
}
