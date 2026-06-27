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

    /// <summary>The full query endpoint URL for this target (used to pull features).</summary>
    public string QueryUrl =>
        $"{BaseUrl.TrimEnd('/')}/rest/services/{ServiceId}/FeatureServer/{LayerId}/query";

    /// <summary>The full addAttachment endpoint URL for a feature on this target.</summary>
    /// <param name="objectId">Object id of the feature the attachment belongs to.</param>
    public string AttachmentUrl(long objectId) =>
        $"{BaseUrl.TrimEnd('/')}/rest/services/{ServiceId}/FeatureServer/{LayerId}/{objectId.ToString(CultureInfo.InvariantCulture)}/addAttachment";
}

/// <summary>Result of submitting a record to the server.</summary>
/// <param name="Success">Whether the edit was applied.</param>
/// <param name="ObjectId">Server-assigned object id, when successful.</param>
/// <param name="Error">Error message, when not successful.</param>
/// <param name="ErrorCode">Server error code, when not successful.</param>
/// <param name="Attempts">Number of attempts made (including retries).</param>
public sealed record FeatureSyncResult(bool Success, long? ObjectId, string? Error, int? ErrorCode = null, int Attempts = 1)
{
    /// <summary>Builds a successful result for the given server object id.</summary>
    /// <param name="objectId">The server-assigned object id, when known.</param>
    public static FeatureSyncResult Ok(long? objectId) => new(true, objectId, null);

    /// <summary>Builds a failed result carrying the error message and optional code.</summary>
    /// <param name="error">The failure message.</param>
    /// <param name="code">The server/HTTP error code, when known.</param>
    public static FeatureSyncResult Fail(string error, int? code = null) => new(false, null, error, code);

    /// <summary>
    /// A single one-line description of this result's failure (message plus code when
    /// present), suitable for surfacing in the sync center; empty when successful.
    /// </summary>
    public string FailureDescription => Success
        ? string.Empty
        : ErrorCode is { } code
            ? $"{Error ?? "Upload was rejected."} (code {code.ToString(CultureInfo.InvariantCulture)})"
            : Error ?? "Upload was rejected.";
}

/// <summary>
/// A single feature pulled back from the server, decoded into a portable
/// <see cref="FieldRecord"/> plus the layer object id that identifies it remotely.
/// This inverts the submit encoding (Esri attributes -> Values, point geometry ->
/// Location) so a pulled feature can flow into the same conflict/merge model as a
/// locally captured record.
/// </summary>
/// <param name="ObjectId">The layer object id of the feature on the server.</param>
/// <param name="Record">The decoded record (attributes as Values, geometry as Location).</param>
public sealed record PulledRecord(long ObjectId, FieldRecord Record);

/// <summary>
/// Result of a <see cref="GeoServicesFeatureSync.QueryAsync"/> pull. On success
/// <see cref="Records"/> carries the decoded features (paging already followed);
/// on failure <see cref="Error"/> describes the server/transport problem and
/// <see cref="Records"/> is empty. A server <c>{"error":...}</c> response surfaces
/// here as a failure rather than an exception.
/// </summary>
/// <param name="Success">Whether the query completed.</param>
/// <param name="Records">The decoded features, when successful.</param>
/// <param name="Error">Error message, when not successful.</param>
/// <param name="ErrorCode">Server/HTTP error code, when not successful.</param>
public sealed record FeatureQueryResult(bool Success, IReadOnlyList<PulledRecord> Records, string? Error, int? ErrorCode = null)
{
    /// <summary>An empty successful result.</summary>
    public static FeatureQueryResult Empty { get; } = new(true, [], null);

    /// <summary>Builds a failed result with no records.</summary>
    public static FeatureQueryResult Fail(string error, int? code = null) => new(false, [], error, code);
}

