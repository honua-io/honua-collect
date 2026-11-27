// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Field.Core.Forms;
using Honua.Field.Core.Records;
using Honua.Field.Core.Sync;
using Honua.Mobile.Offline.GeoPackage;
using Honua.Sdk.Field.Records;

namespace Honua.Field.Core.Storage;

/// <summary>Persists Field records and their replay state in the encrypted Mobile SDK store.</summary>
public sealed class MobileRecordStore : IRecordStore
{
    private const string Collection = "honua-field-records-v1";
    private readonly GeoPackageSyncStore _store;

    private MobileRecordStore(GeoPackageSyncStore store) => _store = store;

    /// <summary>Opens the existing encrypted database and imports legacy records once.</summary>
    public static async Task<MobileRecordStore> OpenEncryptedAsync(string path, string key, CancellationToken ct = default)
    {
        var legacy = await SqliteRecordStore.OpenEncryptedAsync(path, key, ct).ConfigureAwait(false);
        var store = new GeoPackageSyncStore(new GeoPackageSyncStoreOptions { DatabasePath = path, EncryptionKey = key });
        await store.InitializeAsync(ct).ConfigureAwait(false);
        if (!await store.IsDocumentCollectionImportedAsync(Collection, ct).ConfigureAwait(false))
        {
            var entries = await legacy.LoadAllAsync(ct).ConfigureAwait(false);
            await store.ImportDocumentCollectionAsync(Collection, entries.Select(ToDocument).ToArray(), ct).ConfigureAwait(false);
        }

        return new MobileRecordStore(store);
    }

    /// <inheritdoc />
    public async Task SaveAsync(FieldRecordEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var document = ToDocument(entry);
        var previous = await _store.LoadDocumentAsync(Collection, document.Id, ct).ConfigureAwait(false);
        var previousEntry = previous is null ? null : ReadDocument(previous);
        bool committed;
        if (entry.PublishedSubmission is { } submission)
        {
            if (!CanReplay(entry) || (previousEntry is not null && !previousEntry.CanOpenPublishedEditor
                && (previousEntry.Record.Status != RecordStatus.Draft || previousEntry.PublishedSubmission is not null)))
            {
                throw new InvalidOperationException("This published inspection is already in the outbox or has been sent.");
            }

            var (operationId, payload) = FreezeOperation(document.Id, submission);
            if (previousEntry?.PublishedSubmission is { } prior && FreezeOperation(document.Id, prior).Id == operationId)
            {
                throw new InvalidOperationException("A corrected submission needs a new request identifier.");
            }

            committed = await _store.EnqueueOperationAsync(Collection, operationId, document, payload, previous?.Json, ct)
                .ConfigureAwait(false);
            if (committed)
            {
                // A matching completed operation is a successful SDK no-op.
                var current = await _store.LoadDocumentAsync(Collection, document.Id, ct).ConfigureAwait(false);
                committed = current?.Json == document.Json;
            }
        }
        else
        {
            if (previousEntry?.PublishedSubmission is not null
                && (!previousEntry.CanOpenPublishedEditor || entry.PublishedDraft?.ReplacesRejectedSubmission != true))
            {
                throw new InvalidOperationException("A queued published submission cannot be overwritten.");
            }

            committed = await _store.CompareExchangeDocumentAsync(Collection, document, previous?.Json, ct)
                .ConfigureAwait(false);
        }

        if (!committed)
        {
            throw new InvalidOperationException("This record changed before it could be saved. Reopen it and try again.");
        }
    }

