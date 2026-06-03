using System.Globalization;
using System.Text.Json;
using Honua.Collect.Core.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

/// <summary>
/// Comprehensive end-to-end tests against a real running Honua server. Opt-in:
/// every test no-ops unless <c>HONUA_E2E_SERVER</c> is set (e.g.
/// <c>http://localhost:18080</c>); <c>HONUA_E2E_APIKEY</c> supplies the write
/// credential. Exercises the full capture → applyEdits → server → database path
/// the app uses, including update/delete, error cases, and write collisions.
/// Bring a server up with <c>scripts/e2e/up.sh</c>.
/// </summary>
[Collection("e2e")]
public class FeatureSyncE2ETests
{
    private static readonly string? ServerUrl = Environment.GetEnvironmentVariable("HONUA_E2E_SERVER");
    private static readonly string? ApiKey = Environment.GetEnvironmentVariable("HONUA_E2E_APIKEY");
    private const string ServiceId = "mobile_offline_demo";
    private const int LayerId = 68910;

    private static bool Enabled => !string.IsNullOrWhiteSpace(ServerUrl);
    private static GeoServicesTarget Target => new(ServerUrl!, ServiceId, LayerId);

    private static HttpClient Authed()
    {
        var http = new HttpClient { BaseAddress = new Uri(ServerUrl!) };
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            http.DefaultRequestHeaders.Add("X-API-Key", ApiKey);
        }