/// <summary>
/// Controls automatic retry of transient submission failures (server-side write
/// contention on a feature, HTTP 5xx/429/408, transient network errors). Field
/// submissions are concurrent and contend on shared rows, so a single attempt
/// can fail transiently; retrying with backoff lets the submission succeed
/// instead of surfacing a spurious error to the user. Permanent failures
/// (feature-not-found, auth, validation) are never retried.
/// </summary>
/// <param name="MaxAttempts">Total attempts, including the first. Defaults to 4.</param>
/// <param name="BaseDelay">Base backoff delay; grows exponentially with jitter.</param>
public sealed record FeatureSyncRetryPolicy(int MaxAttempts = 4, TimeSpan BaseDelay = default)
{
    /// <summary>The effective base delay (defaults to 150ms when unset).</summary>
    public TimeSpan EffectiveBaseDelay => BaseDelay == default ? TimeSpan.FromMilliseconds(150) : BaseDelay;

    /// <summary>The default policy (4 attempts, 150ms base backoff).</summary>
    public static FeatureSyncRetryPolicy Default { get; } = new();

    /// <summary>A policy that never retries (single attempt).</summary>
    public static FeatureSyncRetryPolicy None { get; } = new(MaxAttempts: 1);

    /// <summary>
    /// Substrings (case-insensitive) in a server error message that mark a
    /// failure as permanent. The GeoServices error <em>code</em> is unreliable —
    /// the server reuses code 1000 for both "feature not found" (permanent) and
    /// "update failed" (transient write contention) — so retryability is decided
    /// from the message instead.
    /// </summary>
    internal static readonly string[] PermanentMessagePatterns =
        ["not found", "does not exist", "invalid", "unauthorized", "forbidden", "permission", "not allowed", "not supported"];
}

/// <summary>
/// Submits a captured <see cref="FieldRecord"/> to a Honua/ArcGIS GeoServices
/// Feature Server via <c>applyEdits</c> — the same wire protocol Survey123 and
/// Fulcrum use to push submissions to a feature service. Record values become
/// feature attributes and <see cref="FieldRecord.Location"/> becomes the point
/// geometry. The <see cref="HttpClient"/> is injected (the host configures the
/// base address, the <c>X-API-Key</c>/token auth header, and the platform
/// handler), so this is portable and unit-testable. Transient failures are
/// retried per the <see cref="FeatureSyncRetryPolicy"/>.
/// </summary>
public sealed class GeoServicesFeatureSync
{
    /// <summary>
    /// This product's default object-id field name. Esri field references are
    /// case-insensitive, so this is stable against <c>OBJECTID</c> too.
    /// </summary>
    private const string DefaultObjectIdField = "objectid";

    private readonly HttpClient _http;
    private readonly FeatureSyncRetryPolicy _retry;

    /// <summary>Creates the sync client over a configured HTTP client.</summary>
    /// <param name="http">HTTP client (base address/auth headers set by the host).</param>
    /// <param name="retryPolicy">Retry policy; defaults to <see cref="FeatureSyncRetryPolicy.Default"/>.</param>
    public GeoServicesFeatureSync(HttpClient http, FeatureSyncRetryPolicy? retryPolicy = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _retry = retryPolicy ?? FeatureSyncRetryPolicy.Default;
    }

    /// <summary>Submits a record as an <c>add</c> edit to the target layer.</summary>
    /// <param name="record">The record to submit.</param>
    /// <param name="target">Target service/layer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sync result with the server-assigned object id on success.</returns>
    /// <remarks>
    /// An <c>add</c> is <em>not idempotent</em>: the Feature Server is not a dedup
    /// authority, so a retried add creates a second feature. When a request fails
    /// without a server response (transport reset, request timeout, or a 5xx that
    /// may have committed before the connection dropped) it is impossible to tell
    /// whether the add already committed, so this method does <strong>not</strong>
    /// auto-retry an add in that ambiguous window — it surfaces the failure for the
    /// caller to reconcile (re-queue once) instead of silently duplicating. Adds are
    /// still retried on a server-acknowledged transient per-edit failure
    /// (HTTP 200 with <c>success:false</c> under <c>rollbackOnFailure</c>), which is
    /// provably non-committed. Add submission therefore carries at-least-once
    /// semantics; full at-most-once requires server-side dedupe on a client key.
    /// </remarks>
    public Task<FeatureSyncResult> SubmitAsync(
        FieldRecord record,
        GeoServicesTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(target);
        return PostEditsAsync(target, "adds", BuildFeaturesJson(record, null), "addResults", idempotent: false, cancellationToken);
    }

