// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Forms;

public sealed class RepeatInstance : ICaptureHost
{
    private readonly FormSession _session;

    internal RepeatInstance(string instanceId, FormSession session)
    {
        InstanceId = instanceId;
        _session = session;
    }

    public string InstanceId { get; }

    public IReadOnlyList<FieldState> Fields => _session.Fields;

    public IEnumerable<FieldState> VisibleFields => _session.VisibleFields;

    public FieldState GetField(string fieldId) => _session.GetField(fieldId);

    public object? GetValue(string fieldId) => _session.GetValue(fieldId);

    public void SetValue(string fieldId, object? value) => _session.SetValue(fieldId, value);

    public void AddMedia(CapturedMediaAttachment attachment) => _session.AddMedia(attachment);

    public bool RemoveMedia(string fieldId, string attachmentId) => _session.RemoveMedia(fieldId, attachmentId);

    public FormValidationResult Validate() => _session.Validate();

    internal Dictionary<string, object?> SnapshotValues()
        => new(_session.Record.Values, StringComparer.OrdinalIgnoreCase);
}
