using System.Globalization;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// A <c>where</c>-style attribute predicate for selective sync (BACKLOG S2): a
/// small, SQL-like filter that decides whether a record's attribute
/// <see cref="FieldRecord.Values"/> match, without a database. The same text is
/// also emitted to the GeoServices <c>query</c> endpoint as the <c>where</c>
/// clause so the server pre-filters the pull, and is re-applied locally so the
/// device never trusts the server to have filtered correctly.
/// </summary>
/// <remarks>
/// <para>
/// The grammar is intentionally a Survey123/ArcGIS-style subset:
/// <c>field op value [AND|OR field op value ...]</c> where <c>op</c> is one of
/// <c>= != &lt;&gt; &lt; &lt;= &gt; &gt;=</c>, <c>LIKE</c> (with <c>%</c>
/// wildcards), <c>IN (v1, v2, ...)</c>, or <c>IS NULL</c> / <c>IS NOT NULL</c>.
/// String literals are single-quoted; bare tokens are parsed as numbers. <c>AND</c>
/// binds tighter than <c>OR</c>. Comparisons are culture-invariant; numeric
/// comparisons fall back to a case-insensitive string compare when either side is
/// non-numeric so a text field still filters predictably.
/// </para>
/// <para>
/// This is a filter, not a general SQL engine: parentheses, functions, and joins
/// are out of scope. Anything it cannot parse throws <see cref="FormatException"/>
/// at construction so a bad config fails fast instead of silently syncing
/// everything.
/// </para>
/// </remarks>
public sealed class SyncAttributeFilter
{
    private readonly string _where;
    private readonly Func<FieldRecord, bool> _predicate;

    private SyncAttributeFilter(string where, Func<FieldRecord, bool> predicate)
    {
        _where = where;
        _predicate = predicate;
    }

    /// <summary>The original <c>where</c> text (used as the server query clause).</summary>
    public string Where => _where;

    /// <summary>Parses a <c>where</c> clause into a reusable filter.</summary>
    /// <param name="where">The <c>where</c>-style predicate text.</param>
    /// <returns>The compiled filter.</returns>
    /// <exception cref="FormatException">The clause could not be parsed.</exception>
    public static SyncAttributeFilter Parse(string where)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        var tokens = Tokenize(where);
        var index = 0;
        var predicate = ParseOr(tokens, ref index);
        if (index != tokens.Count)
        {
            throw new FormatException($"Unexpected token '{tokens[index]}' in where clause.");
        }