    /// <summary>
    /// Submits many records as a single <c>applyEdits</c> with a multi-feature
    /// <c>adds</c> array — one HTTP round-trip for the whole batch instead of one per
    /// record. This is the bulk field-submission path: pushing hundreds of queued
    /// captures one-at-a-time is a throughput and battery sink, whereas a single
    /// batched call lets the server apply them together.
    /// </summary>
    /// <param name="records">The records to add, in order.</param>
    /// <param name="target">Target service/layer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One result per input record, aligned by index (the server returns
    /// <c>addResults</c> in submitted order). A whole-request failure yields a failed
    /// result for every record.
    /// </returns>
    /// <remarks>
    /// Like <see cref="SubmitAsync"/>, a batch of <c>adds</c> is <em>not idempotent</em>;
    /// a transport failure with no server response is ambiguous, so the batch is not
    /// auto-retried here — the failed records stay queued for the caller to re-submit.
    /// <c>rollbackOnFailure</c> is intentionally <em>not</em> set for the batch so one
    /// bad record does not reject the whole batch; each record's outcome is reported
    /// independently in its result.
    /// </remarks>
    public async Task<IReadOnlyList<FeatureSyncResult>> SubmitBatchAsync(
        IReadOnlyList<FieldRecord> records,
        GeoServicesTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(target);
        if (records.Count == 0)
        {
            return [];
        }

        var addsJson = BuildFeaturesArrayJson(records);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["adds"] = addsJson,
            // No rollbackOnFailure: report each record's outcome independently rather
            // than failing the whole batch on one bad record.
            ["rollbackOnFailure"] = "false",
        });

        string body;
        try
        {
            using var response = await _http.PostAsync(target.ApplyEditsUrl, content, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return FailEach(records.Count, $"HTTP {(int)response.StatusCode}: {body}", (int)response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            return FailEach(records.Count, ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return FailEach(records.Count, ex.Message); // request timeout
        }

        return ParseBatchResults(body, records.Count);
    }

    private static IReadOnlyList<FeatureSyncResult> FailEach(int count, string error, int? code = null)
    {
        var results = new FeatureSyncResult[count];
        Array.Fill(results, FeatureSyncResult.Fail(error, code));
        return results;
    }

    private static IReadOnlyList<FeatureSyncResult> ParseBatchResults(string body, int expectedCount)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                var msg = error.TryGetProperty("message", out var m) ? m.GetString() ?? "Server error" : "Server error";
                int? code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var cv) ? cv : null;
                return FailEach(expectedCount, msg, code);
            }

            var results = new FeatureSyncResult[expectedCount];
            JsonElement addResults = default;
            var haveResults = root.TryGetProperty("addResults", out addResults)
                && addResults.ValueKind == JsonValueKind.Array;

            for (var i = 0; i < expectedCount; i++)
            {
                if (!haveResults || i >= addResults.GetArrayLength())
                {
                    results[i] = FeatureSyncResult.Fail("The server returned no result for this record.");
                    continue;
                }

                var item = addResults[i];
                var success = item.TryGetProperty("success", out var s) && s.GetBoolean();
                long? oid = item.TryGetProperty("objectId", out var o) && o.TryGetInt64(out var v) ? v : null;
                if (success)
                {
                    results[i] = FeatureSyncResult.Ok(oid);
                    continue;
                }

                string detail = "Edit was not applied.";
                int? errCode = null;
                if (item.TryGetProperty("error", out var e))
                {
                    detail = (e.TryGetProperty("description", out var d) ? d.GetString() : detail) ?? detail;
                    errCode = e.TryGetProperty("code", out var ec) && ec.TryGetInt32(out var ecv) ? ecv : null;
                }

                results[i] = FeatureSyncResult.Fail(detail, errCode);
            }

            return results;
        }
        catch (JsonException ex)
        {
            return FailEach(expectedCount, $"Invalid response: {ex.Message}");
        }
    }

    /// <summary>Updates an existing feature, identified by its object id.</summary>
    /// <param name="objectId">Server object id to update.</param>
    /// <param name="record">Record carrying the new attribute/geometry values.</param>
    /// <param name="target">Target service/layer.</param>
    /// <param name="objectIdField">
    /// The layer's object-id field name to write the id under. Defaults to
    /// <c>objectid</c> (this product's layer convention); pass the layer's actual
    /// <c>objectIdFieldName</c> (e.g. <c>OBJECTID</c>, <c>FID</c>) when it differs,
    /// so the update keys the correct feature instead of silently creating an
    /// unrecognized attribute and updating nothing.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result for the update.</returns>
    public Task<FeatureSyncResult> UpdateAsync(
        long objectId,
        FieldRecord record,
        GeoServicesTarget target,
        string objectIdField = DefaultObjectIdField,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectIdField);
        return PostEditsAsync(target, "updates", BuildFeaturesJson(record, objectId, objectIdField), "updateResults", idempotent: true, cancellationToken);
    }

    /// <summary>Deletes a feature by its object id.</summary>
    /// <param name="objectId">Server object id to delete.</param>
    /// <param name="target">Target service/layer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result for the delete.</returns>
    public Task<FeatureSyncResult> DeleteAsync(
        long objectId,
        GeoServicesTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var deletes = $"[{objectId.ToString(CultureInfo.InvariantCulture)}]";
        return PostEditsAsync(target, "deletes", deletes, "deleteResults", idempotent: true, cancellationToken);
    }

    /// <summary>
    /// Uploads a media file to a feature's <c>addAttachment</c> endpoint as a
    /// <c>multipart/form-data</c> POST. Call this after <see cref="SubmitAsync"/>
    /// returns the feature's object id, once per captured media attachment.
    /// </summary>
    /// <param name="objectId">Object id of the feature to attach the file to.</param>
    /// <param name="filePath">Local file-system path of the media to upload.</param>
    /// <param name="contentType">MIME type of the file; defaults to <c>application/octet-stream</c>.</param>
    /// <param name="target">Target service/layer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result with the server-assigned attachment object id on success.</returns>
    public async Task<FeatureSyncResult> AddAttachmentAsync(
        long objectId,
        string filePath,
        string? contentType,
        GeoServicesTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var content = new MultipartFormDataContent();

        var textPart = new StringContent("json");
        content.Add(textPart, "f");

        // Stream the file straight from disk instead of buffering the whole thing
        // into a byte[] — a field photo/video can be tens to hundreds of MB, and
        // ReadAllBytes would pin all of it in managed memory (OOM/GC risk on a
        // memory-constrained device). The MultipartFormDataContent owns the
        // StreamContent and disposes the FileStream when `content` is disposed.
        var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        var filePart = new StreamContent(fileStream);
        filePart.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        content.Add(filePart, "attachment", Path.GetFileName(filePath));

        using var response = await _http.PostAsync(target.AttachmentUrl(objectId), content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new FeatureSyncResult(false, null, $"HTTP {(int)response.StatusCode}: {body}", (int)response.StatusCode);
        }

        return ParseAttachmentResult(body);
    }

    /// <summary>
    /// Pulls existing features from the target layer's <c>query</c> endpoint and
    /// decodes them into <see cref="FieldRecord"/>s. This is the read side of
    /// bidirectional sync: where <see cref="SubmitAsync"/> pushes a record up,
    /// this fetches the server's current features so they can be merged into the
    /// local store and fed into conflict review.
    /// </summary>
    /// <remarks>
    /// Issues a <c>GET</c> with <c>f=json</c>, <c>outFields=*</c>,
    /// <c>returnGeometry=true</c> and the supplied <paramref name="where"/> clause,
    /// following server-side paging via <c>resultOffset</c>/<c>resultRecordCount</c>
    /// while the server reports <c>exceededTransferLimit</c>. A server
    /// <c>{"error":...}</c> response is returned as a failed
    /// <see cref="FeatureQueryResult"/> rather than thrown.
    /// </remarks>
    /// <param name="target">Target service/layer to query.</param>
    /// <param name="where">SQL-style filter; defaults to <c>1=1</c> (all features).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decoded features, or a failure describing the problem.</returns>
    public async Task<FeatureQueryResult> QueryAsync(
        GeoServicesTarget target,
        string where = "1=1",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var effectiveWhere = string.IsNullOrWhiteSpace(where) ? "1=1" : where;

        var records = new List<PulledRecord>();
        var offset = 0;
        const int pageSize = 1000;
        const string ObjectIdOrderField = DefaultObjectIdField;

        // Hard upper bound on pages followed in a single pull. A server that always
        // reports exceededTransferLimit (a misbehaving/old server that ignores
        // resultOffset, or a pathologically large layer) would otherwise loop
        // forever and exhaust device memory accumulating records. 10k pages ×
        // 1k = 10M features is far beyond any realistic on-device pull, so hitting
        // it means the server is not paging correctly — fail with a clear error
        // rather than OOM the app.
        const int maxPages = 10_000;
        var pagesFetched = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (pagesFetched++ >= maxPages)
            {
                return FeatureQueryResult.Fail(
                    $"Server did not stop paging after {maxPages.ToString(CultureInfo.InvariantCulture)} pages " +
                    $"({records.Count.ToString(CultureInfo.InvariantCulture)} features pulled); aborting to bound memory. " +
                    "The layer's query endpoint may be ignoring resultOffset/orderByFields.");
            }

            var query = new Dictionary<string, string?>
            {
                ["f"] = "json",
                ["where"] = effectiveWhere,
                ["outFields"] = "*",
                ["returnGeometry"] = "true",
                // resultOffset paging is only well-defined under a stable sort: the
                // ArcGIS REST spec requires orderByFields, otherwise the server may
                // return rows in an arbitrary/changing order across pages and a
                // layer larger than the page size can silently skip or double-count
                // features. Order by the object-id field (this product's layers key
                // on "objectid"; Esri field references are case-insensitive in the
                // SQL order-by, so this is stable against OBJECTID too).
                ["orderByFields"] = ObjectIdOrderField,
                ["resultOffset"] = offset.ToString(CultureInfo.InvariantCulture),
                ["resultRecordCount"] = pageSize.ToString(CultureInfo.InvariantCulture),
            };

            var url = $"{target.QueryUrl}?{BuildQueryString(query)}";

            string body;
            try
            {
                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return FeatureQueryResult.Fail($"HTTP {(int)response.StatusCode}: {body}", (int)response.StatusCode);
                }
            }
            catch (HttpRequestException ex)
            {
                return FeatureQueryResult.Fail(ex.Message);
            }

            FeaturePage page;
            try
            {
                page = ParseQueryPage(body);
            }
            catch (JsonException ex)
            {
                return FeatureQueryResult.Fail($"Invalid response: {ex.Message}");
            }

            if (page.Error is { } err)
            {
                return FeatureQueryResult.Fail(err.Message, err.Code);
            }

            records.AddRange(page.Records);

            // Advance and break on the raw feature count the server returned, not
            // the decoded count: DecodeFeature drops features lacking a recognized
            // object id, so paging on Records.Count would re-fetch the dropped
            // boundary features and could truncate early when a page decodes to
            // zero rows but still reports exceededTransferLimit.
            if (!page.ExceededTransferLimit || page.RawCount == 0)
            {
                break;
            }

            offset += page.RawCount;
        }

        return new FeatureQueryResult(true, records, null);
    }

    private static string BuildQueryString(IReadOnlyDictionary<string, string?> parameters)
    {
        var pairs = parameters
            .Where(p => p.Value is not null)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}");
        return string.Join('&', pairs);
    }

    private sealed record QueryError(string Message, int? Code);

    private sealed record FeaturePage(IReadOnlyList<PulledRecord> Records, int RawCount, bool ExceededTransferLimit, QueryError? Error);

    private static FeaturePage ParseQueryPage(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var msg = error.TryGetProperty("message", out var m) ? m.GetString() ?? "Server error" : "Server error";
            int? code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var cv) ? cv : null;
            return new FeaturePage([], 0, false, new QueryError(msg, code));
        }

        var objectIdField = root.TryGetProperty("objectIdFieldName", out var oidf) ? oidf.GetString() : null;

        var records = new List<PulledRecord>();
        var rawCount = 0;
        if (root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
        {
            foreach (var feature in features.EnumerateArray())
            {
                rawCount++;
                if (DecodeFeature(feature, objectIdField) is { } pulled)
                {
                    records.Add(pulled);
                }
            }
        }

        var exceeded = root.TryGetProperty("exceededTransferLimit", out var etl)
            && etl.ValueKind == JsonValueKind.True;

        return new FeaturePage(records, rawCount, exceeded, null);
    }

    private static PulledRecord? DecodeFeature(JsonElement feature, string? objectIdField)
    {
        if (!feature.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        long? objectId = null;

        foreach (var attribute in attributes.EnumerateObject())
        {
            var isObjectId = (objectIdField is not null && string.Equals(attribute.Name, objectIdField, StringComparison.OrdinalIgnoreCase))
                || string.Equals(attribute.Name, "objectid", StringComparison.OrdinalIgnoreCase);

            if (isObjectId && attribute.Value.ValueKind == JsonValueKind.Number && attribute.Value.TryGetInt64(out var oid))
            {
                objectId = oid;
                continue; // the object id is transport metadata, not a captured value
            }

            values[attribute.Name] = ConvertJsonValue(attribute.Value);
        }

        if (objectId is null)
        {
            return null; // cannot key the feature without an object id
        }

        FieldGeoPoint? location = null;
        if (feature.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Object
            && geometry.TryGetProperty("x", out var x) && x.TryGetDouble(out var lon)
            && geometry.TryGetProperty("y", out var y) && y.TryGetDouble(out var lat))
        {
            location = new FieldGeoPoint(lat, lon);
        }

        var record = new FieldRecord
        {
            RecordId = objectId.Value.ToString(CultureInfo.InvariantCulture),
            FormId = string.Empty,
            Location = location,
            Status = RecordStatus.Submitted,
        };

        foreach (var (key, value) in values)
        {
            record.Values[key] = value;
        }

        return new PulledRecord(objectId.Value, record);
    }

    private static object? ConvertJsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        // Box each numeric branch separately: a unified ternary would widen the
        // integer branch to double, losing the integral type the submit encoding round-trips.
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : (object)value.GetDouble(),
        _ => value.GetRawText(),
    };

    private async Task<FeatureSyncResult> PostEditsAsync(
        GeoServicesTarget target,
        string editKey,
        string editJson,
        string resultKey,
        bool idempotent,
        CancellationToken cancellationToken)
    {
        FeatureSyncResult result = new(false, null, "No attempt was made.");

        for (var attempt = 1; attempt <= Math.Max(1, _retry.MaxAttempts); attempt++)
        {
            var isLast = attempt >= _retry.MaxAttempts;
            try
            {
                result = (await AttemptAsync(target, editKey, editJson, resultKey, cancellationToken).ConfigureAwait(false))
                    with { Attempts = attempt };

                if (result.Success || isLast || !IsRetryable(result, idempotent))
                {
                    return result;
                }
            }
            catch (HttpRequestException ex)
            {
                result = new FeatureSyncResult(false, null, ex.Message, Attempts: attempt);
                // A transport failure left no server response, so a non-idempotent
                // add is ambiguous (it may have committed) — never auto-retry it.
                if (!idempotent || isLast)
                {
                    return result;
                }
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                result = new FeatureSyncResult(false, null, ex.Message, Attempts: attempt); // request timeout
                if (!idempotent || isLast)
                {
                    return result;
                }
            }

            await Task.Delay(BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<FeatureSyncResult> AttemptAsync(
        GeoServicesTarget target,
        string editKey,
        string editJson,
        string resultKey,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            [editKey] = editJson,
            ["rollbackOnFailure"] = "true",
        });

        using var response = await _http.PostAsync(target.ApplyEditsUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // 5xx/429/408 are transient; mark them retryable via the HTTP status code.
            return new FeatureSyncResult(false, null, $"HTTP {(int)response.StatusCode}: {body}", (int)response.StatusCode);
        }

        return ParseResult(body, resultKey);
    }

    private static bool IsRetryable(FeatureSyncResult result, bool idempotent)
    {
        // HTTP-level failures carry their status as the code: only 408/429/5xx
        // are transient; other 4xx (auth/validation) are permanent.
        if (result.ErrorCode is >= 400 and < 600 and var http)
        {
            // For a non-idempotent add a 408/429/5xx is ambiguous — the write may
            // have committed before the failure response (e.g. 502-after-commit),
            // so retrying risks a duplicate feature. Only idempotent update/delete
            // are auto-retried at the HTTP level.
            if (!idempotent)
            {
                return false;
            }

            return http is 408 or 429 or (>= 500 and <= 599);
        }

        // Application/per-edit failures: permanent only when the message says so
        // (e.g. "feature not found"); otherwise treat as transient contention.
        var message = result.Error ?? string.Empty;
        return !FeatureSyncRetryPolicy.PermanentMessagePatterns.Any(
            p => message.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private TimeSpan BackoffDelay(int attempt)
    {
        if (_retry.EffectiveBaseDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // Exponential backoff with full jitter.
        var max = _retry.EffectiveBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * max);
    }

    /// <summary>Serializes a record to the GeoServices <c>adds</c> array JSON.</summary>
    /// <param name="record">Record to serialize.</param>
    /// <returns>A one-element JSON array string with geometry + attributes.</returns>
    public static string BuildAddsJson(FieldRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return BuildFeaturesJson(record, null);
    }

    private static string BuildFeaturesJson(FieldRecord record, long? objectId, string objectIdField = DefaultObjectIdField)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            WriteFeature(writer, record, objectId, objectIdField);
            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildFeaturesArrayJson(IReadOnlyList<FieldRecord> records)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var record in records)
            {
                WriteFeature(writer, record, objectId: null, DefaultObjectIdField);
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFeature(Utf8JsonWriter writer, FieldRecord record, long? objectId, string objectIdField)
    {
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
        if (objectId is { } oid)
        {
            // Write the id under the layer's actual object-id field so an update
            // keys the right feature (a wrong/lowercased name would be treated as
            // an unknown attribute and the update would match nothing).
            writer.WriteNumber(objectIdField, oid);
        }

        foreach (var (key, value) in record.Values)
        {
            if (value is null || string.Equals(key, objectIdField, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            WriteAttribute(writer, key, value);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
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

    private static FeatureSyncResult ParseResult(string body, string resultKey)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "Server error";
                int? code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var cv) ? cv : null;
                return new FeatureSyncResult(false, null, msg, code);
            }

            if (root.TryGetProperty(resultKey, out var results) && results.GetArrayLength() > 0)
            {
                var first = results[0];
                var success = first.TryGetProperty("success", out var s) && s.GetBoolean();
                long? oid = first.TryGetProperty("objectId", out var o) && o.TryGetInt64(out var v) ? v : null;
                if (success)
                {
                    return new FeatureSyncResult(true, oid, null);
                }

                // Per-edit failure: surface the server's error message + code.
                string? detail = "Edit was not applied.";
                int? errCode = null;
                if (first.TryGetProperty("error", out var e))
                {
                    detail = e.TryGetProperty("description", out var d) ? d.GetString() : detail;
                    errCode = e.TryGetProperty("code", out var ec) && ec.TryGetInt32(out var ecv) ? ecv : null;
                }

                return new FeatureSyncResult(false, oid, detail, errCode);
            }

            return new FeatureSyncResult(false, null, $"Unexpected response: no {resultKey}.");
        }
        catch (JsonException ex)
        {
            return new FeatureSyncResult(false, null, $"Invalid response: {ex.Message}");
        }
    }

    private static FeatureSyncResult ParseAttachmentResult(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "Server error";
                int? code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var cv) ? cv : null;
                return new FeatureSyncResult(false, null, msg, code);
            }

            if (root.TryGetProperty("addAttachmentResult", out var result))
            {
                var success = result.TryGetProperty("success", out var s) && s.GetBoolean();
                long? oid = result.TryGetProperty("objectId", out var o) && o.TryGetInt64(out var v) ? v : null;
                if (success)
                {
                    return new FeatureSyncResult(true, oid, null);
                }

                // Attachment failure: surface the server's error message + code.
                string? detail = "Attachment was not added.";
                int? errCode = null;
                if (result.TryGetProperty("error", out var e))
                {
                    detail = e.TryGetProperty("description", out var d) ? d.GetString() : detail;
                    errCode = e.TryGetProperty("code", out var ec) && ec.TryGetInt32(out var ecv) ? ecv : null;
                }

                return new FeatureSyncResult(false, oid, detail, errCode);
            }

            return new FeatureSyncResult(false, null, "Unexpected response: no addAttachmentResult.");
        }
        catch (JsonException ex)
        {
            return new FeatureSyncResult(false, null, $"Invalid response: {ex.Message}");
        }
    }
}
