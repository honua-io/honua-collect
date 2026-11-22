// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Collect.Core.Enterprise;

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
