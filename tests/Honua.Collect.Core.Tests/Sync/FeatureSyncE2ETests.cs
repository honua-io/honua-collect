using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Honua.Collect.Core.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

/// <summary>
/// End-to-end test against a real running Honua server. Opt-in: skipped unless
/// HONUA_E2E_SERVER is set (e.g. http://localhost:18080). Submits a captured
/// record via the same <see cref="GeoServicesFeatureSync"/> the app uses, then
/// reads it back from the Feature Server — proving capture → HTTP → server → DB.
/// </summary>
public class FeatureSyncE2ETests
{
    private static readonly string? ServerUrl = Environment.GetEnvironmentVariable("HONUA_E2E_SERVER");
    private static readonly string? ApiKey = Environment.GetEnvironmentVariable("HONUA_E2E_APIKEY");
    private const string ServiceId = "mobile_offline_demo";
    private const int LayerId = 68910;

    [Fact]
    public async Task Submitted_record_round_trips_through_server_to_database()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            return; // opt-in; no server configured
        }

        using var http = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            http.DefaultRequestHeaders.Add("X-API-Key", ApiKey);
        }

        // A unique marker so we can find exactly this submission server-side.
        var marker = "e2e-" + Guid.NewGuid().ToString("n")[..12];
        var record = new FieldRecord { RecordId = marker, FormId = "asset-inspection", Location = new FieldGeoPoint(21.31, -157.81) };
        record.Values["site_name"] = marker;
        record.Values["status"] = "new";
        record.Values["priority"] = "high";
        record.Values["assigned_to"] = "integration-test";
        record.Values["notes"] = "submitted by FeatureSyncE2ETests";

        // 1) Submit via the app's transport.
        var sync = new GeoServicesFeatureSync(http);
        var result = await sync.SubmitAsync(record, new GeoServicesTarget(ServerUrl, ServiceId, LayerId));

        Assert.True(result.Success, $"submit failed: {result.Error}");
        Assert.NotNull(result.ObjectId);

        // 2) Read it back from the Feature Server by our unique marker.
        var queryUrl = $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/query" +
            $"?where=site_name%3D%27{marker}%27&outFields=*&f=json";
        var queryJson = await http.GetStringAsync(queryUrl);

        using var doc = JsonDocument.Parse(queryJson);
        var features = doc.RootElement.GetProperty("features");
        Assert.True(features.GetArrayLength() >= 1, $"feature not found server-side: {queryJson}");

        var attrs = features[0].GetProperty("attributes");
        Assert.Equal(marker, attrs.GetProperty("site_name").GetString());
        Assert.Equal(result.ObjectId, attrs.GetProperty("objectid").GetInt64());
        Assert.Equal("high", attrs.GetProperty("priority").GetString());
    }
}
