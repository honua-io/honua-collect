// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

public sealed record GeoServicesTarget(string BaseUrl, string ServiceId, int LayerId)
{
    public string ApplyEditsUrl =>
        $"{BaseUrl.TrimEnd('/')}/rest/services/{ServiceId}/FeatureServer/{LayerId}/applyEdits";

    public string QueryUrl =>
        $"{BaseUrl.TrimEnd('/')}/rest/services/{ServiceId}/FeatureServer/{LayerId}/query";

    public string AttachmentUrl(long objectId) =>
        $"{BaseUrl.TrimEnd('/')}/rest/services/{ServiceId}/FeatureServer/{LayerId}/{objectId.ToString(CultureInfo.InvariantCulture)}/addAttachment";
}

public sealed record FeatureSyncResult(bool Success, long? ObjectId, string? Error, int? ErrorCode = null, int Attempts = 1)
{
    public static FeatureSyncResult Ok(long? objectId) => new(true, objectId, null);

    public static FeatureSyncResult Fail(string error, int? code = null) => new(false, null, error, code);

    public string FailureDescription => Success
        ? string.Empty
        : ErrorCode is { } code
            ? $"{Error ?? "Upload was rejected."} (code {code.ToString(CultureInfo.InvariantCulture)})"
            : Error ?? "Upload was rejected.";
}

public sealed record PulledRecord(long ObjectId, FieldRecord Record);

public sealed record FeatureQueryResult(bool Success, IReadOnlyList<PulledRecord> Records, string? Error, int? ErrorCode = null)
{
    public static FeatureQueryResult Empty { get; } = new(true, [], null);

    public static FeatureQueryResult Fail(string error, int? code = null) => new(false, [], error, code);
}

public sealed record FeatureSyncRetryPolicy(int MaxAttempts = 4, TimeSpan BaseDelay = default)
{
    public TimeSpan EffectiveBaseDelay => BaseDelay == default ? TimeSpan.FromMilliseconds(150) : BaseDelay;

    public static FeatureSyncRetryPolicy Default { get; } = new();

    public static FeatureSyncRetryPolicy None { get; } = new(MaxAttempts: 1);

    internal static readonly string[] PermanentMessagePatterns =
        ["not found", "does not exist", "invalid", "unauthorized", "forbidden", "permission", "not allowed", "not supported"];
}

public sealed class GeoServicesFeatureSync
{
    private const string DefaultObjectIdField = "objectid";

    private const string DefaultGlobalIdField = "globalid";

    private static readonly Guid IdempotencyNamespace = new("9f1d2c3b-4a5e-46f7-8a9b-0c1d2e3f4a5b");

    private const long MinUploadBytesPerSecond = 8 * 1024;

    private static readonly TimeSpan MinUploadTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly HttpClient _uploadHttp;
    private readonly FeatureSyncRetryPolicy _retry;

    public GeoServicesFeatureSync(
        HttpClient http,
        FeatureSyncRetryPolicy? retryPolicy = null,
        HttpClient? uploadHttp = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _uploadHttp = uploadHttp ?? _http;
        _retry = retryPolicy ?? FeatureSyncRetryPolicy.Default;
    }

    public Task<FeatureSyncResult> SubmitAsync(
        FieldRecord record,
        GeoServicesTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(target);
        return PostEditsAsync(
            target, "adds", BuildFeaturesJson(record, null, includeGlobalId: true), "addResults",
            idempotent: false, useGlobalIds: true, cancellationToken);
    }

    public static string GlobalIdFor(FieldRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return GlobalIdFor(record.RecordId);
    }

    public static string GlobalIdFor(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return DeterministicGuid(IdempotencyNamespace, recordId).ToString("B").ToUpperInvariant();
    }

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
            // Each batched add carries its stable client GlobalID;
            // useGlobalIds lets a dedupe-aware server treat a re-sent add as a no-op.
            ["useGlobalIds"] = "true",
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

            if (TryReadServerError(root, out var msg, out var code))
            {
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

                var outcome = ReadEditOutcome(addResults[i], "Edit was not applied.");
                results[i] = outcome.Success
                    ? FeatureSyncResult.Ok(outcome.ObjectId)
                    : FeatureSyncResult.Fail(outcome.Detail ?? "Edit was not applied.", outcome.Code);
            }

