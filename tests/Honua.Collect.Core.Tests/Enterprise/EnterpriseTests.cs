using Honua.Collect.Core.Enterprise;
using Honua.Collect.Core.Notifications;

namespace Honua.Collect.Core.Tests.Enterprise;

public class EnterpriseTests
{
    // --- E2 roles -------------------------------------------------------------

    [Fact]
    public void Principal_effective_permissions_are_the_union_of_roles()
    {
        var worker = CollectRole.Create("field-worker", CollectPermission.CaptureRecords, CollectPermission.SubmitRecords);
        var reviewer = CollectRole.Create("reviewer", CollectPermission.ReviewRecords);
        var principal = new DevicePrincipal("u1", [worker, reviewer]);

        Assert.True(principal.Has(CollectPermission.CaptureRecords));
        Assert.True(principal.Has(CollectPermission.ReviewRecords));
        Assert.False(principal.Has(CollectPermission.ConfigureForms));
        Assert.Equal(3, principal.EffectivePermissions.Count);
    }

    [Fact]
    public void Require_throws_for_a_missing_permission()
    {
        var principal = new DevicePrincipal("u1", [CollectRole.Create("worker", CollectPermission.CaptureRecords)]);
        principal.Require(CollectPermission.CaptureRecords); // ok

        var ex = Assert.Throws<PermissionDeniedException>(() => principal.Require(CollectPermission.DeleteRecords));
        Assert.Equal("u1", ex.UserId);
        Assert.Equal(CollectPermission.DeleteRecords, ex.Permission);
    }

    // --- E3 audit log ---------------------------------------------------------

    private static AuditEvent Event(string user, AuditAction action, string? record = null)
        => new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero), user, action, record);

    [Fact]
    public void Audit_log_appends_and_chains_entries()
    {
        var log = new AuditLog();
        var first = log.Append(Event("u1", AuditAction.RecordCreated, "r1"));
        var second = log.Append(Event("u1", AuditAction.RecordSubmitted, "r1"));

        Assert.Equal(0, first.Sequence);
        Assert.Equal(string.Empty, first.PreviousHash);
        Assert.Equal(first.Hash, second.PreviousHash); // chained
        Assert.Equal(second.Hash, log.HeadHash);
        Assert.True(log.Verify());
    }

    [Fact]
    public void Audit_log_filters_by_record()
    {
        var log = new AuditLog();
        log.Append(Event("u1", AuditAction.RecordCreated, "r1"));
        log.Append(Event("u1", AuditAction.RecordCreated, "r2"));
        log.Append(Event("u1", AuditAction.RecordSubmitted, "r1"));

        Assert.Equal(2, log.ForRecord("r1").Count);
    }

    [Fact]
    public void Tampering_with_an_entry_breaks_verification()
    {
        var log = new AuditLog();
        log.Append(Event("u1", AuditAction.RecordCreated, "r1"));
        log.Append(Event("u1", AuditAction.RecordApproved, "r1"));
        Assert.True(log.Verify());

        // Mutate an earlier entry's event in place (simulating a storage attack);
        // the chained hash no longer matches, so Verify detects it.
        var backing = (List<AuditEntry>)typeof(AuditLog)
            .GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(log)!;
        backing[0] = backing[0] with { Event = Event("attacker", AuditAction.RecordDeleted, "r1") };

        Assert.False(log.Verify());
    }

    // --- E6 push notifications ------------------------------------------------

    [Fact]
    public void New_assignment_notification_routes_to_the_assignment()
    {
        var action = PushNotificationRouter.Resolve(new PushNotification
        {
            Kind = PushNotificationKind.NewAssignment,
            Title = "New work",
            AssignmentId = "a1",
        });

        Assert.Equal(NotificationTarget.Assignment, action.Target);
        Assert.Equal("a1", action.EntityId);
    }

    [Fact]
    public void Rejected_record_routes_to_the_record_and_sync_routes_to_center()
    {
        var rejected = PushNotificationRouter.Resolve(new PushNotification
        {
            Kind = PushNotificationKind.RecordRejected,
            Title = "Rejected",
            RecordId = "r9",
        });
        Assert.Equal(NotificationTarget.Record, rejected.Target);
        Assert.Equal("r9", rejected.EntityId);

        var sync = PushNotificationRouter.Resolve(new PushNotification
        {
            Kind = PushNotificationKind.SyncCompleted,
            Title = "Synced",
        });
        Assert.Equal(NotificationTarget.SyncCenter, sync.Target);
        Assert.Null(sync.EntityId);
    }

    [Fact]
    public void Notification_without_required_id_falls_back_to_inbox()
    {
        var action = PushNotificationRouter.Resolve(new PushNotification
        {
            Kind = PushNotificationKind.NewAssignment,
            Title = "New work",
            // no AssignmentId
        });

        Assert.Equal(NotificationTarget.Inbox, action.Target);
    }
}
