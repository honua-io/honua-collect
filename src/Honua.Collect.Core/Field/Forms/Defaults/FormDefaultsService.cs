using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Forms.Defaults;

/// <summary>
/// Orchestrates "default-from-previous" and "favorites" answer reuse (BACKLOG F5)
/// over an <see cref="IAnswerMemoryStore"/>. It resolves the values a new record
/// should start with — applying the precedence <em>explicit default &gt; favorite
/// / last answer &gt; empty</em> — and remembers a user's answers after they
/// submit so the next record can default from them.
/// </summary>
/// <remarks>
/// Calculated and media fields are never defaulted or remembered: calculated
/// values are recomputed from their expressions, and media attachments are
/// host-local and not reusable across records. The resolver returns a plain value
/// map; <see cref="FormSession.CreateForNewRecord"/> applies it (and itself
/// re-skips calculated/media fields defensively).
/// </remarks>
public sealed class FormDefaultsService
{
    private readonly IAnswerMemoryStore _store;

    /// <summary>Creates the service over an answer-memory store.</summary>
    /// <param name="store">The durable answer/favorites memory.</param>
    public FormDefaultsService(IAnswerMemoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Resolves the default values a brand-new record for <paramref name="form"/>
    /// should start with, merging three sources by precedence:
    /// <list type="number">
    ///   <item><paramref name="explicitDefaults"/> — caller/author-set defaults (win).</item>
    ///   <item>The named <paramref name="favoriteName"/> answer set, when supplied.</item>
    ///   <item>The user's last-submitted answers for the form.</item>
    /// </list>
    /// A higher-precedence source only fills fields a lower one left empty, so an
    /// explicit default is never overwritten by a favorite or last answer.
    /// </summary>
    /// <param name="form">The form a new record is being created for.</param>
    /// <param name="explicitDefaults">Author/caller defaults that take precedence, or <see langword="null"/>.</param>
    /// <param name="favoriteName">A saved favorite to seed from, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved per-field defaults, keyed by field id.</returns>
    public async Task<IReadOnlyDictionary<string, object?>> ResolveDefaultsAsync(
        FormDefinition form,
        IReadOnlyDictionary<string, object?>? explicitDefaults = null,
        string? favoriteName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(form);

        var seedable = SeedableFieldIds(form);
        var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // 1. Explicit defaults win — applied first; later sources never overwrite.
        Merge(resolved, explicitDefaults, seedable);

        // 2. Favorite answer set, when one was chosen.
        if (!string.IsNullOrWhiteSpace(favoriteName))
        {
            var favorite = await _store.GetFavoriteAsync(form.FormId, favoriteName, ct).ConfigureAwait(false);
            Merge(resolved, favorite?.Values, seedable);
        }

        // 3. Last-submitted answers (default-from-previous).
        var last = await _store.GetLastAsync(form.FormId, ct).ConfigureAwait(false);
        Merge(resolved, last, seedable);

        return resolved;
    }

    /// <summary>
    /// Convenience over <see cref="ResolveDefaultsAsync"/> and
    /// <see cref="FormSession.CreateForNewRecord"/>: resolves defaults and opens a
    /// new session already seeded with them.
    /// </summary>
    /// <param name="form">The form to capture.</param>
    /// <param name="recordId">Identifier for the new record.</param>
    /// <param name="explicitDefaults">Author/caller defaults that take precedence, or <see langword="null"/>.</param>
    /// <param name="favoriteName">A saved favorite to seed from, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A new draft session seeded with the resolved defaults.</returns>
    public async Task<FormSession> StartNewRecordAsync(
        FormDefinition form,
        string recordId,
        IReadOnlyDictionary<string, object?>? explicitDefaults = null,
        string? favoriteName = null,
        CancellationToken ct = default)
    {
        var defaults = await ResolveDefaultsAsync(form, explicitDefaults, favoriteName, ct).ConfigureAwait(false);
        return FormSession.CreateForNewRecord(form, recordId, seedDefaults: defaults);
    }

    /// <summary>
    /// Remembers the answers in a submitted record as the user's "last answers"
    /// for the form (per-field "remember last"), so the next new record defaults
    /// from them. Only seedable (non-calculated, non-media) fields with a value are
    /// remembered; volatile geometry/identifier fields are excluded by virtue of
    /// not being plain answer fields.
    /// </summary>
    /// <param name="form">The submitted record's form.</param>
    /// <param name="record">The record whose answers should be remembered.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RememberAsync(FormDefinition form, FieldRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(record);

        var seedable = SeedableFieldIds(form);
        var toRemember = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fieldId, value) in record.Values)
        {
            if (value is not null && seedable.Contains(fieldId))
            {
                toRemember[fieldId] = value;
            }
        }

        return _store.RememberLastAsync(form.FormId, toRemember, ct);
    }

    /// <summary>
    /// Saves the seedable answers of a record as a named favorite the user can
    /// re-apply to future records.
    /// </summary>
    /// <param name="form">The record's form.</param>
    /// <param name="record">The record to capture as a favorite.</param>
    /// <param name="name">The favorite name.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task SaveAsFavoriteAsync(FormDefinition form, FieldRecord record, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var seedable = SeedableFieldIds(form);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fieldId, value) in record.Values)
        {
            if (value is not null && seedable.Contains(fieldId))
            {
                values[fieldId] = value;
            }
        }

        return _store.SaveFavoriteAsync(form.FormId, new FavoriteAnswerSet(name, values), ct);
    }

    private static void Merge(
        Dictionary<string, object?> into,
        IReadOnlyDictionary<string, object?>? source,
        HashSet<string> seedable)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (fieldId, value) in source)
        {
            // Only fill fields a higher-precedence source left empty, and never
            // seed fields that aren't reusable answer fields.
            if (value is not null && seedable.Contains(fieldId) && !into.ContainsKey(fieldId))
            {
                into[fieldId] = value;
            }
        }
    }

    private static HashSet<string> SeedableFieldIds(FormDefinition form)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in form.Sections.Where(s => !s.Repeatable))
        {
            foreach (var field in section.Fields)
            {
                if (field.Type is FormFieldType.Calculated || FormFieldTypes.IsMedia(field.Type))
                {
                    continue;
                }

                ids.Add(field.FieldId);
            }
        }

        return ids;
    }
}
