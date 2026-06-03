using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// How a server feature relates to what the device already holds, after a pull.
/// </summary>
public enum PullDisposition
{
    /// <summary>The server has a feature the device has never seen; create it locally.</summary>
    New,

    /// <summary>The device already has this feature and it matches the server; nothing to do.</summary>
    Unchanged,

    /// <summary>
    /// The device has local edits to this feature and the server version also
    /// differs — a real conflict that needs manual review.
    /// </summary>
    Conflict,
}

/// <summary>
/// The classification of a single pulled server feature against the local store.
/// </summary>
/// <param name="ObjectId">The server object id the feature is keyed by.</param>
/// <param name="Disposition">How the feature relates to the local record.</param>
/// <param name="Server">The decoded server record.</param>
/// <param name="Local">The matching local record, when one exists.</param>
/// <param name="Conflict">
/// The field-level conflict, present only when <see cref="Disposition"/> is
/// <see cref="PullDisposition.Conflict"/>. The review UI binds to this.
/// </param>
public sealed record PullClassification(
    long ObjectId,
    PullDisposition Disposition,
    FieldRecord Server,
    FieldRecord? Local,
    RecordConflict? Conflict);

/// <summary>
/// The structured outcome of merging a server pull into the local store: every
/// pulled feature classified, plus convenience views for the records to create
/// and the conflicts to review.
/// </summary>
public sealed class PullMergeResult
{
    internal PullMergeResult(IReadOnlyList<PullClassification> classifications)
    {
        Classifications = classifications;
        NewRecords = classifications.Where(c => c.Disposition == PullDisposition.New).ToList();
        Unchanged = classifications.Where(c => c.Disposition == PullDisposition.Unchanged).ToList();
        Conflicts = classifications
            .Where(c => c.Disposition == PullDisposition.Conflict && c.Conflict is not null)
            .Select(c => c.Conflict!)
            .ToList();
    }

    /// <summary>Every pulled feature with its disposition.</summary>
    public IReadOnlyList<PullClassification> Classifications { get; }

    /// <summary>Features the device had not seen before (to insert locally).</summary>
    public IReadOnlyList<PullClassification> NewRecords { get; }

    /// <summary>Features already present locally and matching the server.</summary>
    public IReadOnlyList<PullClassification> Unchanged { get; }

    /// <summary>Real conflicts (local edits vs differing server version) to review.</summary>
    public IReadOnlyList<RecordConflict> Conflicts { get; }

    /// <summary>Whether anything needs manual conflict review.</summary>
    public bool HasConflicts => Conflicts.Count > 0;
}

/// <summary>
/// The read/merge half of bidirectional sync. Given the records the device
/// already holds (keyed by their server object id) and the features pulled from
/// the server via <see cref="GeoServicesFeatureSync.QueryAsync"/>, this classifies
/// each pulled feature as new, unchanged, or conflicting, using
/// <see cref="RecordConflictDetector"/> for the field-level diff so real conflicts
/// flow straight into the existing conflict-review workflow.
/// </summary>
public sealed class FeaturePullService
{
    /// <summary>
    /// Classifies each pulled server feature against the local store.
    /// </summary>
    /// <param name="form">Form definition supplying field order/labels for diffing.</param>
    /// <param name="pulled">Features pulled from the server.</param>
    /// <param name="localByObjectId">
    /// Local records keyed by the server object id they were synced as. A feature
    /// with no entry here is treated as new-from-server.
    /// </param>
    /// <returns>The structured merge outcome.</returns>
    public PullMergeResult Merge(
        FormDefinition form,
        IReadOnlyList<PulledRecord> pulled,
        IReadOnlyDictionary<long, FieldRecord> localByObjectId)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(pulled);
        ArgumentNullException.ThrowIfNull(localByObjectId);

        var classifications = new List<PullClassification>(pulled.Count);

        foreach (var feature in pulled)
        {
            if (!localByObjectId.TryGetValue(feature.ObjectId, out var local))
            {
                classifications.Add(new PullClassification(
                    feature.ObjectId, PullDisposition.New, feature.Record, null, null));
                continue;
            }

            // Diff the local record against the server's version using the same
            // field-level detector the manual review screen consumes. Align the
            // record ids so the resulting conflict/merge stays keyed locally.
            var serverView = WithRecordId(feature.Record, local.RecordId);
            var conflict = RecordConflictDetector.Detect(form, local, serverView);

            classifications.Add(conflict.HasConflicts
                ? new PullClassification(feature.ObjectId, PullDisposition.Conflict, serverView, local, conflict)
                : new PullClassification(feature.ObjectId, PullDisposition.Unchanged, serverView, local, null));
        }

        return new PullMergeResult(classifications);
    }

    private static FieldRecord WithRecordId(FieldRecord source, string recordId)
    {
        var copy = new FieldRecord
        {
            RecordId = recordId,
            FormId = source.FormId,
            Location = source.Location,
            Status = source.Status,
            AssignedUserId = source.AssignedUserId,
        };

        foreach (var pair in source.Values)
        {
            copy.Values[pair.Key] = pair.Value;
        }

        return copy;
    }
}