    /// <summary>Claims and replays one durable published-form request through the shared Mobile outbox.</summary>
    public async Task<bool> ReplayPublishedAsync(FieldRecordEntry entry,
        Func<PublishedFormSubmission, CancellationToken, Task<FeatureSyncResult>> upload, CancellationToken ct = default,
        string? mediaDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(upload);
        var document = await _store.LoadDocumentAsync(Collection, entry.Record.RecordId, ct).ConfigureAwait(false);
        if (document is null)
        {
            return false;
        }

        var durable = ReadDocument(document);
        if (!CanReplay(durable) || durable.PublishedSubmission is not { } submission)
        {
            CopySyncState(durable, entry);
            return false;
        }

        var (operationId, payload) = FreezeOperation(document.Id, submission);
        if (entry.PublishedSubmission is not { } requested
            || FreezeOperation(document.Id, requested).Id != operationId)
        {
            return false;
        }

        if (!await _store.AdoptOperationAsync(Collection, operationId, document, payload, ct).ConfigureAwait(false))
        {
            return false;
        }

        // Keep the request deadline below the lease so a second process can recover an
        // interrupted upload without contending with a live request.
        var replay = mediaDirectory is null ? submission : submission.ResolveMediaPaths(mediaDirectory);
        var uploadTimeout = GetReplayTimeout(replay);
        var claim = await _store.ClaimOperationAsync(Collection, document.Id, operationId, uploadTimeout + TimeSpan.FromMinutes(1), ct)
            .ConfigureAwait(false);
        if (claim is null)
        {
            return false;
        }

        FeatureSyncResult result;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(uploadTimeout);
        try
        {
            if (!string.Equals(claim.PayloadJson, payload, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Stored form submission does not match the claimed operation.");
            }

            result = await upload(replay, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _store.CompleteOperationAsync(Collection, claim, document, document.Json,
                OfflineOperationDisposition.Retry, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            result = FeatureSyncResult.Fail("The form upload timed out. Try syncing again.");
        }
        catch (Exception)
        {
            result = FeatureSyncResult.Fail("The form upload could not complete. Try syncing again.");
        }

        durable.MarkUploading();
        OfflineOperationDisposition disposition;
        if (result.Success)
        {
            durable.MarkSynced(result.ObjectId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            disposition = OfflineOperationDisposition.Succeeded;
        }
        else if (result.CanCorrectSubmission)
        {
            durable.MarkSubmissionRejected(result.FailureDescription);
            disposition = OfflineOperationDisposition.Rejected;
        }
        else
        {
            durable.MarkFailed(result.FailureDescription);
            disposition = OfflineOperationDisposition.Retry;
        }

        if (!await _store.CompleteOperationAsync(Collection, claim, ToDocument(durable), document.Json, disposition,
            CancellationToken.None).ConfigureAwait(false))
        {
            return false;
        }

        CopySyncState(durable, entry);
        return result.Success;
    }

    private static TimeSpan GetReplayTimeout(PublishedFormSubmission submission)
    {
        long bytes = 0;
        foreach (var file in submission.Files)
        {
            try
            {
                var length = new FileInfo(file.LocalPath).Length;
                bytes += Math.Min(length, long.MaxValue - bytes);
            }
            catch (IOException)
            {
                // Let the transport report the unreadable attachment.
            }
            catch (UnauthorizedAccessException)
            {
                // Let the transport report the unreadable attachment.
            }
        }

        var uploadTimeout = UploadDeadline.GetTimeout(bytes) + TimeSpan.FromMinutes(1);
        return TimeSpan.FromMilliseconds(Math.Min(uploadTimeout.TotalMilliseconds, uint.MaxValue - 1));
    }

    private static bool CanReplay(FieldRecordEntry entry) =>
        entry.IsPendingUpload && entry.Record.Status != RecordStatus.Deleted && entry.RemoteId is null;

    private static (string Id, string Payload) FreezeOperation(string recordId, PublishedFormSubmission submission)
    {
        using var request = JsonDocument.Parse(submission.RequestJson);
        var key = request.RootElement.GetProperty("idempotencyKey").GetString()
            ?? throw new JsonException("Form submission requires an idempotency key.");
        var identity = JsonSerializer.SerializeToUtf8Bytes(new[] { recordId, key },
            MobileRecordJsonContext.Default.StringArray);
        return (Convert.ToHexString(SHA256.HashData(identity)),
            JsonSerializer.Serialize(submission, MobileRecordJsonContext.Default.PublishedFormSubmission));
    }

    private static void CopySyncState(FieldRecordEntry source, FieldRecordEntry target) =>
        target.RestoreSyncState(source.SyncState, source.RemoteId, source.LastError, source.FailedAttempts,
            source.LastAttemptUtc, source.LastSyncedUtc);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FieldRecordEntry>> LoadAllAsync(CancellationToken ct = default)
    {
        var documents = await _store.LoadDocumentsAsync(Collection, ct).ConfigureAwait(false);
        return documents.Select(ReadDocument).ToArray();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string recordId, CancellationToken ct = default)
    {
        var document = await _store.LoadDocumentAsync(Collection, recordId, ct).ConfigureAwait(false);
        if (document is not null && ((ReadDocument(document) is { PublishedSubmission: not null } entry && CanReplay(entry))
            || !await _store.CompareDeleteDocumentAsync(Collection, recordId, document.Json, ct).ConfigureAwait(false)))
        {
            throw new InvalidOperationException("A queued or changed record cannot be deleted.");
        }
    }

    private static OfflineDocument ToDocument(FieldRecordEntry entry)
    {
        var record = entry.Record;
        var data = new MobileRecordData(
            record.FormId, record.Status, record.AssignedUserId, record.Location,
            record.CreatedAtUtc, record.SubmittedAtUtc, record.CompletedAtUtc,
            JsonSerializer.SerializeToElement(record.Values, SqliteRecordStore.ValuesJsonOptions),
            entry.SyncState, entry.RemoteId, entry.LastError, entry.FailedAttempts, entry.LastSyncedUtc,
            entry.Version, entry.PublishedSubmission, entry.PublishedDraft, entry.LastAttemptUtc);
        return new OfflineDocument(record.RecordId, JsonSerializer.Serialize(data, MobileRecordJsonContext.Default.MobileRecordData));
    }

    private static FieldRecordEntry ReadDocument(OfflineDocument document)
    {
        var data = JsonSerializer.Deserialize(document.Json, MobileRecordJsonContext.Default.MobileRecordData)
            ?? throw new JsonException("Stored Field record cannot be null.");
        var record = new FieldRecord
        {
            RecordId = document.Id,
            FormId = data.FormId,
            Status = data.Status,
            AssignedUserId = data.AssignedUserId,
            Location = data.Location,
            CreatedAtUtc = data.CreatedAtUtc,
            SubmittedAtUtc = data.SubmittedAtUtc,
            CompletedAtUtc = data.CompletedAtUtc,
        };
        foreach (var value in data.Values.EnumerateObject())
        {
            record.Values[value.Name] = value.Value.ValueKind == JsonValueKind.Null ? null : value.Value;
        }

        var entry = new FieldRecordEntry(record) { PublishedSubmission = data.Submission, PublishedDraft = data.Draft };
        entry.RestoreSyncState(data.SyncState, data.RemoteId, data.LastError, data.FailedAttempts, data.LastAttemptUtc, data.LastSyncedUtc);
        entry.SetVersion(data.Version);
        return entry;
    }
}

internal sealed record MobileRecordData(
    string FormId, RecordStatus Status, string? AssignedUserId, FieldGeoPoint? Location,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? SubmittedAtUtc, DateTimeOffset? CompletedAtUtc,
    JsonElement Values, RecordSyncState SyncState, string? RemoteId, string? LastError,
    int FailedAttempts, DateTimeOffset? LastSyncedUtc, int Version,
    PublishedFormSubmission? Submission, PublishedFormDraft? Draft, DateTimeOffset? LastAttemptUtc = null);

[JsonSerializable(typeof(MobileRecordData))]
[JsonSerializable(typeof(PublishedFormSubmission))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class MobileRecordJsonContext : JsonSerializerContext
{
}
