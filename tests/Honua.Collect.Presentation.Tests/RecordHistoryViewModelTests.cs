// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.History;
using Honua.Collect.Core.Records;
using Honua.Collect.Core.Storage;
using Honua.Collect.Presentation.History;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Tests;

public sealed class RecordHistoryViewModelTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 14, 8, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath;
    private readonly SqliteRecordHistoryStore _store;
    private readonly SqliteRecordStore _recordStore;

    public RecordHistoryViewModelTests()
    {
        _dbPath = Path.GetTempFileName();
        _store = new SqliteRecordHistoryStore($"Data Source={_dbPath}");
        _recordStore = new SqliteRecordStore($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static RecordEdit Edit(long sequence, bool afterSync, params (string Field, object? Old, object? New)[] changes)
        => new(
            sequence,
            T0.AddMinutes(sequence),
            "soleil",
            changes.Select(c => new FieldChange(c.Field, c.Old, c.New)).ToList(),
            afterSync);

    [Fact]
    public async Task Loads_versions_newest_first_with_one_based_numbering()
    {
        await _store.AppendAsync("r1", Edit(0, afterSync: false, ("status", "open", "closed")));
        await _store.AppendAsync("r1", Edit(1, afterSync: true, ("count", 1, 2)));

        var vm = new RecordHistoryViewModel(_store, "r1");
        await vm.LoadAsync();

        Assert.Equal("r1", vm.RecordId);
        Assert.True(vm.HasHistory);
        Assert.False(vm.CanRevert);
        Assert.Equal(2, vm.Versions.Count);
        // Newest first: sequence 1 -> version 2.
        Assert.Equal(1, vm.Versions[0].Sequence);
        Assert.Equal(2, vm.Versions[0].Version);
        Assert.Equal("Revert to v2", vm.Versions[0].RevertLabel);
        Assert.True(vm.Versions[0].AfterSync);
        Assert.Equal(1, vm.Versions[1].Version);

        var topChange = Assert.Single(vm.Versions[0].Changes);
        Assert.Equal("count", topChange.FieldId);
        Assert.Equal("1", topChange.BeforeText);
        Assert.Equal("2", topChange.AfterText);
        Assert.Equal("count: 1 → 2", topChange.Summary);
        Assert.Contains("v2", vm.Versions[0].Header);
        Assert.Contains("post-sync", vm.Versions[0].Header);

        // Read-only view-model returns null for revert actions.
        Assert.Null(await vm.UndoLastChangeAsync());
        Assert.Null(await vm.RevertToVersionAsync(vm.Versions[0]));
        Assert.Null(await vm.RevertToOriginalAsync());
    }

    [Fact]
    public async Task Empty_history_reports_no_versions()
    {
        var vm = new RecordHistoryViewModel(_store, "ghost");
        await vm.LoadAsync();

        Assert.False(vm.HasHistory);
        Assert.Empty(vm.Versions);
    }

    [Fact]
    public async Task Undo_and_revert_to_version_and_original_restore_values_and_reload_history()
    {
        var record = new FieldRecord { RecordId = "r-revert", FormId = "site", Status = RecordStatus.Submitted };
        record.Values["status"] = "verified";
        record.Values["count"] = 2L;
        var entry = new CollectRecordEntry(record);
        entry.MarkPending();
        entry.MarkSynced("42");
        await _recordStore.SaveAsync(entry);

        await _store.AppendAsync("r-revert", Edit(0, afterSync: false, ("status", "open", "closed"), ("count", 1L, 1L)));
        await _store.AppendAsync("r-revert", Edit(1, afterSync: true, ("status", "closed", "verified"), ("count", 1L, 2L)));

        var service = new RecordRevertService(_recordStore, _store);
        var vm = new RecordHistoryViewModel(_store, entry, service, "auditor");

        RecordRevertResult? lastEvent = null;
        vm.RecordReverted += (_, r) => lastEvent = r;

        await vm.LoadAsync();
        Assert.True(vm.CanRevert);
        Assert.True(vm.UndoLastChangeCommand.CanExecute(null));
        Assert.True(vm.RevertToOriginalCommand.CanExecute(null));
        Assert.True(vm.RevertToVersionCommand.CanExecute(vm.Versions[1]));
        Assert.False(vm.RevertToVersionCommand.CanExecute(null));

        // Undo last change restores v1 ("closed", 1L) and appends v3.
        var undo = await vm.UndoLastChangeAsync();
        Assert.NotNull(undo);
        Assert.NotNull(undo!.RevertEdit);
        Assert.Same(undo, lastEvent);
        Assert.Equal("closed", entry.Record.Values["status"]);
        Assert.Equal(1L, entry.Record.Values["count"]);
        Assert.Equal(RecordSyncState.PendingUpdate, entry.SyncState);
        Assert.Equal(3, vm.Versions.Count);
        Assert.Contains("Undid last change", vm.StatusMessage);

        // Reverting to v2 restores ("verified", 2L) and appends v4.
        var v2 = vm.Versions.Single(v => v.Version == 2);
        var reverted = await vm.RevertToVersionAsync(v2);
        Assert.NotNull(reverted);
        Assert.Equal("verified", entry.Record.Values["status"]);
        Assert.Equal(2L, entry.Record.Values["count"]);
        Assert.Equal(4, vm.Versions.Count);
        Assert.Contains("Reverted to v2", vm.StatusMessage);

        // Reverting again to the same v2 is a no-op with clear status.
        var noop = await vm.RevertToVersionAsync(v2);
        Assert.NotNull(noop);
        Assert.Null(noop!.RevertEdit);
        Assert.Contains("already matches v2", vm.StatusMessage);

        // Null target returns null cleanly.
        Assert.Null(await vm.RevertToVersionAsync(null));

        // Revert to original restores ("open", 1L) and appends v5.
        var toOriginal = await vm.RevertToOriginalAsync();
        Assert.NotNull(toOriginal);
        Assert.NotNull(toOriginal!.RevertEdit);
        Assert.Equal("open", entry.Record.Values["status"]);
        Assert.Equal(1L, entry.Record.Values["count"]);
        Assert.Equal(5, vm.Versions.Count);
        Assert.Contains("Reverted to original", vm.StatusMessage);

        // Second revert to original is a no-op.
        var noopOriginal = await vm.RevertToOriginalAsync();
        Assert.NotNull(noopOriginal);
        Assert.Null(noopOriginal!.RevertEdit);
        Assert.Contains("already matches the original", vm.StatusMessage);
    }
}
