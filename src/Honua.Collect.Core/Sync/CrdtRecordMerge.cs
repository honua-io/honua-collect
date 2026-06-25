using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Sync;

/// <summary>
/// One side's view of a single field for a CRDT merge: the field's value plus the
/// provenance that makes last-writer-wins deterministic — when the field was last
/// written and, as a stable tie-break, which actor (device/site) wrote it.
/// </summary>
/// <param name="Value">The field value on this side (null/missing means cleared).</param>
/// <param name="TimestampUtc">When this side last wrote the field.</param>
/// <param name="ActorId">
/// The writer's stable id, used <em>only</em> to break exact-timestamp ties so the
/// merge is total and order-independent. Higher ordinal wins.
/// </param>
public readonly record struct CrdtFieldVersion(object? Value, DateTimeOffset TimestampUtc, string ActorId);

/// <summary>
/// A per-field, last-writer-with-history merge layered on
/// <see cref="RecordConflictDetector"/> (#38). Where manual <c>Resolve</c> asks a
/// human to pick a side, this converges concurrent edits <em>automatically and
/// deterministically</em>: each field is a last-writer-wins register keyed by its
/// write timestamp, with the actor id as a stable tie-break, so the same two edits
/// merge to the same result regardless of which side is called "local" or the order
/// they reconcile in. No field is dropped — fields only one side touched are carried
/// through — and because the inputs come from the durable edit history, the full
/// who/when/what audit is retained alongside the converged value.
/// </summary>
/// <remarks>
/// Determinism (the CRDT guarantee) rests on three properties of the field merge:
/// it is commutative (<c>merge(a,b) == merge(b,a)</c>), idempotent
/// (<c>merge(a,a) == a</c>), and associative across more than two replicas. The
/// timestamp+actor ordering is a total order over writes, so the "latest" write is
/// well-defined even when timestamps collide.
/// </remarks>
public static class CrdtRecordMerge
{
    /// <summary>
    /// Merges two concurrent versions of a record field-by-field. For every field
    /// present on either side, the version with the later timestamp wins; exact ties
    /// are broken by the greater <see cref="CrdtFieldVersion.ActorId"/> (ordinal), so
    /// the result is independent of argument order.
    /// </summary>
    /// <param name="left">One replica's field versions, keyed by field id.</param>
    /// <param name="right">The other replica's field versions, keyed by field id.</param>
    /// <returns>The converged field values (later-writer-wins per field).</returns>
    public static IReadOnlyDictionary<string, CrdtFieldVersion> Merge(
        IReadOnlyDictionary<string, CrdtFieldVersion> left,
        IReadOnlyDictionary<string, CrdtFieldVersion> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var merged = new Dictionary<string, CrdtFieldVersion>(StringComparer.OrdinalIgnoreCase);

        foreach (var fieldId in left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var hasLeft = left.TryGetValue(fieldId, out var l);
            var hasRight = right.TryGetValue(fieldId, out var r);

            merged[fieldId] = (hasLeft, hasRight) switch
            {
                (true, false) => l,
                (false, true) => r,
                _ => Pick(l, r),
            };
        }

        return merged;
    }

    /// <summary>
    /// Materializes a converged <see cref="FieldRecord"/> from a CRDT field map,
    /// dropping fields whose winning value is missing (a converged delete).
    /// </summary>
    /// <param name="recordId">The merged record's id.</param>
    /// <param name="formId">The merged record's form id.</param>
    /// <param name="merged">The converged field versions.</param>
    /// <returns>The merged record.</returns>
    public static FieldRecord ToRecord(
        string recordId,
        string formId,
        IReadOnlyDictionary<string, CrdtFieldVersion> merged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentNullException.ThrowIfNull(merged);

        var record = new FieldRecord { RecordId = recordId, FormId = formId };
        foreach (var (fieldId, version) in merged)
        {
            if (!IsMissing(version.Value))
            {
                record.Values[fieldId] = version.Value;
            }
        }

        return record;
    }

    /// <summary>
    /// The winning version of a single field. The later timestamp wins; an exact tie
    /// is broken by the greater actor id. When timestamp <em>and</em> actor match, the
    /// writes are the same event — return either (idempotent), preferring a non-missing
    /// value so a stale empty echo can't clobber a real value at the same instant.
    /// </summary>
    private static CrdtFieldVersion Pick(CrdtFieldVersion a, CrdtFieldVersion b)
    {
        var byTime = a.TimestampUtc.CompareTo(b.TimestampUtc);
        if (byTime != 0)
        {
            return byTime > 0 ? a : b;
        }

        var byActor = string.CompareOrdinal(a.ActorId, b.ActorId);
        if (byActor != 0)
        {
            return byActor > 0 ? a : b;
        }

        // Same timestamp and same actor: the same logical write. Idempotent, but if
        // one carries a value and the other is missing, keep the value.
        return IsMissing(a.Value) && !IsMissing(b.Value) ? b : a;
    }

    private static bool IsMissing(object? value)
        => RecordConflictDetector.IsMissing(value);
}
