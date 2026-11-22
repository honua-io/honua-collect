// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Related;

public interface IRelatedRecordStore
{
    Task LinkAsync(
        string parentRecordId,
        string fieldId,
        FieldRecordLinkValue link,
        RecordLinkBehavior behavior = RecordLinkBehavior.Cascade,
        CancellationToken ct = default);

    Task<bool> UnlinkAsync(string parentRecordId, string fieldId, string childRecordId, CancellationToken ct = default);

    Task<IReadOnlyList<FieldRecordLinkValue>> ListAsync(string parentRecordId, string fieldId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> DeleteParentAsync(string parentRecordId, CancellationToken ct = default);
}

public sealed class RelatedRecordIntegrityException : InvalidOperationException
{
    public RelatedRecordIntegrityException(string parentRecordId, int childCount)
        : base($"Record '{parentRecordId}' still has {childCount} linked child record(s) under a restrict policy; unlink them before deleting.")
    {
        ParentRecordId = parentRecordId;
        ChildCount = childCount;
    }

    public string ParentRecordId { get; }

    public int ChildCount { get; }
}
