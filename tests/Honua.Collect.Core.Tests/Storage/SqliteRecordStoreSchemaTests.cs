using System.Text.Json;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Sdk.Field.Records;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Tests.Storage;

/// <summary>
/// Schema / persistence-format guard. These tests pin the on-disk shape of
/// <see cref="SqliteRecordStore"/>: a record with every value shape (nulls,
/// numbers, booleans, nested repeat groups, media descriptors, location) is
/// written, the store is reopened as a fresh instance over the SAME file, and
/// everything is asserted to round-trip. A future schema change that drops a
/// column or alters serialization will break here rather than silently in the
/// field.
/// </summary>
public sealed class SqliteRecordStoreSchemaTests : IDisposable
{
    private readonly string _dbPath;

    public SqliteRecordStoreSchemaTests()
    {
        _dbPath = Path.GetTempFileName();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static Dictionary<string, object?> AllShapeValues() => new()
    {
        ["text"] = "Banyan",
        ["number_int"] = 42,
        ["number_double"] = 3.14159,
        ["flag_true"] = true,
        ["flag_false"] = false,
        ["empty"] = null,
        // Nested repeat group: a list of objects, each a captured sub-record.
        ["samples"] = new List<object?>
        {
            new Dictionary<string, object?> { ["depth"] = 1.5, ["label"] = "topsoil" },
            new Dictionary<string, object?> { ["depth"] = 4.0, ["label"] = "clay", ["wet"] = true },
        },
        // Media descriptor stored inline as a nested object.
        ["photo"] = new Dictionary<string, object?>
        {
            ["path"] = "/media/img-1.jpg",
            ["mime"] = "image/jpeg",
            ["bytes"] = 20480,
        },
    };

    [Fact]
    public async Task Record_with_all_field_shapes_round_trips_after_reopening_same_file()
    {
        var location = new FieldGeoPoint(21.3069, -157.8583, 4.5);
        var record = new FieldRecord
        {
            RecordId = "rec-shapes",
            FormId = "tree-survey",
            Status = RecordStatus.Submitted,
            Location = location,
        };
        foreach (var (key, value) in AllShapeValues())
        {
            record.Values[key] = value;
        }

        var entry = new CollectRecordEntry(record);
        entry.MarkPending();
        entry.MarkSynced("srv-77", new DateTimeOffset(2026, 6, 1, 8, 30, 0, TimeSpan.Zero));

        // Write with one store instance...
        var writer = new SqliteRecordStore($"Data Source={_dbPath}");
        await writer.SaveAsync(entry);

        // ...then read with a brand-new instance over the SAME file (guards that
        // nothing relies on in-memory state and the schema is self-describing).
        var reader = new SqliteRecordStore($"Data Source={_dbPath}");
        var loaded = Assert.Single(await reader.LoadAllAsync());

        Assert.Equal("rec-shapes", loaded.Record.RecordId);
        Assert.Equal("tree-survey", loaded.Record.FormId);
        Assert.Equal(RecordSyncState.Synced, loaded.SyncState);
        Assert.Equal("srv-77", loaded.RemoteId);
        Assert.Equal(
            new DateTimeOffset(2026, 6, 1, 8, 30, 0, TimeSpan.Zero),
            loaded.LastSyncedUtc!.Value);

        var loc = loaded.Record.Location;
        Assert.NotNull(loc);
        Assert.Equal(21.3069, loc!.Latitude, 6);
        Assert.Equal(-157.8583, loc.Longitude, 6);
        Assert.Equal(4.5, loc.AccuracyMeters!.Value, 6);

        var v = loaded.Record.Values;
        Assert.Equal("Banyan", JsonString(v["text"]));
        Assert.Equal(42, JsonInt(v["number_int"]));
        Assert.Equal(3.14159, JsonDouble(v["number_double"]), 5);
        Assert.True(JsonBool(v["flag_true"]));
        Assert.False(JsonBool(v["flag_false"]));
        Assert.True(IsJsonNull(v["empty"]));

        // Nested repeat group survives as an array of objects.
        var samples = (JsonElement)v["samples"]!;
        Assert.Equal(JsonValueKind.Array, samples.ValueKind);
        Assert.Equal(2, samples.GetArrayLength());
        Assert.Equal(1.5, samples[0].GetProperty("depth").GetDouble(), 5);
        Assert.Equal("topsoil", samples[0].GetProperty("label").GetString());
        Assert.True(samples[1].GetProperty("wet").GetBoolean());

        // Media descriptor survives as a nested object.
        var photo = (JsonElement)v["photo"]!;
        Assert.Equal(JsonValueKind.Object, photo.ValueKind);
        Assert.Equal("/media/img-1.jpg", photo.GetProperty("path").GetString());
        Assert.Equal("image/jpeg", photo.GetProperty("mime").GetString());
        Assert.Equal(20480, photo.GetProperty("bytes").GetInt32());
    }

    [Fact]
    public async Task Reopened_store_sees_updates_and_deletes_persisted_by_another_instance()
    {
        var first = new SqliteRecordStore($"Data Source={_dbPath}");
        await first.SaveAsync(Outbox("keep"));
        await first.SaveAsync(Outbox("remove"));

        // A separate instance updates one record and deletes the other.
        var second = new SqliteRecordStore($"Data Source={_dbPath}");
        var updated = Outbox("keep");
        updated.MarkSynced("remote-keep");
        await second.SaveAsync(updated);
        await second.DeleteAsync("remove");

        // A third instance must observe the committed state.
        var third = new SqliteRecordStore($"Data Source={_dbPath}");
        var loaded = Assert.Single(await third.LoadAllAsync());
        Assert.Equal("keep", loaded.Record.RecordId);
        Assert.Equal(RecordSyncState.Synced, loaded.SyncState);
        Assert.Equal("remote-keep", loaded.RemoteId);
    }

    [Fact]
    public async Task Expected_columns_exist_on_the_persisted_table()
    {
        // Force schema creation by writing through the store, then inspect the
        // table definition directly. If a column is renamed/dropped the store's
        // SELECT would break, but this pins it explicitly for clarity.
        var store = new SqliteRecordStore($"Data Source={_dbPath}");
        await store.SaveAsync(Outbox("rec-cols"));

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('collect_records');";

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        foreach (var expected in new[]
        {
            "record_id", "form_id", "status", "assigned_user_id", "lat", "lon",
            "accuracy", "created_utc", "submitted_utc", "completed_utc",
            "values_json", "sync_state", "remote_id", "last_error",
            "failed_attempts", "last_synced_utc",
        })
        {
            Assert.Contains(expected, columns);
        }
    }

    private static CollectRecordEntry Outbox(string id)
    {
        var entry = new CollectRecordEntry(new FieldRecord
        {
            RecordId = id,
            FormId = "tree-survey",
            Status = RecordStatus.Submitted,
        });
        entry.MarkPending();
        return entry;
    }

    private static string? JsonString(object? v) => ((JsonElement)v!).GetString();

    private static int JsonInt(object? v) => ((JsonElement)v!).GetInt32();

    private static double JsonDouble(object? v) => ((JsonElement)v!).GetDouble();

    private static bool JsonBool(object? v) => ((JsonElement)v!).GetBoolean();

    private static bool IsJsonNull(object? v) => v is null || ((JsonElement)v).ValueKind == JsonValueKind.Null;
}
