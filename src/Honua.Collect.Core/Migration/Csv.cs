namespace Honua.Collect.Core.Migration;

/// <summary>
/// A minimal RFC-4180 CSV reader for migration imports (Fulcrum CSV export): it
/// honors double-quoted fields, escaped <c>""</c> quotes, and embedded
/// commas/newlines inside quotes, and tolerates both <c>\n</c> and <c>\r\n</c> line
/// endings. Kept internal and dependency-free so the importers stay portable.
/// </summary>
internal static class Csv
{
    /// <summary>Parses CSV text into rows of fields. A trailing newline adds no empty row.</summary>
    /// <param name="text">The CSV text.</param>
    /// <returns>The parsed rows; each row is its list of field values.</returns>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        if (string.IsNullOrEmpty(text))
        {
            return rows;
        }

        var field = new System.Text.StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        var sawAnyChar = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++; // consume the escaped quote
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    sawAnyChar = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    sawAnyChar = true;
                    break;
                case '\r':
                    // Swallow; the \n (or end) closes the row.
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    sawAnyChar = false;
                    break;
                default:
                    field.Append(ch);
                    sawAnyChar = true;
                    break;
            }
        }

        // Flush the final field/row when the text didn't end on a newline.
        if (sawAnyChar || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
