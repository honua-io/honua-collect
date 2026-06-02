namespace Honua.Collect.Core.Notifications;

/// <summary>Where tapping a notification should take the user.</summary>
public enum NotificationTarget
{
    /// <summary>Open a specific assignment.</summary>
    Assignment,

    /// <summary>Open a specific record.</summary>
    Record,

    /// <summary>Open the sync status center.</summary>
    SyncCenter,

    /// <summary>Open the inbox / message list.</summary>
    Inbox,
}

/// <summary>The navigation a notification resolves to.</summary>
/// <param name="Target">Screen to open.</param>
/// <param name="EntityId">Assignment or record id, when the target needs one.</param>
public sealed record NotificationAction(NotificationTarget Target, string? EntityId);

/// <summary>
/// Resolves a <see cref="PushNotification"/> into the in-app navigation it should
/// trigger when tapped (BACKLOG E6). Keeping this mapping in Core makes the
/// deep-link behaviour deterministic and testable independent of the platform
/// push transport.
/// </summary>
public static class PushNotificationRouter
{
    /// <summary>Resolves the action a notification should perform.</summary>
    /// <param name="notification">The received notification.</param>
    /// <returns>The navigation action.</returns>
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
