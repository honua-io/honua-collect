using System.Net;
using System.Text.Json;
using Honua.Collect.Core.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

/// <summary>
/// Client-side at-most-once guarantees for the upload path (#102 / AUD-215). Each
/// <c>add</c> carries a stable, client-generated GlobalID derived from the record's
/// <see cref="FieldRecord.RecordId"/> and is sent with <c>useGlobalIds=true</c>, so
/// a server that dedupes on GlobalID collapses a re-sent add (after a lost
/// response, an app restart, or a re-queue) into the original feature instead of
/// inserting a duplicate. The fake server here models that server-side dedupe — the
/// matching honua-server work is the out-of-scope follow-up.
/// </summary>
public class GeoServicesFeatureSyncIdempotencyTests
{
    private static readonly GeoServicesTarget Target = new("https://example.test", "svc", 0);

    private static FieldRecord Site(string recordId)
    {
        var r = new FieldRecord { RecordId = recordId, FormId = "f", Location = new FieldGeoPoint(21.30, -157.82) };
        r.Values["site_name"] = "Idempotency Site";
        return r;
    }

    /// <summary>
    /// A feature server that keys features on the client-supplied GlobalID. A re-add
    /// of a GlobalID it already holds is a no-op that echoes the original object id
    /// rather than creating a second feature. Optionally drops the response to the
    /// first add <em>after</em> committing it, simulating a lost-response window.
    /// </summary>
    private sealed class DedupingFeatureServer(bool dropFirstResponse = false) : HttpMessageHandler
    {
        private readonly Dictionary<string, long> _features = new(StringComparer.OrdinalIgnoreCase);
        private long _nextObjectId = 1000;
        private bool _dropNext = dropFirstResponse;

        /// <summary>Number of distinct features the server is holding.</summary>
        public int FeatureCount => _features.Count;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var rawBody = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var form = ParseForm(rawBody);

            Assert.Equal("true", form.GetValueOrDefault("useGlobalIds"));
            Assert.True(form.ContainsKey("adds"), "add request must carry an adds array");

            var globalId = ReadFirstGlobalId(form["adds"]);
            Assert.False(string.IsNullOrWhiteSpace(globalId), "every add must carry a client GlobalID");

            // Commit (idempotently): a GlobalID we already hold keeps its object id.
            if (!_features.TryGetValue(globalId, out var objectId))
            {
                objectId = _nextObjectId++;
                _features[globalId] = objectId;
            }

            if (_dropNext)
            {
                // Server committed, but the client never sees the response: the exact
                // window that makes a naive add retry duplicate.
                _dropNext = false;
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
            }

            var json = $$"""{"addResults":[{"objectId":{{objectId}},"success":true}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }

        private static Dictionary<string, string> ParseForm(string body)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                var key = WebUtility.UrlDecode(pair[..eq]);
                var value = WebUtility.UrlDecode(pair[(eq + 1)..]);
                result[key] = value;
            }

            return result;
        }

        private static string? ReadFirstGlobalId(string addsJson)
        {
            using var doc = JsonDocument.Parse(addsJson);
            var attributes = doc.RootElement[0].GetProperty("attributes");
            return attributes.TryGetProperty("globalid", out var g) ? g.GetString() : null;
        }
    }

    [Fact]
    public void GlobalIdFor_is_deterministic_in_the_record_id()
    {
        // Same record id -> same key (survives restart / entry rehydration);
        // different record id -> different key.
        Assert.Equal(GeoServicesFeatureSync.GlobalIdFor("rec-1"), GeoServicesFeatureSync.GlobalIdFor(Site("rec-1")));
        Assert.NotEqual(GeoServicesFeatureSync.GlobalIdFor("rec-1"), GeoServicesFeatureSync.GlobalIdFor("rec-2"));

        // Esri registry format: braces + 36-char canonical GUID inside.
        var key = GeoServicesFeatureSync.GlobalIdFor("rec-1");
        Assert.StartsWith("{", key);
        Assert.EndsWith("}", key);
        Assert.True(Guid.TryParse(key, out _));
    }

    [Fact]
    public async Task Add_sends_a_stable_global_id_under_use_global_ids()
    {
        using var server = new DedupingFeatureServer();
        using var http = new HttpClient(server);
        var record = Site("rec-stable");

        var result = await new GeoServicesFeatureSync(http).SubmitAsync(record, Target);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, server.FeatureCount);
    }

    [Fact]
    public async Task Lost_response_add_then_retry_creates_exactly_one_server_feature()
    {
        // The canonical AUD-215 scenario: the first add commits server-side but the
        // response is lost, so the client sees a failure and re-queues; the retry
        // re-sends the SAME GlobalID and the server dedupes it.
        using var server = new DedupingFeatureServer(dropFirstResponse: true);
        using var http = new HttpClient(server);
        var sync = new GeoServicesFeatureSync(http, FeatureSyncRetryPolicy.None);
        var record = Site("rec-lost");

        var first = await sync.SubmitAsync(record, Target);
        Assert.False(first.Success); // lost response surfaced as a failure, not auto-retried

        // Caller re-queues the same record (next sync pass / restart).
        var retry = await sync.SubmitAsync(record, Target);

        Assert.True(retry.Success, retry.Error);
        Assert.Equal(1, server.FeatureCount); // exactly ONE server feature
    }

    [Fact]
    public async Task Repeated_add_of_the_same_record_does_not_duplicate()
    {
        // Even without a lost response, two adds of the same record (e.g. a
        // double-tap or an over-eager re-sync) dedupe to one feature.
        using var server = new DedupingFeatureServer();
        using var http = new HttpClient(server);
        var sync = new GeoServicesFeatureSync(http);
        var record = Site("rec-twice");

        var a = await sync.SubmitAsync(record, Target);
        var b = await sync.SubmitAsync(record, Target);

        Assert.True(a.Success, a.Error);
        Assert.True(b.Success, b.Error);
        Assert.Equal(a.ObjectId, b.ObjectId);
        Assert.Equal(1, server.FeatureCount);
    }

    private sealed class CannedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    [Fact]
    public async Task Delete_of_an_already_deleted_feature_is_a_no_op_success()
    {
        // delete -> delete must be idempotent: a feature that is already gone is the
        // desired end state, not an error or a duplicate.
        using var handler = new CannedHandler(
            HttpStatusCode.OK,
            """{"deleteResults":[{"objectId":42,"success":false,"error":{"code":1000,"description":"Feature not found."}}]}""");
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http).DeleteAsync(42, Target);

        Assert.True(result.Success);
        Assert.Equal(42, result.ObjectId);
    }

    [Fact]
    public async Task Delete_surfaces_a_genuine_failure()
    {
        using var handler = new CannedHandler(
            HttpStatusCode.OK,
            """{"deleteResults":[{"objectId":42,"success":false,"error":{"code":403,"description":"Permission denied."}}]}""");
        using var http = new HttpClient(handler);

        var result = await new GeoServicesFeatureSync(http).DeleteAsync(42, Target);

        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error);
    }
}