        return new SyncAttributeFilter(where.Trim(), predicate);
    }

    /// <summary>Whether a record's attributes satisfy the predicate.</summary>
    /// <param name="record">Record to test.</param>
    /// <returns><see langword="true"/> when the record matches.</returns>
    public bool Matches(FieldRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _predicate(record);
    }

    // --- Recursive-descent parser: OR over AND over comparisons. ---

    private static Func<FieldRecord, bool> ParseOr(IReadOnlyList<string> tokens, ref int index)
    {
        var left = ParseAnd(tokens, ref index);
        while (index < tokens.Count && IsKeyword(tokens[index], "OR"))
        {
            index++;
            var right = ParseAnd(tokens, ref index);
            var l = left;
            left = r => l(r) || right(r);
        }

        return left;
    }

    private static Func<FieldRecord, bool> ParseAnd(IReadOnlyList<string> tokens, ref int index)
    {
        var left = ParseComparison(tokens, ref index);
        while (index < tokens.Count && IsKeyword(tokens[index], "AND"))
        {
            index++;
            var right = ParseComparison(tokens, ref index);
            var l = left;
            left = r => l(r) && right(r);
        }

        return left;
    }

    private static Func<FieldRecord, bool> ParseComparison(IReadOnlyList<string> tokens, ref int index)
    {
        if (index >= tokens.Count)
        {
            throw new FormatException("Expected a field name in where clause.");
        }

        var field = tokens[index++];
        if (IsKeyword(field, "AND") || IsKeyword(field, "OR"))
        {
            throw new FormatException($"Expected a field name but found '{field}'.");
        }

        if (index >= tokens.Count)
        {
            throw new FormatException($"Expected an operator after '{field}'.");
        }

        var op = tokens[index++];

        // IS [NOT] NULL
        if (IsKeyword(op, "IS"))
        {
            var negate = index < tokens.Count && IsKeyword(tokens[index], "NOT");
            if (negate)
            {
                index++;
            }

            if (index >= tokens.Count || !IsKeyword(tokens[index], "NULL"))
            {
                throw new FormatException("Expected NULL after IS.");
            }

            index++;
            return r => negate ? Lookup(r, field) is not null : Lookup(r, field) is null;
        }

        // field IN (v1, v2, ...)
        if (IsKeyword(op, "IN"))
        {
            var members = ParseInList(tokens, ref index);
            return r =>
            {
                var value = Lookup(r, field);
                return value is not null && members.Any(m => ValuesEqual(value, m));
            };
        }

        if (index >= tokens.Count)
        {
            throw new FormatException($"Expected a value after '{field} {op}'.");
        }

        var literal = ParseLiteral(tokens[index++]);
        return op switch
        {
            "=" => r => ValuesEqual(Lookup(r, field), literal),
            "!=" or "<>" => r => !ValuesEqual(Lookup(r, field), literal),
            "<" => r => Compare(Lookup(r, field), literal) < 0,
            "<=" => r => Compare(Lookup(r, field), literal) <= 0,
            ">" => r => Compare(Lookup(r, field), literal) > 0,
            ">=" => r => Compare(Lookup(r, field), literal) >= 0,
            _ when IsKeyword(op, "LIKE") => r => Like(Lookup(r, field), literal),
            _ => throw new FormatException($"Unsupported operator '{op}'."),
        };
    }

    private static List<object?> ParseInList(IReadOnlyList<string> tokens, ref int index)
    {
        if (index >= tokens.Count || tokens[index] != "(")
        {
            throw new FormatException("Expected '(' after IN.");
        }

        index++;
        var members = new List<object?>();
        while (index < tokens.Count && tokens[index] != ")")
        {
            if (tokens[index] == ",")
            {
                index++;
                continue;
            }

            members.Add(ParseLiteral(tokens[index++]));
        }

        if (index >= tokens.Count || tokens[index] != ")")
        {
            throw new FormatException("Unterminated IN (...) list.");
        }

        index++;
        if (members.Count == 0)
        {
            throw new FormatException("IN (...) list cannot be empty.");
        }

        return members;
    }

    // --- Value semantics. ---

    private static object? Lookup(FieldRecord record, string field)
        => record.Values.TryGetValue(field, out var value) ? value : null;

    private static object ParseLiteral(string token)
    {
        if (token.Length >= 2 && token[0] == '\'' && token[^1] == '\'')
        {
            return token[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (double.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        if (bool.TryParse(token, out var boolean))
        {
            return boolean;
        }

        return token; // bare identifier-as-string
    }

    private static bool ValuesEqual(object? value, object? literal)
    {
        if (value is null || literal is null)
        {
            return value is null && literal is null;
        }

        if (TryAsDouble(value, out var a) && TryAsDouble(literal, out var b))
        {
            return a.Equals(b);
        }

        return string.Equals(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            Convert.ToString(literal, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    private static int Compare(object? value, object? literal)
    {
        if (TryAsDouble(value, out var a) && TryAsDouble(literal, out var b))
        {
            return a.CompareTo(b);
        }

        // A non-numeric (or absent) value can't satisfy an ordered comparison; sort
        // it below the literal so > / >= reject it and < / <= accept nothing absurd.
        return string.Compare(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            Convert.ToString(literal, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool Like(object? value, object? pattern)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var raw = Convert.ToString(pattern, CultureInfo.InvariantCulture) ?? string.Empty;

        // Translate a SQL LIKE pattern (% = any run, _ = any char) to a regex.
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(raw)
            .Replace("%", ".*", StringComparison.Ordinal)
            .Replace("_", ".", StringComparison.Ordinal) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            text, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool TryAsDouble(object? value, out double result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case double d:
                result = d;
                return true;
            case float or int or long or short or byte or sbyte or uint or ulong or ushort or decimal:
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            case string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool IsKeyword(string token, string keyword)
        => string.Equals(token, keyword, StringComparison.OrdinalIgnoreCase);

    // --- Tokenizer: splits on whitespace, quoted strings, operators, and punctuation. ---

    private static List<string> Tokenize(string where)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < where.Length)
        {
            var c = where[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '\'')
            {
                var start = i++;
                while (i < where.Length)
                {
                    if (where[i] == '\'')
                    {
                        // A doubled '' is an escaped quote, not the terminator.
                        if (i + 1 < where.Length && where[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                tokens.Add(where[start..i]);
                continue;
            }

            if (c is '(' or ')' or ',')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            if (c is '=' or '<' or '>' or '!')
            {
                var start = i++;
                if (i < where.Length && (where[i] == '=' || (c == '<' && where[i] == '>')))
                {
                    i++;
                }

                tokens.Add(where[start..i]);
                continue;
            }

            // Bare token: field name, number, or keyword.
            var tokenStart = i;
            while (i < where.Length
                   && !char.IsWhiteSpace(where[i])
                   && where[i] is not ('(' or ')' or ',' or '=' or '<' or '>' or '!' or '\''))
            {
                i++;
            }

            tokens.Add(where[tokenStart..i]);
        }

        return tokens;
    }
}