            return results;
        }
        catch (JsonException ex)
        {
            return FailEach(expectedCount, $"Invalid response: {ex.Message}");
        }
    }

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
        return PostEditsAsync(
            target, "updates", BuildFeaturesJson(record, objectId, objectIdField), "updateResults",
            idempotent: true, useGlobalIds: false, cancellationToken);
    }

    public async Task<FeatureSyncResult> DeleteAsync(
        long objectId,
        GeoServicesTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var deletes = $"[{objectId.ToString(CultureInfo.InvariantCulture)}]";
        var result = await PostEditsAsync(
            target, "deletes", deletes, "deleteResults", idempotent: true, useGlobalIds: false, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success && IsAlreadyAbsent(result))
        {
            // The feature is already gone: report the deletion as achieved.
            return FeatureSyncResult.Ok(objectId) with { Attempts = result.Attempts };
        }

        return result;
    }

    private static bool IsAlreadyAbsent(FeatureSyncResult result)
    {
        var message = result.Error ?? string.Empty;
        return AbsentFeaturePatterns.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] AbsentFeaturePatterns =
        ["not found", "does not exist", "unknown object", "cannot find", "no feature"];

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

        // Bound the upload with a deadline sized to the file, NOT a flat per-request
        // timeout. _uploadHttp is configured with an effectively-infinite
        // HttpClient.Timeout precisely so this size-derived budget governs instead:
        // a multi-hundred-MB attachment over a slow field uplink cannot complete in
        // the 30s the small query/edit requests use, so reusing that cap here would
        // abort every large upload regardless of progress. The deadline is generous
        // (min-throughput floor) so an upload that is making forward progress
        // finishes, while a truly stalled one still fails instead of hanging forever.
        var uploadBudget = ComputeUploadTimeout(fileStream.Length);
        using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        uploadCts.CancelAfter(uploadBudget);

        // Convert transport faults into a failed result like SubmitBatchAsync/
        // QueryAsync/PostEditsAsync do, rather than letting them escape: an uncaught
        // throw here reaches the upload caller (which has no catch) and can MarkFailed
        // a record that was already MarkSynced, re-queuing it and risking a duplicate
        // attachment add on the next drain.
        string body;
        try
        {
            using var response = await _uploadHttp.PostAsync(target.AttachmentUrl(objectId), content, uploadCts.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(uploadCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new FeatureSyncResult(false, null, $"HTTP {(int)response.StatusCode}: {body}", (int)response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            return FeatureSyncResult.Fail(ex.Message);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's token was not cancelled, so this is the upload deadline
            // firing (or an HttpClient request timeout) — a transport failure, not a
            // user cancel. Surface it as a failed result so the upload is retried.
            return FeatureSyncResult.Fail(ex.Message);
        }

        return ParseAttachmentResult(body);
    }

    private static TimeSpan ComputeUploadTimeout(long fileLengthBytes)
    {
        if (fileLengthBytes <= 0)
        {
            return MinUploadTimeout;
        }

        var seconds = (double)fileLengthBytes / MinUploadBytesPerSecond;
        var derived = TimeSpan.FromSeconds(seconds);
        return derived > MinUploadTimeout ? derived : MinUploadTimeout;
    }

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
                // GeoServices REST spec requires orderByFields, otherwise the server may
                // return rows in an arbitrary/changing order across pages and a
                // layer larger than the page size can silently skip or double-count
                // features. Order by the object-id field (this product's layers key
                // on "objectid"; service field references are case-insensitive in the
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
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A TaskCanceledException whose token is NOT the caller's is an
                // HttpClient request timeout, not a user cancel. Surface it as a
                // failed pull (matching SubmitAsync/AddAttachmentAsync/PostEditsAsync)
                // so the caller reports a sync error and can retry, rather than
                // silently treating the aborted pull as a deliberate cancellation.
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

    public async Task<InboxPage> QueryPageAsync(
        GeoServicesTarget target,
        int offset,
        int pageSize = 100,
        string where = "1=1",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var effectiveWhere = string.IsNullOrWhiteSpace(where) ? "1=1" : where;
        var query = new Dictionary<string, string?>
        {
            ["f"] = "json",
            ["where"] = effectiveWhere,
            ["outFields"] = "*",
            ["returnGeometry"] = "true",
            ["orderByFields"] = DefaultObjectIdField,
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
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        FeaturePage page;
        try
        {
            page = ParseQueryPage(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid response: {ex.Message}", ex);
        }

        if (page.Error is { } err)
        {
            throw new InvalidOperationException(err.Message);
        }

        var hasMore = page.ExceededTransferLimit && page.RawCount > 0;
        var nextOffset = offset + page.RawCount;
        return new InboxPage(page.Records, hasMore, nextOffset);
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

        if (TryReadServerError(root, out var msg, out var code))
        {
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

            values[attribute.Name] = JsonValueConverter.ToClrValue(attribute.Value);
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

    private readonly record struct EditOutcome(bool Success, long? ObjectId, string? Detail, int? Code);

    private static bool TryReadServerError(JsonElement root, out string message, out int? code)
    {
        if (root.TryGetProperty("error", out var error))
        {
            message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "Server error" : "Server error";
            code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var cv) ? cv : null;
            return true;
        }

        message = "Server error";
        code = null;
        return false;
    }

    private static EditOutcome ReadEditOutcome(JsonElement item, string defaultDetail)
    {
        var success = item.TryGetProperty("success", out var s) && s.GetBoolean();
        long? oid = item.TryGetProperty("objectId", out var o) && o.TryGetInt64(out var v) ? v : null;
        if (success)
        {
            return new EditOutcome(true, oid, null, null);
        }

        var detail = defaultDetail;
        int? errCode = null;
        if (item.TryGetProperty("error", out var e))
        {
            detail = (e.TryGetProperty("description", out var d) ? d.GetString() : detail) ?? detail;
            errCode = e.TryGetProperty("code", out var ec) && ec.TryGetInt32(out var ecv) ? ecv : null;
        }

        return new EditOutcome(false, oid, detail, errCode);
    }

    private async Task<FeatureSyncResult> PostEditsAsync(
        GeoServicesTarget target,
        string editKey,
        string editJson,
        string resultKey,
        bool idempotent,
        bool useGlobalIds,
        CancellationToken cancellationToken)
    {
        FeatureSyncResult result = new(false, null, "No attempt was made.");

        for (var attempt = 1; attempt <= Math.Max(1, _retry.MaxAttempts); attempt++)
        {
            var isLast = attempt >= _retry.MaxAttempts;
            try
            {
                result = (await AttemptAsync(target, editKey, editJson, resultKey, useGlobalIds, cancellationToken).ConfigureAwait(false))
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
        bool useGlobalIds,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["f"] = "json",
            [editKey] = editJson,
            ["rollbackOnFailure"] = "true",
        };

        if (useGlobalIds)
        {
            // The edit carries a stable client GlobalID; tell a dedupe-aware server
            // to key on it so a re-sent add is a no-op rather than a duplicate.
            fields["useGlobalIds"] = "true";
        }

        using var content = new FormUrlEncodedContent(fields);

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

    private static Guid DeterministicGuid(Guid ns, string name)
    {
        var nsBytes = ns.ToByteArray();
        SwapGuidByteOrder(nsBytes); // .NET's Guid byte layout -> RFC big-endian
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);

        var input = new byte[nsBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(nsBytes, 0, input, 0, nsBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, nsBytes.Length, nameBytes.Length);

        var hash = System.Security.Cryptography.SHA1.HashData(input);

        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50); // version 5
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC 4122 variant

        SwapGuidByteOrder(guidBytes); // RFC big-endian -> .NET Guid byte layout
        return new Guid(guidBytes);
    }

    private static void SwapGuidByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }

    public static string BuildAddsJson(FieldRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return BuildFeaturesJson(record, null);
    }

    private static string BuildFeaturesJson(
        FieldRecord record, long? objectId, string objectIdField = DefaultObjectIdField, bool includeGlobalId = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            WriteFeature(writer, record, objectId, objectIdField, includeGlobalId);
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
                WriteFeature(writer, record, objectId: null, DefaultObjectIdField, includeGlobalId: true);
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFeature(
        Utf8JsonWriter writer, FieldRecord record, long? objectId, string objectIdField, bool includeGlobalId)
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

        if (includeGlobalId && !string.IsNullOrWhiteSpace(record.RecordId))
        {
            // Stable client GlobalID = the at-most-once idempotency key for this add.
            // Deterministic in RecordId, so every (re)attempt of the same record
            // sends the same value and a dedupe-aware server collapses the retry.
            writer.WriteString(DefaultGlobalIdField, GlobalIdFor(record.RecordId));
        }

        foreach (var (key, value) in record.Values)
        {
            if (value is null
                || string.Equals(key, objectIdField, StringComparison.OrdinalIgnoreCase)
                || (includeGlobalId && string.Equals(key, DefaultGlobalIdField, StringComparison.OrdinalIgnoreCase)))
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

            if (TryReadServerError(root, out var msg, out var code))
            {
                return new FeatureSyncResult(false, null, msg, code);
            }

            if (root.TryGetProperty(resultKey, out var results) && results.GetArrayLength() > 0)
            {
                var outcome = ReadEditOutcome(results[0], "Edit was not applied.");
                return outcome.Success
                    ? new FeatureSyncResult(true, outcome.ObjectId, null)
                    : new FeatureSyncResult(false, outcome.ObjectId, outcome.Detail, outcome.Code);
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

            if (TryReadServerError(root, out var msg, out var code))
            {
                return new FeatureSyncResult(false, null, msg, code);
            }

            if (root.TryGetProperty("addAttachmentResult", out var result))
            {
                var outcome = ReadEditOutcome(result, "Attachment was not added.");
                return outcome.Success
                    ? new FeatureSyncResult(true, outcome.ObjectId, null)
                    : new FeatureSyncResult(false, outcome.ObjectId, outcome.Detail, outcome.Code);
            }

            return new FeatureSyncResult(false, null, "Unexpected response: no addAttachmentResult.");
        }
        catch (JsonException ex)
        {
            return new FeatureSyncResult(false, null, $"Invalid response: {ex.Message}");
        }
    }
}
