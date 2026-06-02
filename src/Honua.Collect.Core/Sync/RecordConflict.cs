using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// Which side of a field-level conflict to keep when resolving a record
/// conflict for manual review (BACKLOG S1).
/// </summary>
public enum ConflictResolution
{
    /// <summary>Keep the value captured on this device.</summary>
    KeepLocal,

    /// <summary>Accept the value currently on the server.</summary>
    KeepServer,
}

/// <summary>
/// A single field whose local value differs from the server value.
/// </summary>
/// <param name="FieldId">Field identifier.</param>
/// <param name="Label">Human-readable field label for the review UI.</param>
/// <param name="LocalValue">Value captured on this device.</param>
/// <param name="ServerValue">Value currently on the server.</param>
public sealed record FieldConflict(string FieldId, string Label, object? LocalValue, object? ServerValue);

/// <summary>
/// A version conflict between a locally-edited record and the server's current
/// version, broken down into the individual fields that differ. This is the
/// product-owned model the manual conflict-review screen binds to: the mobile
/// sync engine's <c>ManualReview</c> strategy marks an operation as conflicted,
/// and this turns the two record versions into an actionable, field-by-field
/// diff plus a merge.
/// </summary>
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

    /// <summary>Record identifier shared by both versions.</summary>
    public string RecordId => _local.RecordId;

    /// <summary>Fields whose values differ between the two versions.</summary>
    public IReadOnlyList<FieldConflict> FieldConflicts { get; }

    /// <summary>Whether there is anything to resolve.</summary>
    public bool HasConflicts => FieldConflicts.Count > 0;

    /// <summary>
    /// Produces a merged record by applying a per-field resolution choice. The
    /// merge starts from the local record and overwrites each conflicted field
    /// with the chosen side; non-conflicted fields are left as the local version.
    /// </summary>
    /// <param name="choices">Per-field resolution choices, keyed by field id.</param>
    /// <param name="defaultResolution">Choice for any conflicted field absent from <paramref name="choices"/>.</param>
    /// <returns>The merged record.</returns>
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

        foreach (var pair in _local.Values)
        {
            merged.Values[pair.Key] = pair.Value;
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

    /// <summary>Resolves every conflicted field to the same side.</summary>
    /// <param name="resolution">The side to keep for all conflicts.</param>
    /// <returns>The merged record.</returns>
    public FieldRecord ResolveAll(ConflictResolution resolution)
        => Resolve(new Dictionary<string, ConflictResolution>(), resolution);
}
