// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Honua.Collect.Core.Enterprise;

public static partial class SecretScrubber
{
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
