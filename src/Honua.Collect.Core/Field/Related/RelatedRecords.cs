// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Related;

public sealed class RelatedRecords
{
    private readonly RecordLinkField _field;

    public RelatedRecords(FieldRecord parent, FormField field, RecordLinkBehavior behavior = RecordLinkBehavior.Cascade)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(field);

        Parent = parent;
        _field = new RecordLinkField(parent, field);
        Behavior = behavior;
    }

    public FieldRecord Parent { get; }

    public FormField Field => _field.Field;

    public string? ReferencedFormId => _field.ReferencedFormId;

    public RecordLinkBehavior Behavior { get; }

    public IReadOnlyList<FieldRecordLinkValue> Children => _field.Links;

    public int Count => _field.Count;

    public bool Link(string childRecordId, string? label = null, string? sourceId = null)
        => _field.Add(childRecordId, label, sourceId);

    public bool Link(FieldRecord child, string? label = null)
        => _field.Add(child, label);

    public FieldRecord Create(string childRecordId, string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childRecordId);

        var child = new FieldRecord
        {
            RecordId = childRecordId,
            FormId = ReferencedFormId ?? Field.FieldId,
        };

        _field.Add(child, label);
        return child;
    }

    public bool Unlink(string childRecordId)
        => _field.Remove(childRecordId);

    public async Task PersistAsync(IRelatedRecordStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        foreach (var link in _field.Links)
        {
            await store.LinkAsync(Parent.RecordId, Field.FieldId, link, Behavior, ct).ConfigureAwait(false);
        }
    }
}
