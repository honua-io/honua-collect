using System.Globalization;
using System.Text.Json;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

/// <summary>Identifies the GeoServices Feature Server layer a record submits to.</summary>
/// <param name="BaseUrl">Server base URL, e.g. <c>http://10.0.2.2:18080</c>.</param>
/// <param name="ServiceId">Feature service id.</param>
/// <param name="LayerId">Layer id within the service.</param>
public sealed record GeoServicesTarget(string BaseUrl, string ServiceId, int LayerId)
{
    /// <summary>The full applyEdits endpoint URL for this target.</summary>
    public string ApplyEditsUrl =>
        $"{BaseUrl.TrimEnd('/')}/rest/services/{ServiceId}/FeatureServer/{LayerId}/applyEdits";
}

/// <summary>Result of submitting a record to the server.</summary>
/// <param name="Success">Whether the edit was applied.</param>
/// <param name="ObjectId">Server-assigned object id, when successful.</param>
/// <param name="Error">Error message, when not successful.</param>
public sealed record FeatureSyncResult(bool Success, long? ObjectId, string? Error);

/// <summary>
/// Submits a captured <see cref="FieldRecord"/> to a Honua/ArcGIS GeoServices
/// Feature Server via <c>applyEdits</c> — the same wire protocol Survey123 and
/// Fulcrum use to push submissions to a feature service. Record values become
/// feature attributes and <see cref="FieldRecord.Location"/> becomes the point
/// geometry. The <see cref="HttpClient"/> is injected (the host configures the
/// base address, the <c>X-API-Key</c>/token auth header, and the platform
/// handler), so this is portable and unit-testable.
/// </summary>
public sealed class GeoServicesFeatureSync
{
    private readonly HttpClient _http;

    /// <summary>Creates the sync client over a configured HTTP client.</summary>
    /// <param name="http">HTTP client (base address/auth headers set by the host).</param>
    public GeoServicesFeatureSync(HttpClient http)
        => _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Submits a record as an <c>add</c> edit to the target layer.</summary>
    /// <param name="record">The record to submit.</param>
    /// <param name="target">Target service/layer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sync result with the server-assigned object id on success.</returns>
    public async Task<FeatureSyncResult> SubmitAsync(
        FieldRecord record,
        GeoServicesTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(target);

        var adds = BuildAddsJson(record);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["adds"] = adds,
            ["rollbackOnFailure"] = "true",
        });

        using var response = await _http.PostAsync(target.ApplyEditsUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new FeatureSyncResult(false, null, $"HTTP {(int)response.StatusCode}: {body}");
        }

        return ParseResult(body);
    }

    /// <summary>Serializes a record to the GeoServices <c>adds</c> array JSON.</summary>
    /// <param name="record">Record to serialize.</param>
    /// <returns>A one-element JSON array string with geometry + attributes.</returns>
    public static string BuildAddsJson(FieldRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();

            if (record.Location is { } location)
            {
                writer.WriteStartObject("geometry");
                writer.WriteNumber("x", location.Longitude);
                writer.WriteNumber("y", location.Latitude);
                writer.WriteStartObject("spatialReference");
                writer.WriteNumber("wkid", 4326);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteStartObject("attributes");
            foreach (var (key, value) in record.Values)
            {
                if (value is null)
                {
                    continue;
                }

                WriteAttribute(writer, key, value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteAttribute(Utf8JsonWriter writer, string key, object value)
    {
        switch (value)
        {
            case string s:
                writer.WriteString(key, s);
                break;
            case bool b:
                writer.WriteBoolean(key, b);
                break;
            case sbyte or byte or short or ushort or int or uint or long:
                writer.WriteNumber(key, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case float or double or decimal:
                writer.WriteNumber(key, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            default:
                writer.WriteString(key, value.ToString() ?? string.Empty);
                break;
        }
    }

    private static FeatureSyncResult ParseResult(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "Server error";
                return new FeatureSyncResult(false, null, msg);
            }

            if (root.TryGetProperty("addResults", out var addResults) && addResults.GetArrayLength() > 0)
            {
                var first = addResults[0];
                var success = first.TryGetProperty("success", out var s) && s.GetBoolean();
                long? oid = first.TryGetProperty("objectId", out var o) && o.TryGetInt64(out var v) ? v : null;
                return success
                    ? new FeatureSyncResult(true, oid, null)
                    : new FeatureSyncResult(false, oid, "Edit was not applied.");
            }

            return new FeatureSyncResult(false, null, "Unexpected response: no addResults.");
        }
        catch (JsonException ex)
        {
            return new FeatureSyncResult(false, null, $"Invalid response: {ex.Message}");
        }
    }
}
