using System.Globalization;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Forms.Authoring;

/// <summary>
/// Imports an XLSForm definition (the de-facto authoring format shared by
/// Survey123, KoboToolbox, and ODK) into the SDK <see cref="FormDefinition"/>
/// contract (BACKLOG F1). The host parses the spreadsheet into rows; this maps
/// types, groups/repeats, choice lists, <c>required</c>, <c>relevant</c>
/// visibility, and <c>calculate</c> expressions.
/// </summary>
public static class XlsFormImporter
{
    /// <summary>Imports survey and choices rows into a form definition.</summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="name">Form display name.</param>
    /// <param name="survey">Rows from the <c>survey</c> sheet, in order.</param>
    /// <param name="choices">Rows from the <c>choices</c> sheet.</param>
    /// <returns>The imported form definition.</returns>
    public static FormDefinition Import(
        string formId,
        string name,
        IEnumerable<XlsFormSurveyRow> survey,
        IEnumerable<XlsFormChoiceRow> choices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(survey);
        ArgumentNullException.ThrowIfNull(choices);

        var choicesByList = choices
            .GroupBy(c => c.ListName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<FieldChoice>)g
                    .Select(c => new FieldChoice { Value = c.Name, Label = c.Label })
                    .ToList(),
                StringComparer.Ordinal);

        var sections = new List<FormSection>();
        var root = new SectionBuilder("form", name, repeatable: false);
        var stack = new Stack<SectionBuilder>();
        stack.Push(root);

        foreach (var row in survey)
        {
            var (kind, listName) = ParseType(row.Type);

            switch (kind)
            {
                case "begin group":
                case "begin repeat":
                    var pushed = new SectionBuilder(
                        row.Name ?? $"section{sections.Count}",
                        row.Label ?? row.Name ?? "Section",
                        repeatable: kind == "begin repeat");
                    stack.Push(pushed);
                    break;

                case "end group":
                case "end repeat":
                    var finished = stack.Pop();
                    sections.Add(finished.Build());
                    break;

                default:
                    var field = BuildField(row, kind, listName, choicesByList);
                    if (field is not null)
                    {
                        stack.Peek().Fields.Add(field);
                    }

                    break;
            }
        }

        // Emit the root last so ungrouped fields keep their authored position
        // ahead of trailing groups only when it actually holds fields.
        if (root.Fields.Count > 0)
        {
            sections.Insert(0, root.Build());
        }

        return new FormDefinition { FormId = formId, Name = name, Sections = sections };
    }

    private static FormField? BuildField(
        XlsFormSurveyRow row,
        string kind,
        string? listName,
        IReadOnlyDictionary<string, IReadOnlyList<FieldChoice>> choicesByList)
    {
        // Notes carry no value; unnamed rows can't be fields.
        if (kind is "note" || string.IsNullOrWhiteSpace(row.Name))
        {
            return null;
        }

        if (!TryMapType(kind, out var fieldType))
        {
            return null; // unsupported type (e.g. metadata) — skip
        }

        var choices = listName is not null && choicesByList.TryGetValue(listName, out var list)
            ? list
            : (IReadOnlyList<FieldChoice>)[];

        return new FormField
        {
            FieldId = row.Name,
            Label = row.Label ?? row.Name,
            Type = fieldType,
            Required = IsTrue(row.Required),
            Choices = choices,
            HelpText = string.IsNullOrWhiteSpace(row.Hint) ? null : row.Hint,
            CalculatedExpression = fieldType == FormFieldType.Calculated && !string.IsNullOrWhiteSpace(row.Calculation)
                ? row.Calculation
                : null,
            VisibilityRule = ParseRelevant(row.Relevant),
        };
    }

    private static (string Kind, string? ListName) ParseType(string type)
    {
        var trimmed = type.Trim();
        if (trimmed.StartsWith("select_one", StringComparison.Ordinal) ||
            trimmed.StartsWith("select_multiple", StringComparison.Ordinal))
        {
            var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return (parts[0], parts.Length > 1 ? parts[1] : null);
        }

        return (trimmed, null);
    }

    private static bool TryMapType(string kind, out FormFieldType fieldType)
    {
        fieldType = kind switch
        {
            "text" => FormFieldType.Text,
            "integer" or "decimal" or "range" => FormFieldType.Numeric,
            "date" => FormFieldType.Date,
            "time" => FormFieldType.Time,
            "datetime" or "dateTime" => FormFieldType.DateTime,
            "select_one" => FormFieldType.SingleChoice,
            "select_multiple" => FormFieldType.MultipleChoice,
            "geopoint" => FormFieldType.Location,
            "geotrace" => FormFieldType.GeoTrace,
            "geoshape" => FormFieldType.GeoShape,
            "image" => FormFieldType.Photo,
            "audio" => FormFieldType.Audio,
            "video" => FormFieldType.Video,
            "barcode" => FormFieldType.Barcode,
            "calculate" => FormFieldType.Calculated,
            _ => (FormFieldType)(-1),
        };

        return (int)fieldType >= 0;
    }

    private static bool IsTrue(string? value)
        => value is not null && (value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value == "1");

    private static FieldVisibilityRule? ParseRelevant(string? relevant)
    {
        if (string.IsNullOrWhiteSpace(relevant))
        {
            return null;
        }

        // Supports the common single-comparison form: ${field} OP value
        foreach (var (token, op) in Operators)
        {
            var idx = relevant.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            var left = relevant[..idx].Trim();
            var right = relevant[(idx + token.Length)..].Trim();

            if (!left.StartsWith("${", StringComparison.Ordinal) || !left.EndsWith('}'))
            {
                continue;
            }

            var dependsOn = left[2..^1].Trim();
            return new FieldVisibilityRule
            {
                DependsOnFieldId = dependsOn,
                Operator = op,
                MatchValue = ParseLiteral(right),
            };
        }

        return null;
    }

    // Longer tokens first so '!=' is matched before '='.
    private static readonly (string Token, ComparisonOperator Op)[] Operators =
    [
        ("!=", ComparisonOperator.NotEquals),
        (">", ComparisonOperator.GreaterThan),
        ("<", ComparisonOperator.LessThan),
        ("=", ComparisonOperator.Equals),
    ];

    private static object? ParseLiteral(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
        {
            return value[1..^1];
        }

        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return value;
    }

    private sealed class SectionBuilder(string id, string label, bool repeatable)
    {
        public List<FormField> Fields { get; } = [];

        public FormSection Build() => new()
        {
            SectionId = id,
            Label = label,
            Repeatable = repeatable,
            Fields = Fields,
        };
    }
}
