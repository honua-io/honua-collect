using Honua.Collect.Core.Assignments;
using Honua.Sdk.Field.Records;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Tests.Assignments;

public sealed class SqliteAssignmentStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteAssignmentStore _store;

    public SqliteAssignmentStoreTests()
    {
        _dbPath = Path.GetTempFileName();
        _store = new SqliteAssignmentStore($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static FieldAssignment New(string id = "a1", string user = "op-1") => new()
    {
        AssignmentId = id,
        FormId = "hydrant-survey",
        AssignedToUserId = user,
        Title = "Inspect hydrant",
        Instructions = "Check for leaks.",
        Location = new FieldGeoPoint(45.5, -122.6, 4.0),
        DueAtUtc = new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.Zero),
        Priority = AssignmentPriority.High,
        CreatedAtUtc = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Save_then_load_round_trips_all_fields()
    {
        var assignment = New();
        await _store.SaveAsync(assignment);

        var loaded = (await _store.LoadAllAsync()).Single();

        Assert.Equal("a1", loaded.AssignmentId);
        Assert.Equal("hydrant-survey", loaded.FormId);
        Assert.Equal("op-1", loaded.AssignedToUserId);
        Assert.Equal("Inspect hydrant", loaded.Title);
        Assert.Equal("Check for leaks.", loaded.Instructions);
        Assert.NotNull(loaded.Location);
        Assert.Equal(45.5, loaded.Location!.Latitude);
        Assert.Equal(-122.6, loaded.Location.Longitude);
        Assert.Equal(4.0, loaded.Location.AccuracyMeters);
        Assert.Equal(assignment.DueAtUtc, loaded.DueAtUtc);
        Assert.Equal(AssignmentPriority.High, loaded.Priority);
        Assert.Equal(assignment.CreatedAtUtc, loaded.CreatedAtUtc);
        Assert.Equal(AssignmentStatus.Assigned, loaded.Status);
    }

    [Fact]
    public async Task Save_round_trips_advanced_lifecycle_state()
    {
        var assignment = New();
        assignment.Start("rec-7");
        assignment.Complete(new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(assignment);

        var loaded = (await _store.LoadAllAsync()).Single();

        Assert.Equal(AssignmentStatus.Completed, loaded.Status);
        Assert.Equal("rec-7", loaded.RecordId);
        Assert.NotNull(loaded.AcceptedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.Zero), loaded.CompletedAtUtc);
        Assert.False(loaded.IsOpen);
    }

    [Fact]
    public async Task Save_upserts_on_conflicting_id()
    {
        var assignment = New();
        await _store.SaveAsync(assignment);
        assignment.Start("rec-9");
        await _store.SaveAsync(assignment);

        var all = await _store.LoadAllAsync();
        Assert.Single(all);
        Assert.Equal(AssignmentStatus.InProgress, all[0].Status);
    }

    [Fact]
    public async Task Load_for_user_filters_by_operator()
    {
        await _store.SaveAsync(New("a1", "op-1"));
        await _store.SaveAsync(New("a2", "op-1"));
        await _store.SaveAsync(New("a3", "op-2"));

        var mine = await _store.LoadForUserAsync("op-1");
        Assert.Equal(["a1", "a2"], mine.Select(a => a.AssignmentId).OrderBy(x => x));

        var theirs = await _store.LoadForUserAsync("op-2");
        Assert.Equal(["a3"], theirs.Select(a => a.AssignmentId));
    }

    [Fact]
    public async Task Delete_removes_the_assignment()
    {
        await _store.SaveAsync(New("a1"));
        await _store.DeleteAsync("a1");

        Assert.Empty(await _store.LoadAllAsync());
    }

    [Fact]
    public async Task Location_is_null_when_not_set()
    {
        await _store.SaveAsync(new FieldAssignment
        {
            AssignmentId = "a1", FormId = "f", AssignedToUserId = "op-1", Title = "No location",
        });

        var loaded = (await _store.LoadAllAsync()).Single();
        Assert.Null(loaded.Location);
    }
}
