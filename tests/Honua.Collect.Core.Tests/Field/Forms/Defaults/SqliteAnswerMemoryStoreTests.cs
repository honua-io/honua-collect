using Honua.Collect.Core.Field.Forms.Defaults;

namespace Honua.Collect.Core.Tests.Field.Forms.Defaults;

public class SqliteAnswerMemoryStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"collect-answers-{Guid.NewGuid():n}.db");

    [Fact]
    public async Task Last_answers_round_trip_and_overwrite()
    {
        var store = new SqliteAnswerMemoryStore(_dbPath);

        await store.RememberLastAsync("f", new Dictionary<string, object?> { ["a"] = "1", ["b"] = "2" });

        // A fresh instance over the same file reads the persisted answers.
        var reopened = new SqliteAnswerMemoryStore(_dbPath);
        var first = await reopened.GetLastAsync("f");
        Assert.Equal("1", first["a"]?.ToString());
        Assert.Equal("2", first["b"]?.ToString());

        // Remembering again overwrites (one row per form).
        await store.RememberLastAsync("f", new Dictionary<string, object?> { ["a"] = "9" });
        var second = await store.GetLastAsync("f");
        Assert.Equal("9", second["a"]?.ToString());
        Assert.False(second.ContainsKey("b"));
    }

    [Fact]
    public async Task Missing_form_returns_empty_last_answers()
    {
        var store = new SqliteAnswerMemoryStore(_dbPath);
        Assert.Empty(await store.GetLastAsync("nope"));
    }

    [Fact]
    public async Task Favorites_save_get_and_list_per_form()
    {
        var store = new SqliteAnswerMemoryStore(_dbPath);

        await store.SaveFavoriteAsync("f", new FavoriteAnswerSet("Routine", new Dictionary<string, object?> { ["crew"] = "Blue" }));
        await store.SaveFavoriteAsync("f", new FavoriteAnswerSet("Storm", new Dictionary<string, object?> { ["crew"] = "Red" }));

        var routine = await store.GetFavoriteAsync("f", "Routine");
        Assert.NotNull(routine);
        Assert.Equal("Blue", routine!.Values["crew"]?.ToString());

        var all = await store.ListFavoritesAsync("f");
        Assert.Equal(["Routine", "Storm"], all.Select(x => x.Name));

        // Re-saving the same name replaces it.
        await store.SaveFavoriteAsync("f", new FavoriteAnswerSet("Routine", new Dictionary<string, object?> { ["crew"] = "Gold" }));
        var updated = await store.GetFavoriteAsync("f", "Routine");
        Assert.Equal("Gold", updated!.Values["crew"]?.ToString());
        Assert.Equal(2, (await store.ListFavoritesAsync("f")).Count);
    }

    [Fact]
    public async Task Missing_favorite_returns_null()
    {
        var store = new SqliteAnswerMemoryStore(_dbPath);
        Assert.Null(await store.GetFavoriteAsync("f", "ghost"));
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
