using Honua.Collect.Core.Field.Forms.Localization;

namespace Honua.Collect.Core.Tests.Field.Forms;

public sealed class SqliteLocaleStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteLocaleStore _store;

    public SqliteLocaleStoreTests()
    {
        _dbPath = Path.GetTempFileName();
        _store = new SqliteLocaleStore($"Data Source={_dbPath}");
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
    public async Task Unset_form_has_no_active_language()
        => Assert.Null(await _store.GetActiveLanguageAsync("inspection"));

    [Fact]
    public async Task Persists_and_reads_the_active_language()
    {
        await _store.SetActiveLanguageAsync("inspection", "es");
        Assert.Equal("es", await _store.GetActiveLanguageAsync("inspection"));
    }

    [Fact]
    public async Task Re_setting_replaces_the_prior_language()
    {
        await _store.SetActiveLanguageAsync("inspection", "es");
        await _store.SetActiveLanguageAsync("inspection", "fr-CA");
        Assert.Equal("fr-CA", await _store.GetActiveLanguageAsync("inspection"));
    }

    [Fact]
    public async Task Languages_are_isolated_per_form()
    {
        await _store.SetActiveLanguageAsync("a", "es");
        await _store.SetActiveLanguageAsync("b", "de");

        Assert.Equal("es", await _store.GetActiveLanguageAsync("a"));
        Assert.Equal("de", await _store.GetActiveLanguageAsync("b"));
    }

    [Fact]
    public async Task Survives_a_new_store_over_the_same_file()
    {
        await _store.SetActiveLanguageAsync("inspection", "es");

        var reopened = new SqliteLocaleStore($"Data Source={_dbPath}");
        Assert.Equal("es", await reopened.GetActiveLanguageAsync("inspection"));
    }
}
