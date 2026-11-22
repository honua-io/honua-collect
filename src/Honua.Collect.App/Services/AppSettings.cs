// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using Honua.Collect.Core.Sync;

namespace Honua.Collect.App.Services;

public sealed record AppSettings
{
    public required string ServerBaseUrl { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public string? DemoApiKey { get; init; }

    public IReadOnlyList<string> PinnedCertificateSpki { get; init; } = Array.Empty<string>();

    public Uri BaseUri => new(ServerBaseUrl);

    public GeoServicesTarget Target => new(ServerBaseUrl, ServiceId, LayerId);

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
            PinnedCertificateSpki = ReadPins(server),
        };

        // Fail fast on a cleartext endpoint to a non-loopback host (no credential leak).
        EndpointSecurity.EnsureSecureTransport(settings.BaseUri);
        return ApplyLocalOverride(settings);
    }

    private static IReadOnlyList<string> ReadPins(JsonElement server)
    {
        if (!server.TryGetProperty("pinnedCertificates", out var pins) || pins.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return pins.EnumerateArray()
            .Where(p => p.ValueKind == JsonValueKind.String)
            .Select(p => p.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
    }

    private static string? ReadApiKey(JsonElement root)
        => root.TryGetProperty("demo", out var demo)
            && demo.TryGetProperty("apiKey", out var key)
            && key.ValueKind == JsonValueKind.String
            ? key.GetString()
            : null;

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
