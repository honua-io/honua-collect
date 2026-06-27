using Honua.Collect.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Field.Forms.Localization;

/// <summary>
/// An <see cref="ILocaleStore"/> backed by the same on-device SQLite engine as
/// <see cref="Storage.SqliteRecordStore"/> — it reuses the device database file
/// rather than introducing a new store. The active language lives in one row per
/// form id, so re-opening a form restores the language the user last picked
/// (BACKLOG F2). The schema is created lazily on first use.
/// </summary>
public sealed class SqliteLocaleStore : SqliteStoreBase, ILocaleStore
{
    private const string Table = "collect_form_locale";

    /// <summary>
    /// Creates a store over the given connection string or database file path —
    /// pass the same value used for <see cref="Storage.SqliteRecordStore"/> so the
    /// active locale shares the device database.
    /// </summary>
    /// <param name="connectionStringOrPath">A SQLite connection string, or a path to the database file.</param>
    /// <param name="encryptionKey">
    /// Optional SQLCipher key, applied as the connection <c>Password</c> (PRAGMA
    /// key) when non-empty — supply the same key as the record store so the whole
    /// device database is encrypted at rest.
    /// </param>
    public SqliteLocaleStore(string connectionStringOrPath, string? encryptionKey = null)
        : base(connectionStringOrPath, encryptionKey)
    {
    }

    /// <inheritdoc />
    protected override string StoreDescription => "locale database";

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task<string?> GetActiveLanguageAsync(string formId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT language FROM {Table} WHERE form_id = $form_id;";
        command.Parameters.AddWithValue("$form_id", formId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <inheritdoc />
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
