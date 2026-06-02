using System.Globalization;

namespace Honua.Collect.Core.Enterprise;

/// <summary>White-label branding delivered by MDM (BACKLOG E4).</summary>
/// <param name="AppName">Overridden app display name.</param>
/// <param name="PrimaryColorHex">Brand primary color, e.g. <c>#0A7E3C</c>.</param>
/// <param name="LogoPath">Path/URI to a branding logo asset.</param>
public sealed record BrandingConfig(string? AppName, string? PrimaryColorHex, string? LogoPath);

/// <summary>Device policy delivered by MDM (BACKLOG E4).</summary>
/// <param name="RequireDeviceLock">Require a device passcode/biometric.</param>
/// <param name="DisableExport">Block data export off the device.</param>
/// <param name="DisableScreenshots">Block screenshots of captured data.</param>
/// <param name="MaxDraftAgeDays">Auto-purge drafts older than this many days, when set.</param>
public sealed record DevicePolicy(bool RequireDeviceLock, bool DisableExport, bool DisableScreenshots, int? MaxDraftAgeDays);

/// <summary>
/// Managed application configuration pushed by an MDM provider (AppConfig /
/// Managed App Configuration — BACKLOG E4). The host reads the platform's
/// managed-config dictionary; this gives typed, defaulted access to the server
/// endpoint, white-label branding, and device policy the app enforces.
/// </summary>
public sealed class ManagedAppConfig
{
    /// <summary>Managed-config key for the server URL.</summary>
    public const string ServerUrlKey = "server.url";

    private readonly IReadOnlyDictionary<string, string> _values;

    /// <summary>Creates a managed config from the raw key/value pairs.</summary>
    /// <param name="values">Managed-config values from the MDM provider.</param>
    public ManagedAppConfig(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An empty (unmanaged) configuration.</summary>
    public static ManagedAppConfig Empty { get; } = new(new Dictionary<string, string>());

    /// <summary>Whether any managed values are present.</summary>
    public bool IsManaged => _values.Count > 0;

    /// <summary>The configured server URL, when set.</summary>
    public string? ServerUrl => GetString(ServerUrlKey);

    /// <summary>White-label branding from <c>branding.*</c> keys.</summary>
    public BrandingConfig Branding => new(
        GetString("branding.appName"),
        GetString("branding.primaryColor"),
        GetString("branding.logo"));

    /// <summary>Device policy from <c>policy.*</c> keys.</summary>
    public DevicePolicy Policy => new(
        GetBool("policy.requireDeviceLock"),
        GetBool("policy.disableExport"),
        GetBool("policy.disableScreenshots"),
        GetInt("policy.maxDraftAgeDays"));

    /// <summary>Reads a string value.</summary>
    /// <param name="key">Config key.</param>
    /// <param name="fallback">Value when the key is absent.</param>
    /// <returns>The value or fallback.</returns>
    public string? GetString(string key, string? fallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _values.TryGetValue(key, out var value) ? value : fallback;
    }

    /// <summary>Reads a boolean value (<c>true</c>/<c>yes</c>/<c>1</c>).</summary>
    /// <param name="key">Config key.</param>
    /// <param name="fallback">Value when the key is absent.</param>
    /// <returns>The value or fallback.</returns>
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

    /// <summary>Reads an integer value, or <see langword="null"/> when absent/invalid.</summary>
    /// <param name="key">Config key.</param>
    /// <returns>The parsed integer or null.</returns>
    public int? GetInt(string key)
    {
        var raw = GetString(key);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
