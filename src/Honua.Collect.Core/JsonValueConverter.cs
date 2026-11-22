// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Honua.Collect.Core;

public static class JsonValueConverter
{
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
