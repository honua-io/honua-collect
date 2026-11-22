// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field;

public sealed class RecordLinkField
{
    private readonly FieldRecord _parent;
    private readonly List<FieldRecordLinkValue> _links;

    public RecordLinkField(FieldRecord parent, FormField field)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(field);

        if (field.Type != FormFieldType.RecordLink)
        {
            throw new ArgumentException($"Field '{field.FieldId}' is {field.Type}, not a RecordLink field.", nameof(field));
        }

        _parent = parent;
        Field = field;
        _links = ReadLinks(parent, field.FieldId);
    }

    public FormField Field { get; }

    public string? ReferencedFormId => Field.ReferencedFormId;

    public IReadOnlyList<FieldRecordLinkValue> Links => _links;

    public int Count => _links.Count;

    public bool Add(string recordId, string? label = null, string? sourceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        if (_links.Any(l => string.Equals(l.RecordId, recordId, StringComparison.Ordinal)))
        {
            return false;
        }

        _links.Add(new FieldRecordLinkValue
        {
            RecordId = recordId,
            FormId = ReferencedFormId,
            SourceId = sourceId,
            Label = label,
        });

        Write();
        return true;
    }

    public bool Add(FieldRecord child, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(child);
        return Add(child.RecordId, label, sourceId: null);
    }

    public bool Remove(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        if (_links.RemoveAll(l => string.Equals(l.RecordId, recordId, StringComparison.Ordinal)) == 0)
        {
            return false;
        }

        Write();
        return true;
    }

    private void Write()
        => _parent.Values[Field.FieldId] = _links.Count == 0 ? null : new List<FieldRecordLinkValue>(_links);

    private static List<FieldRecordLinkValue> ReadLinks(FieldRecord parent, string fieldId)
    {
        if (!parent.Values.TryGetValue(fieldId, out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            FieldRecordLinkValue single => [single],
            IEnumerable<FieldRecordLinkValue> many => [.. many],
            _ => [],
        };
    }
}
