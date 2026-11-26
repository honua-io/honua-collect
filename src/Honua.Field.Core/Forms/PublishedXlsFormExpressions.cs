// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Forms;

/// <summary>Adapts the published XLSForm subset to the portable SDK expression evaluator.</summary>
internal sealed partial class PublishedXlsFormExpressions(IEnumerable<string> multipleChoiceFields)
{
    private readonly HashSet<string> _multipleChoiceFields = new(multipleChoiceFields, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("""'(?:(?:'')|[^'])*'|"[^"]*"|(?<field>\$\{(?<id>[A-Za-z_][A-Za-z0-9_-]*)\})|(?<number>[0-9]+(?:\.[0-9]*)?|\.[0-9]+)|(?<name>[A-Za-z_][A-Za-z0-9_-]*)|(?<self>\.)""", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    [GeneratedRegex("""'(?:(?:'')|[^'])*'|(?<selected>\bselected\s*\(\s*(?<value>\$\{[^}]+\}|\.)\s*,\s*(?<code>'(?:''|[^'])*')\s*\))|(?<boolean>\b(?:true|false)\b)(?:\s*\(\s*\))?""", RegexOptions.CultureInvariant)]
    private static partial Regex Calls();

    internal string? Compile(string? expression, string fieldId)
    {
        if (expression is null)
        {
            return null;
        }

        var normalized = Tokens().Replace(expression, token =>
        {
            if (token.Groups["field"].Success && _multipleChoiceFields.Contains(token.Groups["id"].Value))
            {
                return Reference(token.Groups["id"].Value);
            }
            if (token.Groups["self"].Success && _multipleChoiceFields.Contains(fieldId))
            {
                return Reference(fieldId);
            }
            return token.Value switch
            {
                "div" => "/",
                "mod" => "%",
                _ when token.Value[0] == '"' => "'" + token.Value[1..^1].Replace("'", "''", StringComparison.Ordinal) + "'",
                _ => token.Value,
            };
        });

        return Calls().Replace(normalized, token => token.Groups["selected"].Success
            ? $"contains(concat(' ', {token.Groups["value"].Value}, ' '), concat(' ', {token.Groups["code"].Value}, ' '))"
            : token.Groups["boolean"].Success ? token.Groups["boolean"].Value : token.Value);
    }

    internal FieldRecord EvaluationRecord(FieldRecord record, FieldRecord? parent)
    {
        var values = parent is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(parent.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in record.Values)
        {
            values[pair.Key] = pair.Value;
        }
        foreach (string fieldId in _multipleChoiceFields)
        {
            values[Alias(fieldId)] = values.GetValueOrDefault(fieldId) switch
            {
                IEnumerable<string> codes => string.Join(' ', codes),
                JsonElement { ValueKind: JsonValueKind.Array } json =>
                    string.Join(' ', json.EnumerateArray().Select(item => item.GetString())),
                _ => null,
            };
        }
        return new FieldRecord { RecordId = record.RecordId, FormId = record.FormId, Values = values, Media = record.Media };
    }

    private static string Alias(string fieldId) => $"xlsform:{fieldId}";
    private static string Reference(string fieldId) => "${" + Alias(fieldId) + "}";
}
