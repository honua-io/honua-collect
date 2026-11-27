// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Field.Core.Forms;
using Honua.Field.Core.Records;
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
    public Task SaveAsync(FieldRecordEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _store.SaveDocumentAsync(Collection, ToDocument(entry), ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FieldRecordEntry>> LoadAllAsync(CancellationToken ct = default)
    {
        var documents = await _store.LoadDocumentsAsync(Collection, ct).ConfigureAwait(false);
        return documents.Select(ReadDocument).ToArray();
    }

    /// <inheritdoc />
    public Task DeleteAsync(string recordId, CancellationToken ct = default) =>
        _store.DeleteDocumentAsync(Collection, recordId, ct);

    private static OfflineDocument ToDocument(FieldRecordEntry entry)
    {
        var record = entry.Record;
        var data = new MobileRecordData(
            record.FormId, record.Status, record.AssignedUserId, record.Location,
            record.CreatedAtUtc, record.SubmittedAtUtc, record.CompletedAtUtc,
            JsonSerializer.SerializeToElement(record.Values, SqliteRecordStore.ValuesJsonOptions),
            entry.SyncState, entry.RemoteId, entry.LastError, entry.FailedAttempts, entry.LastSyncedUtc,
            entry.Version, entry.PublishedSubmission, entry.PublishedDraft);
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
        SqliteRecordStore.Rehydrate(entry, data.SyncState, data.RemoteId, data.LastError, data.FailedAttempts, data.LastSyncedUtc);
        entry.SetVersion(data.Version);
        return entry;
    }
}

internal sealed record MobileRecordData(
    string FormId, RecordStatus Status, string? AssignedUserId, FieldGeoPoint? Location,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? SubmittedAtUtc, DateTimeOffset? CompletedAtUtc,
    JsonElement Values, RecordSyncState SyncState, string? RemoteId, string? LastError,
    int FailedAttempts, DateTimeOffset? LastSyncedUtc, int Version,
    PublishedFormSubmission? Submission, PublishedFormDraft? Draft);

[JsonSerializable(typeof(MobileRecordData))]
internal sealed partial class MobileRecordJsonContext : JsonSerializerContext
{
}
