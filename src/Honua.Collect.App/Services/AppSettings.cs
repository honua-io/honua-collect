using System.Reflection;
using System.Text.Json;
using Honua.Collect.Core.Sync;

namespace Honua.Collect.App.Services;

/// <summary>
/// Strongly-typed app configuration loaded from the bundled <c>appsettings.json</c>
/// asset — server endpoint and the (development-only) fallback credential. Keeps
/// connection details and keys out of compiled code so they live in configuration
/// and can be overridden per environment.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Base URL of the Honua server.</summary>
    public required string ServerBaseUrl { get; init; }

    /// <summary>Feature service id of the editable layer.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Layer id within the feature service.</summary>
    public required int LayerId { get; init; }

    /// <summary>Development fallback credential used until the user signs in; null in production.</summary>
    public string? DemoApiKey { get; init; }

    /// <summary>The server base address.</summary>
    public Uri BaseUri => new(ServerBaseUrl);

    /// <summary>The GeoServices feature-layer target derived from settings.</summary>
    public GeoServicesTarget Target => new(ServerBaseUrl, ServiceId, LayerId);

    /// <summary>
    /// Loads settings from the embedded <c>appsettings.json</c> resource. Fully
    /// synchronous so it is safe to call during MAUI startup without blocking on an
    /// async file API (which deadlocks the UI thread).
    /// </summary>
    /// <returns>The parsed settings.</returns>
    public static AppSettings Load()
    {
        var assembly = typeof(AppSettings).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("appsettings.json", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var server = root.GetProperty("server");

        var settings = new AppSettings
        {
            ServerBaseUrl = server.GetProperty("baseUrl").GetString()!,
            ServiceId = server.GetProperty("serviceId").GetString()!,
            LayerId = server.GetProperty("layerId").GetInt32(),
            DemoApiKey = ReadApiKey(root), // null in source — no credential ships in the binary
        };

        return ApplyLocalOverride(settings);
    }

    private static string? ReadApiKey(JsonElement root)
        => root.TryGetProperty("demo", out var demo)
            && demo.TryGetProperty("apiKey", out var key)
            && key.ValueKind == JsonValueKind.String
            ? key.GetString()
            : null;

    /// <summary>
    /// Overlays an optional, gitignored <c>appsettings.local.json</c> from the app
    /// data directory (a local-dev credential convenience). Read with synchronous
    /// file IO so startup never blocks on an async API; absent in normal/CI builds.
    /// </summary>
    private static AppSettings ApplyLocalOverride(AppSettings settings)
    {
        try
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "appsettings.local.json");
            if (!File.Exists(path))
            {
                return settings;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return settings with { DemoApiKey = ReadApiKey(document.RootElement) ?? settings.DemoApiKey };
        }
        catch (Exception)
        {
            return settings; // a malformed local override must not break startup
        }
    }
}
