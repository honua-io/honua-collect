// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Enterprise;

public enum AuthSessionState
{
    Active,

    Expiring,

    Expired,
}

public sealed record AuthSession
{
    public required string UserId { get; init; }

    public string? DisplayName { get; init; }

    public required string AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public IReadOnlySet<string> Scopes { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> Roles { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool IsExpired(DateTimeOffset asOfUtc) => asOfUtc >= ExpiresAtUtc;

    public bool NeedsRefresh(DateTimeOffset asOfUtc, TimeSpan skew) => asOfUtc >= ExpiresAtUtc - skew;

    public bool CanRefresh => !string.IsNullOrEmpty(RefreshToken);

    public bool HasScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return Scopes.Contains(scope);
    }

    public bool HasRole(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        return Roles.Contains(role);
    }

    public AuthSessionState StateAt(DateTimeOffset asOfUtc, TimeSpan refreshSkew)
    {
        if (IsExpired(asOfUtc))
        {
            return AuthSessionState.Expired;
        }

        return NeedsRefresh(asOfUtc, refreshSkew) ? AuthSessionState.Expiring : AuthSessionState.Active;
    }
}
