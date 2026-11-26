// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Storage;

namespace Honua.Field.Core.Tests.Storage;

public class DeviceDatabaseKeyTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    [Theory]
    [InlineData("records.db")]
    [InlineData("audit.db")]
    [InlineData("records.db-wal")]
    [InlineData("records.db-shm")]
    [InlineData("audit.db-journal")]
    public async Task Missing_key_preserves_existing_files_and_can_retry_after_unlock(string file)
    {
        string? key = null;
        var path = Path.Combine(_directory, file);
        byte[] original = [42, 19, 73];
        await File.WriteAllBytesAsync(path, original);
        var deviceKey = new DeviceDatabaseKey(() => Task.FromResult(key),
            _ => throw new InvalidOperationException("Unexpected key write"),
            Path.Combine(_directory, "records.db"), Path.Combine(_directory, "audit.db"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(deviceKey.GetOrCreateAsync);
        Assert.Contains("device key", failure.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));

        key = "restored-key";
        Assert.Equal(key, await deviceKey.GetOrCreateAsync());
    }

    [Fact]
    public async Task Concurrent_first_openers_share_one_persisted_key()
    {
        string? key = null;
        var writes = 0;
        var deviceKey = new DeviceDatabaseKey(async () =>
        {
            await Task.Yield();
            return key;
        }, value =>
        {
            writes++;
            key = value;
            return Task.CompletedTask;
        }, Path.Combine(_directory, "records.db"));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => deviceKey.GetOrCreateAsync()));
        Assert.Equal(1, writes);
        Assert.All(results, value => Assert.Equal(key, value));
        Assert.Equal(32, Convert.FromBase64String(results[0]).Length);
    }

    [Fact]
    public async Task Read_error_from_secure_storage_never_creates_a_key()
    {
        var deviceKey = new DeviceDatabaseKey(() => throw new IOException("Device locked"),
            _ => throw new InvalidOperationException("Unexpected key write"), Path.Combine(_directory, "records.db"));
        await Assert.ThrowsAsync<IOException>(deviceKey.GetOrCreateAsync);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
