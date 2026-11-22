// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.History;

public sealed record FieldChange(string FieldId, object? OldValue, object? NewValue);

public sealed record RecordEdit(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string EditorUserId,
    IReadOnlyList<FieldChange> Changes,
    bool AfterSync,
    string? Note = null);
