using System.Text;

namespace Honua.Collect.Core.Migration;

/// <summary>
/// Derives a stable, identifier-safe form id from a free-text source name (an Esri
/// layer name, a Fulcrum app name) when the caller doesn't supply one. Lower-cases,
/// replaces runs of non-alphanumeric characters with a single underscore, and trims
/// leading/trailing underscores so the result is a valid, predictable form id.
/// </summary>
internal static class Slug
{
    /// <summary>Slugifies a name; returns <c>"imported_form"</c> for an empty result.</summary>
    /// <param name="name">The source name.</param>
    /// <returns>A lower-cased, underscore-separated identifier.</returns>
    public static string From(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "imported_form";
        }

        var builder = new StringBuilder(name.Length);
        var lastWasSeparator = false;
        foreach (var ch in name.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('_');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('_');
        return slug.Length == 0 ? "imported_form" : slug;
    }
}
