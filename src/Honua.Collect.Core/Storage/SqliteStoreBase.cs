// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Storage;

public abstract class SqliteStoreBase
{
    private const int BusyTimeoutMs = 5000;

    private readonly string _connectionString;
    private readonly bool _encryptionRequested;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    protected SqliteStoreBase(string connectionStringOrPath, string? encryptionKey)
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
            builder.Password = encryptionKey; // SQLCipher: applied as PRAGMA key on open
            _encryptionRequested = true;
        }

        _connectionString = builder.ToString();
    }

    protected abstract string StoreDescription { get; }

    protected abstract Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct);

    protected async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await EnsureCipherEngagedAsync(connection, ct).ConfigureAwait(false);
            await ApplyConnectionPragmasAsync(connection, ct).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ApplyConnectionPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        // busy_timeout: wait (not fail) on a contended lock; WAL: readers and a
        // writer no longer block each other. journal_mode returns a row, which a
        // non-query execution harmlessly ignores.
        command.CommandText =
            $"PRAGMA busy_timeout={BusyTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}; " +
            "PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            cipherVersion = result as string;
        }
        catch (SqliteException)
        {
            // Stock SQLite doesn't recognize the SQLCipher-specific pragma; treat that
            // as "cipher not engaged" rather than a hard error here so the single
            // InvalidOperationException below is the one consistent failure.
            cipherVersion = null;
        }

        if (string.IsNullOrWhiteSpace(cipherVersion))
        {
            throw new InvalidOperationException(
                "An encryption key was provided but SQLCipher is not active for this database " +
                "(PRAGMA cipher_version returned nothing). The native SQLCipher bundle " +
                "(e.g. SQLitePCLRaw.bundle_e_sqlcipher) is not wired in, so the database would " +
                $"be stored in plaintext. Refusing to open the {StoreDescription} unencrypted.");
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

            await CreateSchemaAsync(connection, ct).ConfigureAwait(false);
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    protected static string ToStorage(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    protected static object ToStorage(DateTimeOffset? value)
        => value is { } v ? ToStorage(v) : DBNull.Value;

    protected static DateTimeOffset FromStorage(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    protected static DateTimeOffset? FromStorage(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : FromStorage(reader.GetString(ordinal));

    private static bool LooksLikeConnectionString(string value)
        => value.Contains('=', StringComparison.Ordinal);
}
