// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Field.Forms.Localization;

public sealed class SqliteLocaleStore : SqliteStoreBase, ILocaleStore
{
    private const string Table = "collect_form_locale";

    public SqliteLocaleStore(string connectionStringOrPath, string? encryptionKey = null)
        : base(connectionStringOrPath, encryptionKey)
    {
    }

    protected override string StoreDescription => "locale database";

    public async Task SetActiveLanguageAsync(string formId, string language, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {Table} (form_id, language)
            VALUES ($form_id, $language)
            ON CONFLICT(form_id) DO UPDATE SET language = excluded.language;
            """;
        command.Parameters.AddWithValue("$form_id", formId);
        command.Parameters.AddWithValue("$language", language);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> GetActiveLanguageAsync(string formId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT language FROM {Table} WHERE form_id = $form_id;";
        command.Parameters.AddWithValue("$form_id", formId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    protected override async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {Table} (
                form_id TEXT PRIMARY KEY,
                language TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

}
