// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

public enum ConflictResolution
{
    KeepLocal,

    KeepServer,
}

public sealed record FieldConflict(string FieldId, string Label, object? LocalValue, object? ServerValue);

public sealed class RecordConflict
{
    private readonly FieldRecord _local;
    private readonly FieldRecord _server;

    internal RecordConflict(FieldRecord local, FieldRecord server, IReadOnlyList<FieldConflict> fieldConflicts)
    {
        _local = local;
        _server = server;
        FieldConflicts = fieldConflicts;
    }

    public string RecordId => _local.RecordId;

    public IReadOnlyList<FieldConflict> FieldConflicts { get; }

    public bool HasConflicts => FieldConflicts.Count > 0;

    public FieldRecord Resolve(
        IReadOnlyDictionary<string, ConflictResolution> choices,
        ConflictResolution defaultResolution = ConflictResolution.KeepServer)
    {
        ArgumentNullException.ThrowIfNull(choices);

        var merged = new FieldRecord
        {
            RecordId = _local.RecordId,
            FormId = _local.FormId,
            Location = _local.Location,
            Status = _local.Status,
            AssignedUserId = _local.AssignedUserId,
        };

        // Seed from local first, then carry over any server-only attributes that
        // the local record doesn't have. Fields that genuinely differ are listed
        // in FieldConflicts and resolved explicitly below, so a server attribute
        // present on both sides keeps its local value here and is corrected by the
        // conflict pass if it differs.
        foreach (var pair in _local.Values)
        {
            merged.Values[pair.Key] = pair.Value;
        }

        foreach (var pair in _server.Values)
        {
            if (!merged.Values.ContainsKey(pair.Key))
            {
                merged.Values[pair.Key] = pair.Value;
            }
        }

        foreach (var conflict in FieldConflicts)
        {
            var resolution = choices.TryGetValue(conflict.FieldId, out var choice) ? choice : defaultResolution;
            merged.Values[conflict.FieldId] = resolution == ConflictResolution.KeepServer
                ? conflict.ServerValue
                : conflict.LocalValue;
        }

        foreach (var media in _local.Media)
        {
            merged.Media.Add(media);
        }

        return merged;
    }

    public FieldRecord ResolveAll(ConflictResolution resolution)
        => Resolve(new Dictionary<string, ConflictResolution>(), resolution);
}
