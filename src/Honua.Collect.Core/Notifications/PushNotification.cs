namespace Honua.Collect.Core.Notifications;

/// <summary>The kind of push notification delivered to the field app (BACKLOG E6).</summary>
public enum PushNotificationKind
{
    /// <summary>A new assignment was dispatched to the user.</summary>
    NewAssignment,

    /// <summary>An assignment was updated or reassigned.</summary>
    AssignmentUpdated,

    /// <summary>A background sync completed.</summary>
    SyncCompleted,

    /// <summary>A submitted record was rejected and needs rework.</summary>
    RecordRejected,

    /// <summary>A general broadcast message.</summary>
    Message,
}

/// <summary>
/// A push notification payload delivered to the field app (BACKLOG E6).
/// </summary>
public sealed record PushNotification
{
    /// <summary>What the notification is about.</summary>
    public required PushNotificationKind Kind { get; init; }

    /// <summary>Short display title.</summary>
    public required string Title { get; init; }

    /// <summary>Display body.</summary>
    public string? Body { get; init; }

    /// <summary>Assignment this notification refers to, when applicable.</summary>
    public string? AssignmentId { get; init; }

    /// <summary>Record this notification refers to, when applicable.</summary>
    public string? RecordId { get; init; }

    /// <summary>Additional provider data.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
