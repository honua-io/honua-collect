using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// Serializes an <see cref="AuthSession"/> to and from a compact JSON string for
/// secure-storage persistence. Lives in Core (not the platform layer) so the
/// round-trip — and the defensive handling of absent/garbage payloads — is
/// unit-tested without a device secure store. A payload missing the identifying
/// fields (user id / access token) is treated as unreadable rather than producing
/// a half-formed session.
/// </summary>
public static class AuthSessionSerializer
{
    private sealed record Dto(
        [property: JsonPropertyName("uid")] string? UserId,
        [property: JsonPropertyName("name")] string? DisplayName,
        [property: JsonPropertyName("at")] string? AccessToken,
        [property: JsonPropertyName("rt")] string? RefreshToken,
        [property: JsonPropertyName("exp")] DateTimeOffset ExpiresAtUtc,
        [property: JsonPropertyName("scopes")] string[]? Scopes,
        [property: JsonPropertyName("roles")] string[]? Roles);

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes a session to a JSON string.</summary>
    /// <param name="session">The session to serialize.</param>
    /// <returns>A JSON representation suitable for secure-storage persistence.</returns>
    public static string Serialize(AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var dto = new Dto(
            session.UserId,
            session.DisplayName,
            session.AccessToken,
            session.RefreshToken,
            session.ExpiresAtUtc,
            session.Scopes.ToArray(),
            session.Roles.ToArray());
        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>
    /// Parses a session from JSON, returning null for a null/blank/garbage payload
    /// or one missing the required identifying fields — so a corrupt store can never
    /// resume a malformed session.
    /// </summary>
    /// <param name="json">The stored JSON, or null.</param>
    /// <returns>The parsed session, or null when unreadable.</returns>
    public static AuthSession? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto is null || string.IsNullOrEmpty(dto.UserId) || string.IsNullOrEmpty(dto.AccessToken))
        {
            return null;
        }

        return new AuthSession
        {
            UserId = dto.UserId,
            DisplayName = dto.DisplayName,
            AccessToken = dto.AccessToken,
            RefreshToken = dto.RefreshToken,
            ExpiresAtUtc = dto.ExpiresAtUtc,
            Scopes = new HashSet<string>(dto.Scopes ?? Array.Empty<string>(), StringComparer.Ordinal),
            Roles = new HashSet<string>(dto.Roles ?? Array.Empty<string>(), StringComparer.Ordinal),
        };
    }
}
