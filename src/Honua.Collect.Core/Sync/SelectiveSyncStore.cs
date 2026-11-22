// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Collect.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Honua.Collect.Core.Sync;

public interface ISelectiveSyncStore
{
    Task SaveAsync(LayerSyncScope scope, CancellationToken ct = default);

    Task<IReadOnlyList<LayerSyncScope>> LoadAllAsync(CancellationToken ct = default);

    Task DeleteAsync(string layerKey, CancellationToken ct = default);

    async Task<SelectiveSyncPlan> LoadPlanAsync(bool includeUnlistedLayers = false, CancellationToken ct = default)
        => new(await LoadAllAsync(ct).ConfigureAwait(false), includeUnlistedLayers);
}

internal sealed record LayerSyncScopeDto
{
    public bool Enabled { get; init; } = true;

    public IReadOnlyList<SyncAreaBounds>? Areas { get; init; }

    public SyncExtent? Extent { get; init; }

    public string? Where { get; init; }

    public SyncDateWindow? DateWindow { get; init; }

    public IReadOnlyList<string>? RecordIds { get; init; }
}

public sealed class SqliteSelectiveSyncStore : SqliteStoreBase, ISelectiveSyncStore
{
    private const string TableName = "collect_selective_sync";

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public SqliteSelectiveSyncStore(string connectionStringOrPath, string? encryptionKey = null)
        : base(connectionStringOrPath, encryptionKey)
    {
    }

    protected override string StoreDescription => "selective-sync database";

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
