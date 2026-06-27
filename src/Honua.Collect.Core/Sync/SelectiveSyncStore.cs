using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Collect.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// Persists the selective-sync configuration (BACKLOG S2) per layer. A
/// <see cref="LayerSyncScope"/> is saved keyed by its layer, loaded back to
/// reconstruct a <see cref="SelectiveSyncPlan"/>, and deleted when a layer reverts
/// to full sync, so a field device remembers what subset to sync across launches.
/// </summary>
public interface ISelectiveSyncStore
{
    /// <summary>Saves (inserts or replaces) the scope for its layer.</summary>
    /// <param name="scope">The layer scope to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the scope is stored.</returns>
    Task SaveAsync(LayerSyncScope scope, CancellationToken ct = default);

    /// <summary>Loads every stored scope.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored scopes.</returns>
    Task<IReadOnlyList<LayerSyncScope>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Removes the stored scope for a layer (reverting it to full sync).</summary>
    /// <param name="layerKey">Layer to forget.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the scope is removed.</returns>
    Task DeleteAsync(string layerKey, CancellationToken ct = default);

    /// <summary>Loads the stored scopes and assembles them into a plan.</summary>
    /// <param name="includeUnlistedLayers">Whether unlisted layers sync by default.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The reconstructed plan.</returns>
    async Task<SelectiveSyncPlan> LoadPlanAsync(bool includeUnlistedLayers = false, CancellationToken ct = default)
        => new(await LoadAllAsync(ct).ConfigureAwait(false), includeUnlistedLayers);
}

/// <summary>
/// Serializable form of a <see cref="LayerSyncScope"/>. The runtime scope carries
/// a compiled <see cref="SyncAttributeFilter"/> (a delegate) which cannot be
/// serialized, so the persisted form keeps the raw <c>where</c> text and recompiles
/// it on load — round-tripping config, not behaviour.
/// </summary>
internal sealed record LayerSyncScopeDto
{
    public bool Enabled { get; init; } = true;

    public IReadOnlyList<SyncAreaBounds>? Areas { get; init; }

    public SyncExtent? Extent { get; init; }

    public string? Where { get; init; }

    public SyncDateWindow? DateWindow { get; init; }

    public IReadOnlyList<string>? RecordIds { get; init; }
}

/// <summary>
/// An <see cref="ISelectiveSyncStore"/> backed by a single SQLite database file,
/// sharing the encrypted-at-rest posture of the other Collect stores (an optional
/// SQLCipher key applied as the connection <c>Password</c>). The schema is created
/// lazily, so a fresh device file is usable immediately and an existing record
/// database can be reused — the selective-sync table is independent of the others.
/// </summary>
public sealed class SqliteSelectiveSyncStore : SqliteStoreBase, ISelectiveSyncStore
{
    private const string TableName = "collect_selective_sync";

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Creates a store over the given connection string or database file path.</summary>
    /// <param name="connectionStringOrPath">A SQLite connection string, or a path to the database file.</param>
    /// <param name="encryptionKey">Optional SQLCipher key; when non-empty the database is encrypted at rest.</param>
    public SqliteSelectiveSyncStore(string connectionStringOrPath, string? encryptionKey = null)
        : base(connectionStringOrPath, encryptionKey)
    {
    }

    /// <inheritdoc />
    protected override string StoreDescription => "selective-sync database";

    /// <inheritdoc />
    public async Task SaveAsync(LayerSyncScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {TableName} (layer_key, config_json)
            VALUES ($layer_key, $config_json)
            ON CONFLICT(layer_key) DO UPDATE SET config_json = excluded.config_json;
            """;
        command.Parameters.AddWithValue("$layer_key", scope.LayerKey);
        command.Parameters.AddWithValue("$config_json", JsonSerializer.Serialize(ToDto(scope), ConfigJsonOptions));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LayerSyncScope>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT layer_key, config_json FROM {TableName};";

        var scopes = new List<LayerSyncScope>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            scopes.Add(FromDto(reader.GetString(0), reader.GetString(1)));
        }

        return scopes;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string layerKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerKey);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {TableName} WHERE layer_key = $layer_key;";
        command.Parameters.AddWithValue("$layer_key", layerKey);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static LayerSyncScopeDto ToDto(LayerSyncScope scope) => new()
    {
        Enabled = scope.Enabled,
        Areas = scope.Areas.Count > 0 ? scope.Areas : null,
        Extent = scope.Extent,
        Where = scope.Where?.Where,
        DateWindow = scope.DateWindow,
        RecordIds = scope.RecordIds is { Count: > 0 } ids ? ids.ToList() : null,
    };

    private static LayerSyncScope FromDto(string layerKey, string configJson)
    {
        var dto = JsonSerializer.Deserialize<LayerSyncScopeDto>(configJson, ConfigJsonOptions)
            ?? new LayerSyncScopeDto();

        return new LayerSyncScope
        {
            LayerKey = layerKey,
            Enabled = dto.Enabled,
            Areas = dto.Areas ?? [],
            Extent = dto.Extent,
            Where = dto.Where is { } w ? SyncAttributeFilter.Parse(w) : null,
            DateWindow = dto.DateWindow,
            RecordIds = dto.RecordIds is { Count: > 0 } ids
                ? new HashSet<string>(ids, StringComparer.Ordinal)
                : null,
        };
    }

    /// <inheritdoc />
    protected override async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {TableName} (
                layer_key TEXT PRIMARY KEY,
                config_json TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

}
