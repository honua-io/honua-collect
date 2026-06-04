using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Sdk.Field.Records;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Tests.Storage;

public class SqliteRecordStoreEncryptionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"collect-enc-{Guid.NewGuid():n}.db");

    private static FieldRecord Record(string id) => new()
    {
        RecordId = id,
        FormId = "f",
        Status = RecordStatus.Submitted,
        Values = { ["site_name"] = "Encrypted Site", ["count"] = 7L },
    };

    [Fact]
    public async Task Encrypted_database_round_trips_with_the_same_key()
    {
        const string key = "test-key-aaaa";
        var store = new SqliteRecordStore(_dbPath, key);
        await store.SaveAsync(new CollectRecordEntry(Record("r1")));

        // A fresh store instance opening the SAME encrypted file with the same key reads it.
        var reopened = new SqliteRecordStore(_dbPath, key);
        var all = await reopened.LoadAllAsync();

        Assert.Single(all);
        Assert.Equal("r1", all[0].Record.RecordId);
        Assert.Equal("Encrypted Site", all[0].Record.Values["site_name"]?.ToString());
    }

    [Fact]
    public async Task Encrypted_file_is_not_openable_with_the_wrong_key()
    {
        await new SqliteRecordStore(_dbPath, "correct-key").SaveAsync(new CollectRecordEntry(Record("r1")));

        var wrong = new SqliteRecordStore(_dbPath, "WRONG-key");
        await Assert.ThrowsAsync<SqliteException>(() => wrong.LoadAllAsync());
    }

    [Fact]
    public async Task Encrypted_file_is_not_openable_without_a_key()
    {
        await new SqliteRecordStore(_dbPath, "correct-key").SaveAsync(new CollectRecordEntry(Record("r1")));

        var noKey = new SqliteRecordStore(_dbPath);
        await Assert.ThrowsAsync<SqliteException>(() => noKey.LoadAllAsync());
    }

    [Fact]
    public async Task Encrypted_file_is_not_plaintext_on_disk()
    {
        await new SqliteRecordStore(_dbPath, "correct-key").SaveAsync(new CollectRecordEntry(Record("r1")));

        var bytes = await File.ReadAllBytesAsync(_dbPath);
        // A plaintext SQLite file starts with the "SQLite format 3\0" header; an
        // encrypted one does not, and the cleartext field value must not appear.
        var header = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(16, bytes.Length));
        Assert.DoesNotContain("SQLite format 3", header);
        Assert.DoesNotContain("Encrypted Site", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public async Task A_corrupt_database_file_fails_loudly_not_silently()
    {
        await File.WriteAllBytesAsync(_dbPath, [0xDE, 0xAD, 0xBE, 0xEF, .. new byte[2048]]);

        var store = new SqliteRecordStore(_dbPath);
        await Assert.ThrowsAsync<SqliteException>(() => store.LoadAllAsync());
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
