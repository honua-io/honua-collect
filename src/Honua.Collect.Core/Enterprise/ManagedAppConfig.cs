// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Honua.Collect.Core.Enterprise;

public sealed record BrandingConfig(string? AppName, string? PrimaryColorHex, string? LogoPath);

public sealed record DevicePolicy(bool RequireDeviceLock, bool DisableExport, bool DisableScreenshots, int? MaxDraftAgeDays);

public sealed class ManagedAppConfig
{
    public const string ServerUrlKey = "server.url";

    private readonly IReadOnlyDictionary<string, string> _values;

    public ManagedAppConfig(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    public static ManagedAppConfig Empty { get; } = new(new Dictionary<string, string>());

    public bool IsManaged => _values.Count > 0;

    public string? ServerUrl => GetString(ServerUrlKey);

    public BrandingConfig Branding => new(
        GetString("branding.appName"),
        GetString("branding.primaryColor"),
        GetString("branding.logo"));

    public DevicePolicy Policy => new(
        GetBool("policy.requireDeviceLock"),
        GetBool("policy.disableExport"),
        GetBool("policy.disableScreenshots"),
        GetInt("policy.maxDraftAgeDays"));

    public string? GetString(string key, string? fallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _values.TryGetValue(key, out var value) ? value : fallback;
    }

    public bool GetBool(string key, bool fallback = false)
    {
        var raw = GetString(key);
        if (raw is null)
        {
            return fallback;
        }

        return raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || raw == "1";
    }

    public int? GetInt(string key)
    {
        var raw = GetString(key);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
