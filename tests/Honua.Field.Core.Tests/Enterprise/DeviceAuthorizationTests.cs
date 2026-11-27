// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.Core.Enterprise;

namespace Honua.Field.Core.Tests.Enterprise;

public class DeviceAuthorizationTests
{
    private static AuthSession SessionWithRoles(params string[] roles) => new()
    {
        UserId = "u1",
        AccessToken = "at",
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
        Roles = new HashSet<string>(roles, StringComparer.Ordinal),
    };

    private static CapabilityMap StandardMap() => CapabilityMap.Build()
        .Role("field-worker", FieldPermission.CaptureRecords, FieldPermission.EditRecords, FieldPermission.SubmitRecords)
        .Role("supervisor", FieldPermission.ReviewRecords, FieldPermission.DeleteRecords, FieldPermission.ExportData)
        .RoleOn("layer-editor", FieldPermission.EditRecords, "layer.parcels", "layer.permits")
        .Create();

    [Fact]
    public void Role_grants_its_capabilities()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker"));
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.True(authz.Can(FieldPermission.CaptureRecords));
        Assert.True(authz.Can(FieldPermission.SubmitRecords));
    }

    [Fact]
    public void Role_denies_capabilities_it_does_not_grant()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker"));
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.False(authz.Can(FieldPermission.DeleteRecords));
        Assert.False(authz.Can(FieldPermission.ExportData));
    }

    [Fact]
    public void Union_of_roles_grants_all_their_capabilities()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker", "supervisor"));
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.True(authz.Can(FieldPermission.CaptureRecords)); // from field-worker
        Assert.True(authz.Can(FieldPermission.ExportData));     // from supervisor
    }

    [Fact]
    public void Fails_closed_when_no_session()
    {
        var store = new AuthSessionStore();
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.False(authz.Can(FieldPermission.CaptureRecords));
        Assert.Throws<PermissionDeniedException>(() => authz.Require(FieldPermission.CaptureRecords));
    }

    [Fact]
    public void Fails_closed_when_session_holds_no_matching_role()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("unknown-role"));
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.False(authz.Can(FieldPermission.CaptureRecords));
    }

    [Fact]
    public void Resource_scoped_grant_applies_only_to_named_resources()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("layer-editor"));
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.True(authz.Can(FieldPermission.EditRecords, "layer.parcels"));
        Assert.True(authz.Can(FieldPermission.EditRecords, "layer.permits"));
        Assert.False(authz.Can(FieldPermission.EditRecords, "layer.secret"));
        // A resource-scoped grant does not satisfy an unscoped check.
        Assert.False(authz.Can(FieldPermission.EditRecords));
    }

    [Fact]
    public void Unscoped_grant_applies_to_any_resource()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker"));
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.True(authz.Can(FieldPermission.EditRecords, "any.resource"));
        Assert.True(authz.Can(FieldPermission.EditRecords));
    }

    [Fact]
    public async Task RequireAsync_records_a_denial_to_the_audit_sink()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker"));
        var audit = new AuditTrail(new InMemoryAuditStore());
        var authz = new DeviceAuthorization(store, StandardMap(), audit);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => authz.RequireAsync(FieldPermission.DeleteRecords));

        var trail = await audit.QueryAsync();
        var denial = Assert.Single(trail);
        Assert.Equal(AuditAction.PermissionDenied, denial.Event.Action);
        Assert.Equal("u1", denial.Event.UserId);
    }

    [Fact]
    public async Task RequireAsync_succeeds_without_recording_denial_when_permitted()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker"));
        var audit = new AuditTrail(new InMemoryAuditStore());
        var authz = new DeviceAuthorization(store, StandardMap(), audit);

        await authz.RequireAsync(FieldPermission.CaptureRecords);

        Assert.Empty(await audit.QueryAsync());
    }

    [Fact]
    public void Require_throws_without_blocking_when_audit_sink_is_present()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker"));
        var audit = new AuditTrail(new InMemoryAuditStore());
        var authz = new DeviceAuthorization(store, StandardMap(), audit);

        Assert.Throws<PermissionDeniedException>(() => authz.Require(FieldPermission.DeleteRecords));
    }

    [Fact]
    public void Sign_out_revokes_authorization_immediately()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker"));
        var authz = new DeviceAuthorization(store, StandardMap());
        Assert.True(authz.Can(FieldPermission.CaptureRecords));

        store.Set(null);
        Assert.False(authz.Can(FieldPermission.CaptureRecords));
    }

    [Fact]
    public void Effective_actions_is_the_union_across_roles()
    {
        var map = StandardMap();
        var effective = map.EffectiveActions(new[] { "field-worker", "supervisor" });

        Assert.Contains(FieldPermission.CaptureRecords, effective);
        Assert.Contains(FieldPermission.ExportData, effective);
        Assert.DoesNotContain(FieldPermission.ConfigureForms, effective);
    }
}
