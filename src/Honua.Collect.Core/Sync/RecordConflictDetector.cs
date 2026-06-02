using System.Collections;
using System.Text.Json;
using Honua.Collect.Core.Field;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// Compares a locally-edited record against the server's current version and
/// produces a field-level <see cref="RecordConflict"/> for manual review
/// (BACKLOG S1). Comparison uses the same loose value coercion as the form
/// runtime, and treats two "missing" values (null, empty string, empty list) as
/// equal so a blank-vs-null difference is never reported as a conflict.
/// </summary>
public static class RecordConflictDetector
{
    /// <summary>Detects the fields that differ between two versions of a record.</summary>
    /// <param name="form">Form definition supplying field order and labels.</param>
    /// <param name="local">The locally-edited record.</param>
    /// <param name="server">The server's current version of the record.</param>
    /// <returns>A conflict describing the differing fields (possibly empty).</returns>
    public static RecordConflict Detect(FormDefinition form, FieldRecord local, FieldRecord server)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(server);

        var conflicts = new List<FieldConflict>();

        foreach (var field in form.Sections.SelectMany(s => s.Fields))
        {
            // Calculated fields are derived from other values, so a difference is
            // a symptom, not an independent conflict to resolve.
            if (field.Type == FormFieldType.Calculated)
            {
                continue;
            }

            local.Values.TryGetValue(field.FieldId, out var localValue);
            server.Values.TryGetValue(field.FieldId, out var serverValue);

            var localMissing = IsMissing(localValue);
            var serverMissing = IsMissing(serverValue);

            if (localMissing && serverMissing)
            {
                continue;
            }

            if (localMissing != serverMissing || !ValuesMatch(localValue, serverValue))
            {
                conflicts.Add(new FieldConflict(field.FieldId, field.Label, localValue, serverValue));
            }
        }

        return new RecordConflict(local, server, conflicts);
    }

    private static bool ValuesMatch(object? left, object? right)
    {
        // Compare ordered collections element-by-element (e.g. multi-choice).
        if (left is IEnumerable leftSeq and not string && right is IEnumerable rightSeq and not string)
        {
            var leftItems = leftSeq.Cast<object?>().ToList();
            var rightItems = rightSeq.Cast<object?>().ToList();
            return leftItems.Count == rightItems.Count
                && leftItems.Zip(rightItems, FieldValues.AreEqual).All(equal => equal);
        }

        return FieldValues.AreEqual(left, right);
    }

    private static bool IsMissing(object? value) => value switch
    {
        null => true,
        string text => string.IsNullOrWhiteSpace(text),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => true,
        JsonElement { ValueKind: JsonValueKind.Array } element => !element.EnumerateArray().Any(),
        IEnumerable collection and not string => !collection.Cast<object?>().Any(),
        _ => false,
    };
}