        return http;
    }

    private static FieldRecord Site(string marker, Action<FieldRecord>? customize = null)
    {
        var r = new FieldRecord { RecordId = marker, FormId = "field-site", Location = new FieldGeoPoint(21.31, -157.81) };
        r.Values["site_name"] = marker;
        r.Values["status"] = "new";
        customize?.Invoke(r);
        return r;
    }

    private static string Marker() => "e2e-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>Returns the features (cloned) matching a site_name.</summary>
    private static async Task<JsonElement> QueryByNameAsync(HttpClient http, string name)
    {
        var url = $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/query?where=site_name%3D%27{name}%27&outFields=*&f=json";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
        return doc.RootElement.GetProperty("features").Clone();
    }

    private static async Task<int> CountByNameAsync(HttpClient http, string name)
        => (await QueryByNameAsync(http, name)).GetArrayLength();

    /// <summary>Reads an attribute as a long whether returned as a JSON number or numeric string.</summary>
    private static long ReadLong(JsonElement attrs, string name)
    {
        var v = attrs.GetProperty(name);
        return v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.Parse(v.GetString()!, CultureInfo.InvariantCulture);
    }

    // ---- Add / update / delete happy paths -----------------------------------

    [SkippableFact]
    public async Task Add_persists_and_round_trips_geometry_and_typed_attributes()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var http = Authed();
        var marker = Marker();
        var record = Site(marker, r => { r.Values["priority"] = "high"; r.Values["sync_version"] = 7; });

        var result = await new GeoServicesFeatureSync(http).SubmitAsync(record, Target);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.ObjectId);

        var features = await QueryByNameAsync(http, marker);
        Assert.Equal(1, features.GetArrayLength());
        var f = features[0];
        var attrs = f.GetProperty("attributes");
        Assert.Equal(result.ObjectId, attrs.GetProperty("objectid").GetInt64()); // PK column stays numeric
        Assert.Equal("high", attrs.GetProperty("priority").GetString());
        // The shared 'features' table stores user attributes as JSONB and projects
        // them back as strings, so an integer field round-trips as "7" (read
        // tolerantly rather than assuming the JSON token kind).
        Assert.Equal(7, ReadLong(attrs, "sync_version"));
        Assert.Equal(-157.81, f.GetProperty("geometry").GetProperty("x").GetDouble(), 5);
        Assert.Equal(21.31, f.GetProperty("geometry").GetProperty("y").GetDouble(), 5);
    }

    [SkippableFact]
    public async Task Update_changes_attributes_server_side()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var http = Authed();
        var sync = new GeoServicesFeatureSync(http);
        var marker = Marker();

        var added = await sync.SubmitAsync(Site(marker), Target);
        Assert.True(added.Success, added.Error);

        var update = Site(marker, r => { r.Values["status"] = "done"; r.Values["notes"] = "updated"; });
        var updated = await sync.UpdateAsync(added.ObjectId!.Value, update, Target);
        Assert.True(updated.Success, updated.Error);

        var attrs = (await QueryByNameAsync(http, marker))[0].GetProperty("attributes");
        Assert.Equal("done", attrs.GetProperty("status").GetString());
        Assert.Equal("updated", attrs.GetProperty("notes").GetString());
    }

    [SkippableFact]
    public async Task Delete_removes_the_feature()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var http = Authed();
        var sync = new GeoServicesFeatureSync(http);
        var marker = Marker();
        var added = await sync.SubmitAsync(Site(marker), Target);
        Assert.Equal(1, await CountByNameAsync(http, marker));

        var deleted = await sync.DeleteAsync(added.ObjectId!.Value, Target);

        Assert.True(deleted.Success, deleted.Error);
        Assert.Equal(0, await CountByNameAsync(http, marker));
    }

    // ---- Error / rejection cases ---------------------------------------------

    [SkippableFact]
    public async Task Update_of_nonexistent_feature_is_rejected_not_crashing()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var http = Authed();
        var result = await new GeoServicesFeatureSync(http).UpdateAsync(999_000_111, Site(Marker()), Target);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Delete_of_nonexistent_feature_is_rejected_not_crashing()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var http = Authed();
        var result = await new GeoServicesFeatureSync(http).DeleteAsync(999_000_222, Target);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Submit_without_credentials_fails_gracefully()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var anon = new HttpClient { BaseAddress = new Uri(ServerUrl!) }; // no X-API-Key
        var marker = Marker();
        var result = await new GeoServicesFeatureSync(anon).SubmitAsync(Site(marker), Target);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);

        // Nothing should have been written.
        using var authed = Authed();
        Assert.Equal(0, await CountByNameAsync(authed, marker));
    }

    // ---- Collisions / concurrency --------------------------------------------

    [SkippableFact]
    public async Task Concurrent_adds_all_persist_with_distinct_object_ids()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        const int n = 12;
        using var http = Authed();
        var sync = new GeoServicesFeatureSync(http);
        var markers = Enumerable.Range(0, n).Select(_ => Marker()).ToArray();

        // Fire all submissions concurrently — stresses server-side id assignment.
        var results = await Task.WhenAll(markers.Select(m => sync.SubmitAsync(Site(m), Target)));

        Assert.All(results, r => Assert.True(r.Success, r.Error));
        var ids = results.Select(r => r.ObjectId).ToHashSet();
        Assert.Equal(n, ids.Count); // no object-id collision / lost write

        // Every one is independently retrievable server-side.
        foreach (var marker in markers)
        {
            Assert.Equal(1, await CountByNameAsync(http, marker));
        }
    }

    [SkippableFact]
    public async Task Concurrent_updates_to_same_feature_all_succeed_via_retry()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        const int n = 10;
        using var http = Authed();
        var sync = new GeoServicesFeatureSync(http); // default retry policy
        var marker = Marker();

        var added = await sync.SubmitAsync(Site(marker), Target);
        var oid = added.ObjectId!.Value;

        // N concurrent writers each set notes to a distinct value — a write
        // collision on a single row. Transient contention is retried, so every
        // submission succeeds rather than surfacing a spurious failure.
        var values = Enumerable.Range(0, n).Select(i => $"writer-{i}").ToArray();
        var results = await Task.WhenAll(values.Select(v =>
            sync.UpdateAsync(oid, Site(marker, r => r.Values["notes"] = v), Target)));

        Assert.All(results, r => Assert.True(r.Success, r.Error)); // retry resolves the contention

        // Consistency: exactly one row remains (no duplication) and its value
        // comes from a writer that actually submitted it (last-write-wins, intact).
        var features = await QueryByNameAsync(http, marker);
        Assert.Equal(1, features.GetArrayLength());
        var finalNotes = features[0].GetProperty("attributes").GetProperty("notes").GetString();
        Assert.Contains(finalNotes, values);
    }

    [SkippableFact]
    public async Task Duplicate_submissions_create_distinct_rows_server_side()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");


        using var http = Authed();
        var sync = new GeoServicesFeatureSync(http);
        var marker = Marker();

        var r1 = await sync.SubmitAsync(Site(marker), Target);
        var r2 = await sync.SubmitAsync(Site(marker), Target); // identical capture submitted twice

        Assert.True(r1.Success && r2.Success);
        Assert.NotEqual(r1.ObjectId, r2.ObjectId);
        // The Feature Server is not a dedup authority — both adds persist as
        // distinct rows; duplicate suppression is a client-side concern
        // (SDK DuplicateDetector), not the transport's.
        Assert.Equal(2, await CountByNameAsync(http, marker));
    }
}

/// <summary>Serializes the e2e tests so concurrent classes don't fight over the server.</summary>
[CollectionDefinition("e2e", DisableParallelization = true)]
public sealed class E2ECollection;
