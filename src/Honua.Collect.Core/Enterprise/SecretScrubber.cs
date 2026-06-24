using System.Text.RegularExpressions;

namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// Scrubs secrets out of free-form audit detail strings before they are persisted
/// (BACKLOG E3). Tokens, passwords, keys and bearer credentials must never land in
/// the durable audit trail. The scrubber is conservative: it redacts on a match
/// rather than trying to preserve the value, since an over-redacted audit note is
/// always preferable to a leaked credential at rest.
/// </summary>
public static partial class SecretScrubber
{
    /// <summary>Placeholder substituted for a redacted secret.</summary>
    public const string Redacted = "***REDACTED***";

    // key=value / key: value where the key names a sensitive field.
    [GeneratedRegex(
        @"(?i)\b(password|passwd|pwd|secret|token|access[_-]?token|refresh[_-]?token|api[_-]?key|apikey|authorization|auth|bearer|client[_-]?secret|private[_-]?key)\b(\s*[:=]\s*|\s+)(""[^""]*""|'[^']*'|\S+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecret();

    // Bearer <token> in an Authorization-style value.
    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    // JWT-shaped tokens (three base64url segments).
    [GeneratedRegex(@"\beyJ[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+", RegexOptions.CultureInvariant)]
    private static partial Regex JwtToken();

    /// <summary>
    /// Returns <paramref name="value"/> with any embedded secrets replaced by
    /// <see cref="Redacted"/>. Null/blank input is returned unchanged.
    /// </summary>
    /// <param name="value">Free-form text that may contain secrets.</param>
    /// <returns>The scrubbed text.</returns>
    public static string? Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var scrubbed = BearerToken().Replace(value, $"Bearer {Redacted}");
        scrubbed = JwtToken().Replace(scrubbed, Redacted);
        scrubbed = KeyValueSecret().Replace(scrubbed, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Redacted}");
        return scrubbed;
    }
}
