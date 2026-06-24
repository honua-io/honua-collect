using System.Globalization;
using Honua.Sdk.Field.Records;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Assignments;

/// <summary>
/// An <see cref="IAssignmentStore"/> backed by a single SQLite database file via
/// <c>Microsoft.Data.Sqlite</c>, sharing the encrypted-at-rest posture of
/// <see cref="Storage.SqliteRecordStore"/> (an optional SQLCipher key applied as the
/// connection <c>Password</c>). The schema is created lazily on first use, so a
/// fresh device file is usable immediately, and an existing record database can be
/// reused — the assignments table is independent of <c>collect_records</c>.
/// </summary>
public sealed class SqliteAssignmentStore : IAssignmentStore
{
    private const string TableName = "collect_assignments";

    private readonly string _connectionString;
    private readonly bool _encryptionRequested;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    /// <summary>Creates a store over the given connection string or database file path.</summary>
    /// <param name="connectionStringOrPath">A SQLite connection string, or a path to the database file.</param>
    /// <param name="encryptionKey">Optional SQLCipher key; when non-empty the database is encrypted at rest.</param>
    public SqliteAssignmentStore(string connectionStringOrPath, string? encryptionKey = null)
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
    public async Task SaveAsync(FieldAssignment assignment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {TableName}
                (assignment_id, form_id, assigned_user_id, title, instructions,
                 lat, lon, accuracy, due_utc, priority, created_utc, status, record_id,
                 accepted_utc, completed_utc)
            VALUES
                ($assignment_id, $form_id, $assigned_user_id, $title, $instructions,
                 $lat, $lon, $accuracy, $due_utc, $priority, $created_utc, $status, $record_id,
                 $accepted_utc, $completed_utc)
            ON CONFLICT(assignment_id) DO UPDATE SET
                form_id = excluded.form_id,
                assigned_user_id = excluded.assigned_user_id,
                title = excluded.title,
                instructions = excluded.instructions,
                lat = excluded.lat,
                lon = excluded.lon,
                accuracy = excluded.accuracy,
                due_utc = excluded.due_utc,
                priority = excluded.priority,
                created_utc = excluded.created_utc,
                status = excluded.status,
                record_id = excluded.record_id,
                accepted_utc = excluded.accepted_utc,
                completed_utc = excluded.completed_utc;
            """;

        var location = assignment.Location;
        command.Parameters.AddWithValue("$assignment_id", assignment.AssignmentId);
        command.Parameters.AddWithValue("$form_id", assignment.FormId);
        command.Parameters.AddWithValue("$assigned_user_id", assignment.AssignedToUserId);
        command.Parameters.AddWithValue("$title", assignment.Title);
        command.Parameters.AddWithValue("$instructions", (object?)assignment.Instructions ?? DBNull.Value);
        command.Parameters.AddWithValue("$lat", location is null ? DBNull.Value : location.Latitude);
        command.Parameters.AddWithValue("$lon", location is null ? DBNull.Value : location.Longitude);
        command.Parameters.AddWithValue("$accuracy", location?.AccuracyMeters is { } acc ? acc : (object)DBNull.Value);
        command.Parameters.AddWithValue("$due_utc", ToStorage(assignment.DueAtUtc));
        command.Parameters.AddWithValue("$priority", (int)assignment.Priority);
        command.Parameters.AddWithValue("$created_utc", ToStorage(assignment.CreatedAtUtc));
        command.Parameters.AddWithValue("$status", (int)assignment.Status);
        command.Parameters.AddWithValue("$record_id", (object?)assignment.RecordId ?? DBNull.Value);
        command.Parameters.AddWithValue("$accepted_utc", ToStorage(assignment.AcceptedAtUtc));
        command.Parameters.AddWithValue("$completed_utc", ToStorage(assignment.CompletedAtUtc));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FieldAssignment>> LoadAllAsync(CancellationToken ct = default)
        => QueryAsync(userId: null, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<FieldAssignment>> LoadForUserAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return QueryAsync(userId, ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string assignmentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assignmentId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName} WHERE assignment_id = $assignment_id;";
        command.Parameters.AddWithValue("$assignment_id", assignmentId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<FieldAssignment>> QueryAsync(string? userId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT assignment_id, form_id, assigned_user_id, title, instructions,
                   lat, lon, accuracy, due_utc, priority, created_utc, status, record_id,
                   accepted_utc, completed_utc
            FROM {TableName}
            {(userId is null ? string.Empty : "WHERE assigned_user_id = $assigned_user_id")};
            """;
        if (userId is not null)
        {
            command.Parameters.AddWithValue("$assigned_user_id", userId);
        }

        var assignments = new List<FieldAssignment>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            assignments.Add(ReadAssignment(reader));
        }

        return assignments;
    }

    private static FieldAssignment ReadAssignment(SqliteDataReader reader)
    {
        FieldGeoPoint? location = null;
        if (!reader.IsDBNull(5) && !reader.IsDBNull(6))
        {
            double? accuracy = reader.IsDBNull(7) ? null : reader.GetDouble(7);
            location = new FieldGeoPoint(reader.GetDouble(5), reader.GetDouble(6), accuracy);
        }

        var assignment = new FieldAssignment
        {
            AssignmentId = reader.GetString(0),
            FormId = reader.GetString(1),
            AssignedToUserId = reader.GetString(2),
            Title = reader.GetString(3),
            Instructions = reader.IsDBNull(4) ? null : reader.GetString(4),
            Location = location,
            DueAtUtc = FromStorage(reader, 8),
            Priority = (AssignmentPriority)reader.GetInt32(9),
            CreatedAtUtc = FromStorage(reader, 10) ?? default,
        };

        assignment.RestoreState(
            (AssignmentStatus)reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            FromStorage(reader, 13),
            FromStorage(reader, 14));

        return assignment;
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

    /// <summary>
    /// When an encryption key was supplied, fails closed unless SQLCipher is actually
    /// active, so the assignment database is never silently written in plaintext (same
    /// guard as <see cref="Storage.SqliteRecordStore"/>).
    /// </summary>
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
            cipherVersion = null;
        }

        if (string.IsNullOrWhiteSpace(cipherVersion))
        {
            throw new InvalidOperationException(
                "An encryption key was provided but SQLCipher is not active for this database " +
                "(PRAGMA cipher_version returned nothing). Refusing to open the assignment " +
                "database unencrypted.");
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
                CREATE TABLE IF NOT EXISTS {TableName} (
                    assignment_id TEXT PRIMARY KEY,
                    form_id TEXT NOT NULL,
                    assigned_user_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    instructions TEXT NULL,
                    lat REAL NULL,
                    lon REAL NULL,
                    accuracy REAL NULL,
                    due_utc TEXT NULL,
                    priority INTEGER NOT NULL,
                    created_utc TEXT NULL,
                    status INTEGER NOT NULL,
                    record_id TEXT NULL,
                    accepted_utc TEXT NULL,
                    completed_utc TEXT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText =
                $"CREATE INDEX IF NOT EXISTS idx_{TableName}_user ON {TableName} (assigned_user_id);";
            await indexCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static object ToStorage(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    private static object ToStorage(DateTimeOffset? value)
        => value is { } v ? ToStorage(v) : DBNull.Value;

    private static DateTimeOffset? FromStorage(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

    private static bool LooksLikeConnectionString(string value)
        => value.Contains('=', StringComparison.Ordinal);
}
