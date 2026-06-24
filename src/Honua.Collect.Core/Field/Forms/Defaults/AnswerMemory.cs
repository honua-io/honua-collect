namespace Honua.Collect.Core.Field.Forms.Defaults;

/// <summary>
/// A named, reusable set of answers for a form — a "favorite" the field worker
/// can apply to pre-fill a new record (BACKLOG F5). The canonical example is
/// Fulcrum/Survey123 "favorites": save a typical answer set ("Routine pole
/// inspection", "Standard hydrant") and start future records from it.
/// </summary>
/// <param name="Name">Human-readable favorite name, unique per form.</param>
/// <param name="Values">The field values this favorite supplies, keyed by field id.</param>
public sealed record FavoriteAnswerSet(string Name, IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>Human-readable favorite name (unique per form).</summary>
    public string Name { get; } = !string.IsNullOrWhiteSpace(Name)
        ? Name
        : throw new ArgumentException("Favorite name is required.", nameof(Name));

    /// <summary>The field values this favorite supplies, keyed by field id.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; } =
        Values ?? throw new ArgumentNullException(nameof(Values));
}

/// <summary>
/// Durable, device-local memory of what a field worker last answered on a form,
/// plus their saved favorite answer sets — the persistence behind
/// "default-from-previous" and "favorites" answer reuse (BACKLOG F5). Backed by
/// the same on-device SQLite database as captured records; it is deliberately a
/// separate seam from <see cref="Storage.IRecordStore"/> because remembered
/// answers outlive any single record and must not be mixed into the outbox.
/// </summary>
public interface IAnswerMemoryStore
{
    /// <summary>
    /// Records the values a user just submitted for a form as their "last
    /// answers", overwriting any prior remembered answers for that form. Pass only
    /// the fields that should be remembered (the caller filters out volatile,
    /// per-record fields such as identifiers, media, and geometry).
    /// </summary>
    /// <param name="formId">The form the answers belong to.</param>
    /// <param name="values">The field values to remember, keyed by field id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RememberLastAsync(string formId, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default);

    /// <summary>Gets the values the user last submitted for a form, or an empty map.</summary>
    /// <param name="formId">The form to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The last-submitted values, keyed by field id.</returns>
    Task<IReadOnlyDictionary<string, object?>> GetLastAsync(string formId, CancellationToken ct = default);

    /// <summary>Saves (inserts or replaces) a named favorite answer set for a form.</summary>
    /// <param name="formId">The form the favorite belongs to.</param>
    /// <param name="favorite">The favorite to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveFavoriteAsync(string formId, FavoriteAnswerSet favorite, CancellationToken ct = default);

    /// <summary>Gets a named favorite for a form, or <see langword="null"/> when absent.</summary>
    /// <param name="formId">The form to read.</param>
    /// <param name="name">The favorite name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The favorite, or <see langword="null"/>.</returns>
    Task<FavoriteAnswerSet?> GetFavoriteAsync(string formId, string name, CancellationToken ct = default);

    /// <summary>Lists every favorite saved for a form, in name order.</summary>
    /// <param name="formId">The form to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The form's favorites.</returns>
    Task<IReadOnlyList<FavoriteAnswerSet>> ListFavoritesAsync(string formId, CancellationToken ct = default);
}
