// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Field.Core.Enterprise;

/// <summary>
/// Records security- and data-relevant events to the durable audit trail (BACKLOG
/// E3). The sink owns sequencing and timestamping; callers describe what happened.
/// Convenience methods cover the lifecycle events the app raises; arbitrary events
/// go through <see cref="RecordAsync(AuditEvent, CancellationToken)"/>.
/// </summary>
public interface IAuditSink
{
    /// <summary>Asynchronously records a fully-formed event (its details are scrubbed before persisting).</summary>
    /// <param name="auditEvent">Event to record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted entry, including its assigned sequence and chain hash.</returns>
    Task<AuditEntry> RecordAsync(AuditEvent auditEvent, CancellationToken ct = default);

    /// <summary>Records a successful sign-in.</summary>
    /// <param name="userId">Authenticated user id.</param>
    /// <param name="details">Optional non-secret context (provider, method).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuditEntry> SignInAsync(string userId, string? details = null, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.SignIn, Details: details), ct);

    /// <summary>Records a sign-out.</summary>
    /// <param name="userId">User id signing out.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuditEntry> SignOutAsync(string userId, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.SignOut), ct);

    /// <summary>Records a session refresh.</summary>
    /// <param name="userId">User id whose session refreshed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuditEntry> SessionRefreshedAsync(string userId, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.SessionRefreshed), ct);

    /// <summary>Records a session expiring unrecoverably.</summary>
    /// <param name="userId">User id whose session expired.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuditEntry> SessionExpiredAsync(string userId, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.SessionExpired), ct);

    /// <summary>Records a permission denial.</summary>
    /// <param name="userId">User id that was denied.</param>
    /// <param name="action">The action that was denied.</param>
    /// <param name="resource">Target resource id, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuditEntry> PermissionDeniedAsync(string userId, FieldPermission action, string? resource = null, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.PermissionDenied, RecordId: resource, Details: action.ToString()), ct);

    /// <summary>Records a sync push of local records.</summary>
    /// <param name="userId">User id performing the sync.</param>
    /// <param name="count">Number of records pushed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuditEntry> SyncPushedAsync(string userId, int count, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.SyncPush, Details: $"count={count}"), ct);

    /// <summary>Records a sync pull of remote features.</summary>
    /// <param name="userId">User id performing the sync.</param>
    /// <param name="count">Number of features pulled.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuditEntry> SyncPulledAsync(string userId, int count, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.SyncPull, Details: $"count={count}"), ct);

    /// <summary>Current UTC time the sink stamps events with.</summary>
    /// <returns>Now, in UTC.</returns>
    protected DateTimeOffset Now();
}
