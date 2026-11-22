// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Text.Json;
using Honua.Collect.Core.Field;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

public static class RecordConflictDetector
{
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

    internal static bool IsMissing(object? value) => value switch
    {
        null => true,
        string text => string.IsNullOrWhiteSpace(text),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => true,
        JsonElement { ValueKind: JsonValueKind.Array } element => !element.EnumerateArray().Any(),
        IEnumerable collection and not string => !collection.Cast<object?>().Any(),
        _ => false,
    };
}
