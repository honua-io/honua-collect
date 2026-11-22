// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Notifications;

public enum PushNotificationKind
{
    NewAssignment,

    AssignmentUpdated,

    SyncCompleted,

    RecordRejected,

    Message,
}

public sealed record PushNotification
{
    public required PushNotificationKind Kind { get; init; }

    public required string Title { get; init; }

    public string? Body { get; init; }

    public string? AssignmentId { get; init; }

    public string? RecordId { get; init; }

    public IReadOnlyDictionary<string, string> Data { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
