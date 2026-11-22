// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Enterprise;

public interface IAuditSink
{
    Task<AuditEntry> RecordAsync(AuditEvent auditEvent, CancellationToken ct = default);

    Task<AuditEntry> SignInAsync(string userId, string? details = null, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.SignIn, Details: details), ct);

    Task<AuditEntry> SignOutAsync(string userId, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.SignOut), ct);

    Task<AuditEntry> SessionRefreshedAsync(string userId, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.SessionRefreshed), ct);

    Task<AuditEntry> SessionExpiredAsync(string userId, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.SessionExpired), ct);

    Task<AuditEntry> PermissionDeniedAsync(string userId, CollectPermission action, string? resource = null, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.PermissionDenied, RecordId: resource, Details: action.ToString()), ct);

    Task<AuditEntry> SyncPushedAsync(string userId, int count, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.SyncPush, Details: $"count={count}"), ct);

    Task<AuditEntry> SyncPulledAsync(string userId, int count, CancellationToken ct = default)
        => RecordAsync(new AuditEvent(Now(), userId, AuditAction.SyncPull, Details: $"count={count}"), ct);

    protected DateTimeOffset Now();
}
