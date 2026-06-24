namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// Records security- and data-relevant events to the durable audit trail (BACKLOG
/// E3). The sink owns sequencing and timestamping; callers describe what happened.
/// Convenience methods cover the lifecycle events the app raises; arbitrary events
/// go through <see cref="Record(AuditEvent)"/>.
/// </summary>
public interface IAuditSink
{
    /// <summary>Records a fully-formed event (its details are scrubbed before persisting).</summary>
    /// <param name="auditEvent">Event to record.</param>
    /// <returns>The persisted entry, including its assigned sequence and chain hash.</returns>
    AuditEntry Record(AuditEvent auditEvent);

    /// <summary>Records a successful sign-in.</summary>
    /// <param name="userId">Authenticated user id.</param>
    /// <param name="details">Optional non-secret context (provider, method).</param>
    AuditEntry SignIn(string userId, string? details = null)
        => Record(new AuditEvent(Now(), userId, AuditAction.SignIn, Details: details));

    /// <summary>Records a sign-out.</summary>
    /// <param name="userId">User id signing out.</param>
    AuditEntry SignOut(string userId)
        => Record(new AuditEvent(Now(), userId, AuditAction.SignOut));

    /// <summary>Records a session refresh.</summary>
    /// <param name="userId">User id whose session refreshed.</param>
    AuditEntry SessionRefreshed(string userId)
        => Record(new AuditEvent(Now(), userId, AuditAction.SessionRefreshed));

    /// <summary>Records a session expiring unrecoverably.</summary>
    /// <param name="userId">User id whose session expired.</param>
    AuditEntry SessionExpired(string userId)
        => Record(new AuditEvent(Now(), userId, AuditAction.SessionExpired));

    /// <summary>Records a permission denial.</summary>
    /// <param name="userId">User id that was denied.</param>
    /// <param name="action">The action that was denied.</param>
    /// <param name="resource">Target resource id, or null.</param>
    AuditEntry PermissionDenied(string userId, CollectPermission action, string? resource = null)
        => Record(new AuditEvent(Now(), userId, AuditAction.PermissionDenied, RecordId: resource, Details: action.ToString()));

    /// <summary>Records a sync push of local records.</summary>
    /// <param name="userId">User id performing the sync.</param>
    /// <param name="count">Number of records pushed.</param>
    AuditEntry SyncPushed(string userId, int count)
        => Record(new AuditEvent(Now(), userId, AuditAction.SyncPush, Details: $"count={count}"));

    /// <summary>Records a sync pull of remote features.</summary>
    /// <param name="userId">User id performing the sync.</param>
    /// <param name="count">Number of features pulled.</param>
    AuditEntry SyncPulled(string userId, int count)
        => Record(new AuditEvent(Now(), userId, AuditAction.SyncPull, Details: $"count={count}"));

    /// <summary>Current UTC time the sink stamps events with.</summary>
    /// <returns>Now, in UTC.</returns>
    protected DateTimeOffset Now();
}
