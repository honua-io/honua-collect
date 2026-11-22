// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Forms.Defaults;

public sealed class FormDefaultsService
{
    private readonly IAnswerMemoryStore _store;

    public FormDefaultsService(IAnswerMemoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

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
