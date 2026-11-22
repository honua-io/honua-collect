// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Notifications;

public enum NotificationTarget
{
    Assignment,

    Record,

    SyncCenter,

    Inbox,
}

public sealed record NotificationAction(NotificationTarget Target, string? EntityId);

public static class PushNotificationRouter
{
    public static NotificationAction Resolve(PushNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return notification.Kind switch
        {
            PushNotificationKind.NewAssignment or PushNotificationKind.AssignmentUpdated
                when !string.IsNullOrWhiteSpace(notification.AssignmentId)
                => new NotificationAction(NotificationTarget.Assignment, notification.AssignmentId),

            PushNotificationKind.RecordRejected when !string.IsNullOrWhiteSpace(notification.RecordId)
                => new NotificationAction(NotificationTarget.Record, notification.RecordId),

            PushNotificationKind.SyncCompleted
                => new NotificationAction(NotificationTarget.SyncCenter, null),

            _ => new NotificationAction(NotificationTarget.Inbox, null),
        };
    }
}
