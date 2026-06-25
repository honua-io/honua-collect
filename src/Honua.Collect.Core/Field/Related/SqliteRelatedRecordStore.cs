using Honua.Sdk.Field.Records;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Field.Related;

/// <summary>
/// An <see cref="IRelatedRecordStore"/> backed by the same on-device SQLite engine
/// as <see cref="Storage.SqliteRecordStore"/> — it reuses the device database file
/// rather than introducing a new store (BACKLOG F4). Each parent→child link is one
/// row keyed by (parent, field, child); the row also carries the referenced form,
/// a display label, and the <see cref="RecordLinkBehavior"/> so referential
/// integrity is enforced at delete time. The schema is created lazily on first use.
/// </summary>
public sealed class SqliteRelatedRecordStore : IRelatedRecordStore
{
    private const string Table = "collect_record_links";

    private readonly string _connectionString;
    private readonly bool _encryptionRequested;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    /// <summary>
    /// Creates a store over the given connection string or database file path — pass
    /// the same value used for <see cref="Storage.SqliteRecordStore"/> so links share
    /// the device database.
    /// </summary>
    /// <param name="connectionStringOrPath">A SQLite connection string, or a path to the database file.</param>
    /// <param name="encryptionKey">
    /// Optional SQLCipher key, applied as the connection <c>Password</c> (PRAGMA key)
    /// when non-empty — supply the same key as the record store so the whole device
    /// database is encrypted at rest.
    /// </param>
    public SqliteRelatedRecordStore(string connectionStringOrPath, string? encryptionKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringOrPath);
        if (LooksLikeConnectionString(connectionStringOrPath))
        {
            _connectionString = connectionStringOrPath;
            return;
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = connectionStringOrPath };
        if (!string.IsNullOrEmpty(encryptionKey))
        {
            builder.Password = encryptionKey;
            _encryptionRequested = true;
        }

        _connectionString = builder.ToString();
    }

    /// <inheritdoc />
    public async Task LinkAsync(
        string parentRecordId,
        string fieldId,
        FieldRecordLinkValue link,
        RecordLinkBehavior behavior = RecordLinkBehavior.Cascade,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentRecordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.RecordId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {Table} (parent_record_id, field_id, child_record_id, child_form_id, source_id, label, behavior, ordinal)
            VALUES ($parent, $field, $child, $form, $source, $label, $behavior,
                    (SELECT COALESCE(MAX(ordinal), -1) + 1 FROM {Table} WHERE parent_record_id = $parent AND field_id = $field))
            ON CONFLICT(parent_record_id, field_id, child_record_id) DO UPDATE SET
                child_form_id = excluded.child_form_id,
                source_id = excluded.source_id,
                label = excluded.label,
                behavior = excluded.behavior;
            """;
        command.Parameters.AddWithValue("$parent", parentRecordId);
        command.Parameters.AddWithValue("$field", fieldId);
        command.Parameters.AddWithValue("$child", link.RecordId);
        command.Parameters.AddWithValue("$form", (object?)link.FormId ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)link.SourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$label", (object?)link.Label ?? DBNull.Value);
        command.Parameters.AddWithValue("$behavior", (int)behavior);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UnlinkAsync(string parentRecordId, string fieldId, string childRecordId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentRecordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(childRecordId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {Table} WHERE parent_record_id = $parent AND field_id = $field AND child_record_id = $child;";
        command.Parameters.AddWithValue("$parent", parentRecordId);
        command.Parameters.AddWithValue("$field", fieldId);
        command.Parameters.AddWithValue("$child", childRecordId);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FieldRecordLinkValue>> ListAsync(string parentRecordId, string fieldId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentRecordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT child_record_id, child_form_id, source_id, label
            FROM {Table}
            WHERE parent_record_id = $parent AND field_id = $field
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$parent", parentRecordId);
        command.Parameters.AddWithValue("$field", fieldId);

        var links = new List<FieldRecordLinkValue>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            links.Add(new FieldRecordLinkValue
            {
                RecordId = reader.GetString(0),
                FormId = reader.IsDBNull(1) ? null : reader.GetString(1),
                SourceId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Label = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return links;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> DeleteParentAsync(string parentRecordId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentRecordId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Refuse the delete if any restrict-policy child still references the parent.
        await using (var guard = connection.CreateCommand())
        {
            guard.Transaction = (SqliteTransaction)transaction;
            guard.CommandText = $"SELECT COUNT(*) FROM {Table} WHERE parent_record_id = $parent AND behavior = $restrict;";
            guard.Parameters.AddWithValue("$parent", parentRecordId);
            guard.Parameters.AddWithValue("$restrict", (int)RecordLinkBehavior.Restrict);
            var restricted = Convert.ToInt32(await guard.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (restricted > 0)
            {
                throw new RelatedRecordIntegrityException(parentRecordId, restricted);
            }
        }

        // Collect the cascade children so the caller can delete the child records,
        // then remove every link row this parent owns.
        var cascaded = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText = $"SELECT child_record_id FROM {Table} WHERE parent_record_id = $parent AND behavior = $cascade ORDER BY ordinal;";
            select.Parameters.AddWithValue("$parent", parentRecordId);
            select.Parameters.AddWithValue("$cascade", (int)RecordLinkBehavior.Cascade);
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                cascaded.Add(reader.GetString(0));
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = $"DELETE FROM {Table} WHERE parent_record_id = $parent;";
            delete.Parameters.AddWithValue("$parent", parentRecordId);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return cascaded;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await EnsureCipherEngagedAsync(connection, ct).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureCipherEngagedAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!_encryptionRequested)
        {
            return;
        }

        string? cipherVersion = null;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cipher_version;";
            cipherVersion = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
        catch (SqliteException)
        {
            cipherVersion = null;
        }

        if (string.IsNullOrWhiteSpace(cipherVersion))
        {
            throw new InvalidOperationException(
                "An encryption key was provided but SQLCipher is not active for this database " +
                "(PRAGMA cipher_version returned nothing). Refusing to store record links unencrypted.");
        }
    }

    private async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (_schemaReady)
        {
            return;
        }

        await _schemaGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {Table} (
                    parent_record_id TEXT NOT NULL,
                    field_id TEXT NOT NULL,
                    child_record_id TEXT NOT NULL,
                    child_form_id TEXT NULL,
                    source_id TEXT NULL,
                    label TEXT NULL,
                    behavior INTEGER NOT NULL,
                    ordinal INTEGER NOT NULL,
                    PRIMARY KEY (parent_record_id, field_id, child_record_id)
                );
                CREATE INDEX IF NOT EXISTS ix_collect_record_links_parent
                    ON {Table} (parent_record_id);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static bool LooksLikeConnectionString(string value)
        => value.Contains('=', StringComparison.Ordinal);
}
