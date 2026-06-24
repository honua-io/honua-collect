using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Field.Forms.Defaults;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Forms.Defaults;

public class FormDefaultsServiceTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "inspection",
        Name = "Inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "main",
                Label = "Main",
                Fields =
                [
                    new FormField { FieldId = "inspector", Label = "Inspector", Type = FormFieldType.Text },
                    new FormField { FieldId = "crew", Label = "Crew", Type = FormFieldType.Text },
                    new FormField { FieldId = "site", Label = "Site", Type = FormFieldType.Text },
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                    new FormField { FieldId = "stamp", Label = "Stamp", Type = FormFieldType.Calculated, CalculatedExpression = "concat('x')" },
                ],
            },
        ],
    };

    [Fact]
    public async Task Default_is_applied_from_the_last_submission()
    {
        var store = new FakeAnswerMemoryStore();
        var service = new FormDefaultsService(store);

        // User submits a record; we remember their answers.
        var submitted = new FieldRecord { RecordId = "r1", FormId = "inspection" };
        submitted.Values["inspector"] = "Ada";
        submitted.Values["crew"] = "Blue";
        await service.RememberAsync(Form(), submitted);

        // A new record defaults from the remembered answers.
        var session = await service.StartNewRecordAsync(Form(), "r2");

        Assert.Equal("Ada", session.GetValue("inspector"));
        Assert.Equal("Blue", session.GetValue("crew"));
    }

    [Fact]
    public async Task Explicit_default_takes_precedence_over_last_answer()
    {
        var store = new FakeAnswerMemoryStore();
        var service = new FormDefaultsService(store);

        var submitted = new FieldRecord { RecordId = "r1", FormId = "inspection" };
        submitted.Values["inspector"] = "Ada";
        await service.RememberAsync(Form(), submitted);

        var explicitDefaults = new Dictionary<string, object?> { ["inspector"] = "Grace" };
        var defaults = await service.ResolveDefaultsAsync(Form(), explicitDefaults);

        // explicit default > last answer
        Assert.Equal("Grace", defaults["inspector"]);
    }

    [Fact]
    public async Task Favorite_set_is_applied_and_outranks_last_answer()
    {
        var store = new FakeAnswerMemoryStore();
        var service = new FormDefaultsService(store);

        // Last answer says crew=Blue, site=North.
        var submitted = new FieldRecord { RecordId = "r1", FormId = "inspection" };
        submitted.Values["crew"] = "Blue";
        submitted.Values["site"] = "North";
        await service.RememberAsync(Form(), submitted);

        // Favorite "Routine" says crew=Gold (and nothing about site).
        var fav = new FieldRecord { RecordId = "fav", FormId = "inspection" };
        fav.Values["crew"] = "Gold";
        await service.SaveAsFavoriteAsync(Form(), fav, "Routine");

        var defaults = await service.ResolveDefaultsAsync(Form(), favoriteName: "Routine");

        Assert.Equal("Gold", defaults["crew"]);   // favorite outranks last answer
        Assert.Equal("North", defaults["site"]);  // favorite silent -> falls back to last answer
    }

    [Fact]
    public async Task Calculated_and_media_fields_are_never_remembered_or_defaulted()
    {
        var store = new FakeAnswerMemoryStore();
        var service = new FormDefaultsService(store);

        var submitted = new FieldRecord { RecordId = "r1", FormId = "inspection" };
        submitted.Values["inspector"] = "Ada";
        submitted.Values["photo"] = "/tmp/p.jpg";   // media
        submitted.Values["stamp"] = "stale";        // calculated
        await service.RememberAsync(Form(), submitted);

        var remembered = await store.GetLastAsync("inspection");
        Assert.True(remembered.ContainsKey("inspector"));
        Assert.False(remembered.ContainsKey("photo"));
        Assert.False(remembered.ContainsKey("stamp"));

        // And the calculated field is recomputed in the new session, not seeded.
        var session = await service.StartNewRecordAsync(Form(), "r2");
        Assert.Null(session.GetValue("photo"));
        Assert.Equal("x", session.GetValue("stamp"));
    }

    [Fact]
    public async Task Resolution_precedence_is_explicit_then_favorite_then_last()
    {
        var store = new FakeAnswerMemoryStore();
        var service = new FormDefaultsService(store);

        var last = new FieldRecord { RecordId = "r1", FormId = "inspection" };
        last.Values["inspector"] = "Last";
        last.Values["crew"] = "Last";
        last.Values["site"] = "Last";
        await service.RememberAsync(Form(), last);

        var fav = new FieldRecord { RecordId = "fav", FormId = "inspection" };
        fav.Values["crew"] = "Fav";
        fav.Values["site"] = "Fav";
        await service.SaveAsFavoriteAsync(Form(), fav, "F");

        var explicitDefaults = new Dictionary<string, object?> { ["site"] = "Explicit" };

        var resolved = await service.ResolveDefaultsAsync(Form(), explicitDefaults, "F");

        Assert.Equal("Last", resolved["inspector"]);  // only last has it
        Assert.Equal("Fav", resolved["crew"]);        // favorite > last
        Assert.Equal("Explicit", resolved["site"]);   // explicit > favorite > last
    }

    private sealed class FakeAnswerMemoryStore : IAnswerMemoryStore
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> _last = new();
        private readonly Dictionary<(string, string), FavoriteAnswerSet> _favorites = new();

        public Task RememberLastAsync(string formId, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
        {
            _last[formId] = new Dictionary<string, object?>(values);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, object?>> GetLastAsync(string formId, CancellationToken ct = default)
            => Task.FromResult(_last.TryGetValue(formId, out var v) ? v : new Dictionary<string, object?>());

        public Task SaveFavoriteAsync(string formId, FavoriteAnswerSet favorite, CancellationToken ct = default)
        {
            _favorites[(formId, favorite.Name)] = favorite;
            return Task.CompletedTask;
        }

        public Task<FavoriteAnswerSet?> GetFavoriteAsync(string formId, string name, CancellationToken ct = default)
            => Task.FromResult(_favorites.TryGetValue((formId, name), out var f) ? f : null);

        public Task<IReadOnlyList<FavoriteAnswerSet>> ListFavoritesAsync(string formId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FavoriteAnswerSet>>(
                _favorites.Where(kv => kv.Key.Item1 == formId).Select(kv => kv.Value).OrderBy(f => f.Name).ToList());
    }
}
