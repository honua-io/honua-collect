// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Forms.Defaults;

public sealed record FavoriteAnswerSet(string Name, IReadOnlyDictionary<string, object?> Values)
{
    public string Name { get; } = !string.IsNullOrWhiteSpace(Name)
        ? Name
        : throw new ArgumentException("Favorite name is required.", nameof(Name));

    public IReadOnlyDictionary<string, object?> Values { get; } =
        Values ?? throw new ArgumentNullException(nameof(Values));
}

public interface IAnswerMemoryStore
{
    Task RememberLastAsync(string formId, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, object?>> GetLastAsync(string formId, CancellationToken ct = default);

    Task SaveFavoriteAsync(string formId, FavoriteAnswerSet favorite, CancellationToken ct = default);

    Task<FavoriteAnswerSet?> GetFavoriteAsync(string formId, string name, CancellationToken ct = default);

    Task<IReadOnlyList<FavoriteAnswerSet>> ListFavoritesAsync(string formId, CancellationToken ct = default);
}
