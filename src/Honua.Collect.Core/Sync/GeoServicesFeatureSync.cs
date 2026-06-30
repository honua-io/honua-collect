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

    /// <summary>
    /// The attribute name a feature's client-generated GlobalID is written under on
    /// an <c>add</c>. This is the at-most-once idempotency key: a stable id the
    /// client derives once (from the record's <see cref="FieldRecord.RecordId"/>)
    /// and re-sends on every (re)attempt, so a lost-response add that actually
    /// committed is recognised by the server on the retry instead of inserting a
    /// second feature. Paired with <c>useGlobalIds=true</c> on the request, this is
    /// the Esri/GeoServices shape a server dedupes on (server-side dedupe on this
    /// key is the matching honua-server follow-up; see the PR notes).
    /// </summary>
    private const string DefaultGlobalIdField = "globalid";

    /// <summary>
    /// Namespace for the deterministic (RFC 4122 v5) GlobalID derived from a
    /// record id. Fixed so the same <see cref="FieldRecord.RecordId"/> always maps
    /// to the same GlobalID — across retries, app restarts, and re-edits — without
    /// having to persist a separate key (the record id is already durably stored).
    /// </summary>
    private static readonly Guid IdempotencyNamespace = new("9f1d2c3b-4a5e-46f7-8a9b-0c1d2e3f4a5b");

    /// <summary>
    /// Minimum upload throughput (bytes/second) assumed when sizing the
    /// attachment-upload deadline. A slow field uplink is the worst case this must
    /// tolerate; 8 KB/s (~64 kbit/s) is well below even a poor cellular link, so a
    /// file that is making any forward progress at all will finish inside the
    /// derived deadline rather than tripping a flat wall-clock timeout.
    /// </summary>
    private const long MinUploadBytesPerSecond = 8 * 1024;

    /// <summary>
    /// Floor for the attachment-upload deadline, so tiny files still get a sane
    /// allowance for connection setup, TLS, and server processing.
    /// </summary>
    private static readonly TimeSpan MinUploadTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly HttpClient _uploadHttp;
    private readonly FeatureSyncRetryPolicy _retry;

    /// <summary>Creates the sync client over a configured HTTP client.</summary>
    /// <param name="http">HTTP client (base address/auth headers set by the host).</param>
    /// <param name="retryPolicy">Retry policy; defaults to <see cref="FeatureSyncRetryPolicy.Default"/>.</param>
    /// <param name="uploadHttp">
    /// Optional dedicated client for large attachment bodies. Its
    /// <see cref="HttpClient.Timeout"/> MUST be long (ideally
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>) because
    /// <see cref="AddAttachmentAsync"/> bounds the upload with its own
    /// size-derived per-request deadline instead — a short total timeout on this
    /// client would defeat that and abort large media on slow links. Defaults to
    /// <paramref name="http"/> when not supplied (e.g. in tests).
    /// </param>
    public GeoServicesFeatureSync(
        HttpClient http,
        FeatureSyncRetryPolicy? retryPolicy = null,
        HttpClient? uploadHttp = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _uploadHttp = uploadHttp ?? _http;
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
    /// provably non-committed.
    /// <para>
    /// To make the add idempotent across a re-attempt the caller <em>does</em> make
    /// (a next sync pass, a restart, or a re-queue after a lost response), the add
    /// carries a stable client-generated GlobalID derived from
    /// <see cref="FieldRecord.RecordId"/> (see <see cref="GlobalIdFor(FieldRecord)"/>),
    /// sent under the <c>globalid</c> attribute with <c>useGlobalIds=true</c>. The
    /// same record always sends the same key, so a server that dedupes on GlobalID
    /// turns a duplicate add into a no-op — the at-most-once guarantee. The
    /// matching server-side dedupe is a honua-server follow-up; until then the
    /// client key is inert but harmless and the suppress-on-ambiguity behaviour
    /// above still prevents auto-retry duplicates.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// The stable, client-generated GlobalID used as the at-most-once idempotency
    /// key for adding this record. It is a deterministic function of the record's
    /// <see cref="FieldRecord.RecordId"/>, so it is identical on every (re)attempt,
    /// after a restart, and after the entry is rehydrated from storage — no extra
    /// persistence is needed beyond the record id itself. Returned in Esri registry
    /// format (<c>{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}</c>).
    /// </summary>
    /// <param name="record">The record whose stable GlobalID is wanted.</param>
    /// <returns>The deterministic GlobalID for the record.</returns>
    public static string GlobalIdFor(FieldRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return GlobalIdFor(record.RecordId);
    }

    /// <summary>
    /// The stable client GlobalID for a record id. See <see cref="GlobalIdFor(FieldRecord)"/>.
    /// </summary>
    /// <param name="recordId">The stable record id to derive the GlobalID from.</param>
    /// <returns>The deterministic GlobalID for the record id.</returns>
    public static string GlobalIdFor(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return DeterministicGuid(IdempotencyNamespace, recordId).ToString("B").ToUpperInvariant();
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
            // Each batched add carries its stable client GlobalID (see SubmitAsync);
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
        return PostEditsAsync(
            target, "updates", BuildFeaturesJson(record, objectId, objectIdField), "updateResults",
            idempotent: true, useGlobalIds: false, cancellationToken);
    }

    /// <summary>Deletes a feature by its object id.</summary>
    /// <param name="objectId">Server object id to delete.</param>
    /// <param name="target">Target service/layer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result for the delete.</returns>
    /// <remarks>
    /// Delete is idempotent: deleting a feature the server has already removed is
    /// the desired end state, not an error. A "feature not found"/"does not exist"
    /// rejection is therefore coerced to success so a retried delete, or a delete of
    /// a record another client already removed, does not surface a spurious failure
    /// (the delete→delete path the audit flagged). Combined with the non-retry of an
    /// ambiguous transport failure, a delete never duplicates work.
    /// </remarks>
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

    /// <summary>
    /// Whether a failed result indicates the target feature is already absent — the
    /// signal a delete should treat as an idempotent no-op rather than a failure.
    /// </summary>
    private static bool IsAlreadyAbsent(FeatureSyncResult result)
    {
        var message = result.Error ?? string.Empty;
        return AbsentFeaturePatterns.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Substrings (case-insensitive) in a server error message that mean the feature
    /// being deleted no longer exists, so a delete of it is a successful no-op.
    /// </summary>
    private static readonly string[] AbsentFeaturePatterns =
        ["not found", "does not exist", "unknown object", "cannot find", "no feature"];

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

    /// <summary>
    /// Derives an attachment-upload deadline from the file size: the time it would
    /// take to send the body at <see cref="MinUploadBytesPerSecond"/>, floored at
    /// <see cref="MinUploadTimeout"/>. This replaces a flat wall-clock timeout so
    /// large media on a slow link is judged on whether it is making progress, not
    /// on a fixed cap it can never beat.
    /// </summary>
    /// <param name="fileLengthBytes">Size of the file being uploaded, in bytes.</param>
    /// <returns>The per-request deadline for the upload.</returns>
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

    /// <summary>The outcome of a single per-edit result item from a GeoServices response.</summary>
    /// <param name="Success">Whether this edit was applied.</param>
    /// <param name="ObjectId">The server-assigned object id, when present.</param>
    /// <param name="Detail">The failure detail when not successful; otherwise the supplied default.</param>
    /// <param name="Code">The per-edit error code, when present.</param>
    private readonly record struct EditOutcome(bool Success, long? ObjectId, string? Detail, int? Code);

    /// <summary>
    /// Reads the top-level GeoServices <c>{"error":{message,code}}</c> envelope.
    /// This is the wire contract for a failed applyEdits/query/attachment call;
    /// keeping it in one place stops the four response parsers from drifting in
    /// how they decode (and default) the server error.
    /// </summary>
    /// <param name="root">The response root element.</param>
    /// <param name="message">The error message, defaulting to <c>"Server error"</c>.</param>
    /// <param name="code">The error code, when present.</param>
    /// <returns><see langword="true"/> when the response carries a server error.</returns>
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

    /// <summary>
    /// Reads a single GeoServices per-edit result item
    /// (<c>{success, objectId, error:{description,code}}</c>) shared by the
    /// add/update/delete and addAttachment result shapes.
    /// </summary>
    /// <param name="item">The per-edit result element.</param>
    /// <param name="defaultDetail">
    /// The failure detail to use when the item omits an error description
    /// (context-specific, e.g. "Edit was not applied." vs "Attachment was not added.").
    /// </param>
    /// <returns>The decoded outcome.</returns>
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

    /// <summary>
    /// Computes an RFC 4122 §4.3 name-based (version 5, SHA-1) UUID from a namespace
    /// and a name. Deterministic, so it yields a stable GlobalID for a given record
    /// id without storing one.
    /// </summary>
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

    /// <summary>Serializes a record to the GeoServices <c>adds</c> array JSON.</summary>
    /// <param name="record">Record to serialize.</param>
    /// <returns>A one-element JSON array string with geometry + attributes.</returns>
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
