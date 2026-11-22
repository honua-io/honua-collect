// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Forms;

public sealed class FieldState
{
    private readonly List<CapturedMediaAttachment> _media = [];
    private readonly List<string> _errors = [];

    internal FieldState(FormField field, FormSection section, int repeatInstance)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        Section = section ?? throw new ArgumentNullException(nameof(section));
        RepeatInstance = repeatInstance;
        AvailableChoices = field.Choices ?? [];
    }

    public FormField Field { get; }

    public FormSection Section { get; }

    public int RepeatInstance { get; }

    public string FieldId => Field.FieldId;

    public object? Value { get; internal set; }

    public bool IsVisible { get; internal set; } = true;

    public bool IsRequired => Field.Required;

    public IReadOnlyList<string> Errors => _errors;

    public bool IsValid => _errors.Count == 0;

    public IReadOnlyList<FieldChoice> AvailableChoices { get; internal set; }

    public IReadOnlyList<CapturedMediaAttachment> Media => _media;

    internal void AddMedia(CapturedMediaAttachment attachment) => _media.Add(attachment);

    internal bool RemoveMedia(string attachmentId)
        => _media.RemoveAll(m => string.Equals(m.AttachmentId, attachmentId, StringComparison.Ordinal)) > 0;

    internal void SetErrors(IEnumerable<string> messages)
    {
        _errors.Clear();
        _errors.AddRange(messages);
    }
}
