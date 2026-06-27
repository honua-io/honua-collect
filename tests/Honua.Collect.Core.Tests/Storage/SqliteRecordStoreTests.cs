using System.Text.Json;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Storage;

public sealed class SqliteRecordStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRecordStore _store;

    public SqliteRecordStoreTests()
    {
        // GetTempFileName creates a real, unique file; SQLite happily reuses it.
        _dbPath = Path.GetTempFileName();
        _store = new SqliteRecordStore($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task Database_is_opened_in_WAL_mode()
    {
        await _store.SaveAsync(new CollectRecordEntry(NewRecord("r1", RecordStatus.Submitted)));

        await using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await raw.OpenAsync();
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)await cmd.ExecuteScalarAsync();

        Assert.Equal("wal", mode, ignoreCase: true); // AUD-252
    }

    [Fact]
    public async Task Concurrent_writes_do_not_throw_sqlite_busy()
    {
        // busy_timeout + WAL let a concurrent writer wait for the lock instead of
        // throwing SQLITE_BUSY immediately (AUD-252).
        var saves = Enumerable.Range(0, 24)
            .Select(i => _store.SaveAsync(new CollectRecordEntry(NewRecord($"r{i}", RecordStatus.Submitted))));
        await Task.WhenAll(saves);

        Assert.Equal(24, (await _store.LoadAllAsync()).Count);
    }

    private static FieldRecord NewRecord(
        string recordId,
        RecordStatus status,
        FieldGeoPoint? location = null,
        Dictionary<string, object?>? values = null)
    {
        var record = new FieldRecord
        {
            RecordId = recordId,
            FormId = "tree-survey",
            Status = status,
            Location = location,
        };

        if (values is not null)
        {
            foreach (var pair in values)
            {
                record.Values[pair.Key] = pair.Value;
            }
        }

        return record;
    }

    private static CollectRecordEntry Draft(string id)
        => new(NewRecord(id, RecordStatus.Draft));

    private static CollectRecordEntry Outbox(string id)
    {
        var entry = new CollectRecordEntry(NewRecord(id, RecordStatus.Submitted));
        entry.MarkPending();
        return entry;
    }

    private static CollectRecordEntry Synced(string id, string remoteId)
    {
        var entry = new CollectRecordEntry(NewRecord(id, RecordStatus.Submitted));
        entry.MarkPending();
        entry.MarkSynced(remoteId, new DateTimeOffset(2026, 6, 1, 8, 30, 0, TimeSpan.Zero));
        return entry;
    }

    private static CollectRecordEntry Failed(string id, string error, int attempts)
    {
        var entry = new CollectRecordEntry(NewRecord(id, RecordStatus.Submitted));
        entry.MarkPending();
        for (var i = 0; i < attempts; i++)
        {
            entry.MarkFailed(error);
        }

        return entry;
    }

    [Fact]
    public async Task LoadAll_on_empty_store_returns_nothing()
    {
        var loaded = await _store.LoadAllAsync();
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task Saves_and_reloads_entries_across_all_boxes()
    {
        var location = new FieldGeoPoint(21.3069, -157.8583, 4.5);
        var draft = new CollectRecordEntry(NewRecord(
            "rec-draft",
            RecordStatus.Draft,
            location,
            new Dictionary<string, object?>
            {
                ["species"] = "Koa",
                ["count"] = 12,
                ["healthy"] = true,
            }));

        await _store.SaveAsync(draft);
        await _store.SaveAsync(Outbox("rec-outbox"));
        await _store.SaveAsync(Synced("rec-sent", "srv-99"));
        await _store.SaveAsync(Failed("rec-failed", "network down", attempts: 3));

        var loaded = (await _store.LoadAllAsync())
            .ToDictionary(e => e.Record.RecordId);

        Assert.Equal(4, loaded.Count);
        Assert.Equal(
            new[] { "rec-draft", "rec-failed", "rec-outbox", "rec-sent" },
            loaded.Keys.OrderBy(k => k).ToArray());

        // Values round-trip (JsonElement after reload).
        var draftBack = loaded["rec-draft"];
        Assert.Equal(RecordBox.Drafts, draftBack.Box);
        Assert.Equal(RecordSyncState.Local, draftBack.SyncState);
        var values = draftBack.Record.Values;
        Assert.Equal("Koa", AsString(values["species"]));
        Assert.Equal(12, AsInt(values["count"]));
        Assert.True(AsBool(values["healthy"]));

        // Location round-trips.
        var loc = draftBack.Record.Location;
        Assert.NotNull(loc);
        Assert.Equal(21.3069, loc!.Latitude, 6);
        Assert.Equal(-157.8583, loc.Longitude, 6);
        Assert.Equal(4.5, loc.AccuracyMeters!.Value, 6);

        // Outbox: finished, queued, not yet sent.
        var outboxBack = loaded["rec-outbox"];
        Assert.Equal(RecordBox.Outbox, outboxBack.Box);
        Assert.Equal(RecordSyncState.Pending, outboxBack.SyncState);
        Assert.Null(outboxBack.RemoteId);

        // Sent: synced with remote id and timestamp.
        var sentBack = loaded["rec-sent"];
        Assert.Equal(RecordBox.Sent, sentBack.Box);
        Assert.Equal(RecordSyncState.Synced, sentBack.SyncState);
        Assert.Equal("srv-99", sentBack.RemoteId);
        Assert.Equal(0, sentBack.FailedAttempts);
        Assert.NotNull(sentBack.LastSyncedUtc);
        Assert.Equal(
            new DateTimeOffset(2026, 6, 1, 8, 30, 0, TimeSpan.Zero),
            sentBack.LastSyncedUtc!.Value);

        // Failed: stays in outbox, error + retry count preserved.
        var failedBack = loaded["rec-failed"];
        Assert.Equal(RecordBox.Outbox, failedBack.Box);
        Assert.Equal(RecordSyncState.Failed, failedBack.SyncState);
        Assert.Equal("network down", failedBack.LastError);
        Assert.Equal(3, failedBack.FailedAttempts);
    }

    [Fact]
    public async Task Conflicted_record_reloads_into_the_conflicts_box()
    {
        var entry = new CollectRecordEntry(NewRecord("rec-conflict", RecordStatus.Submitted));
        entry.MarkPending();
        entry.MarkConflicted(BuildConflict());
        await _store.SaveAsync(entry);

        var back = (await _store.LoadAllAsync()).Single();

        // The transport state survives a restart so the record stays out of the
        // Outbox; the field-level conflict body is recomputed by the next pull.
        Assert.Equal(RecordSyncState.Conflicted, back.SyncState);
        Assert.Equal(RecordBox.Conflicts, back.Box);
        Assert.False(back.IsPendingUpload);
    }

    private static Honua.Collect.Core.Sync.RecordConflict BuildConflict()
    {
        var form = new Honua.Sdk.Field.Forms.FormDefinition
        {
            FormId = "tree-survey",
            Name = "tree-survey",
            Sections = [new Honua.Sdk.Field.Forms.FormSection { SectionId = "s", Label = "s", Fields =
            [
                new Honua.Sdk.Field.Forms.FormField { FieldId = "species", Label = "Species", Type = Honua.Sdk.Field.Forms.FormFieldType.Text },
            ] }],
        };
        var local = NewRecord("rec-conflict", RecordStatus.Submitted);
        local.Values["species"] = "Koa";
        var server = NewRecord("rec-conflict", RecordStatus.Submitted);
        server.Values["species"] = "Ohia";
        return Honua.Collect.Core.Sync.RecordConflictDetector.Detect(form, local, server);
    }

    [Fact]
    public async Task Record_without_location_reloads_with_null_location()
    {
        await _store.SaveAsync(Outbox("rec-no-loc"));

        var loaded = Assert.Single(await _store.LoadAllAsync());
        Assert.Null(loaded.Record.Location);
    }

    [Fact]
    public async Task Saving_same_record_id_updates_in_place_without_duplicates()
    {
        var entry = new CollectRecordEntry(NewRecord("rec-1", RecordStatus.Submitted));
        entry.MarkPending();
        await _store.SaveAsync(entry);

        // Transition the same record to synced and persist again.
        entry.MarkSynced("srv-1");
        await _store.SaveAsync(entry);

        var loaded = await _store.LoadAllAsync();
        var single = Assert.Single(loaded);
        Assert.Equal("rec-1", single.Record.RecordId);
        Assert.Equal(RecordSyncState.Synced, single.SyncState);
        Assert.Equal("srv-1", single.RemoteId);
    }

    [Fact]
    public async Task Delete_removes_only_the_targeted_record()
    {
        await _store.SaveAsync(Outbox("keep"));
        await _store.SaveAsync(Outbox("remove"));

        await _store.DeleteAsync("remove");

        var loaded = await _store.LoadAllAsync();
        var single = Assert.Single(loaded);
        Assert.Equal("keep", single.Record.RecordId);
    }

    [Fact]
    public async Task Delete_of_missing_record_is_a_no_op()
    {
        await _store.SaveAsync(Outbox("present"));

        await _store.DeleteAsync("ghost");

        Assert.Single(await _store.LoadAllAsync());
    }

    [Fact]
    public async Task Store_accepts_a_bare_file_path_connection()
    {
        var pathOnlyStore = new SqliteRecordStore(_dbPath);
        await pathOnlyStore.SaveAsync(Outbox("rec-path"));

        var loaded = Assert.Single(await pathOnlyStore.LoadAllAsync());
        Assert.Equal("rec-path", loaded.Record.RecordId);
    }

    private static string? AsString(object? value)
        => value is JsonElement e ? e.GetString() : value?.ToString();

    private static int AsInt(object? value)
        => value is JsonElement e ? e.GetInt32() : Convert.ToInt32(value);

    private static bool AsBool(object? value)
        => value is JsonElement e ? e.GetBoolean() : Convert.ToBoolean(value);
}
