// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

public enum PullDisposition
{
    New,

    Unchanged,

    Conflict,
}

public sealed record PullClassification(
    long ObjectId,
    PullDisposition Disposition,
    FieldRecord Server,
    FieldRecord? Local,
    RecordConflict? Conflict);

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

    public IReadOnlyList<PullClassification> Classifications { get; }

    public IReadOnlyList<PullClassification> NewRecords { get; }

    public IReadOnlyList<PullClassification> Unchanged { get; }

    public IReadOnlyList<RecordConflict> Conflicts { get; }

    public bool HasConflicts => Conflicts.Count > 0;
}

public sealed class FeaturePullService
{
    public PullMergeResult Merge(
        FormDefinition form,
        IReadOnlyList<PulledRecord> pulled,
        IReadOnlyDictionary<long, FieldRecord> localByObjectId,
        SelectiveSyncFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(pulled);
        ArgumentNullException.ThrowIfNull(localByObjectId);

        var classifications = new List<PullClassification>(pulled.Count);

        foreach (var feature in pulled)
        {
            // Selective sync: a partial pull only merges features that
            // match the layer's scope. A non-matching feature is skipped entirely —
            // it is never classified New (so it is not inserted) and never diffed
            // against a local record (so any local record stays untouched).
            if (filter is not null && !filter.IncludesPulled(feature))
            {
                continue;
            }

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
