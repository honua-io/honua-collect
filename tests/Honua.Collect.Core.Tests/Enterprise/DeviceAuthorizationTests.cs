using Honua.Collect.Core.Enterprise;

namespace Honua.Collect.Core.Tests.Enterprise;

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
        .Role("field-worker", CollectPermission.CaptureRecords, CollectPermission.EditRecords, CollectPermission.SubmitRecords)
        .Role("supervisor", CollectPermission.ReviewRecords, CollectPermission.DeleteRecords, CollectPermission.ExportData)
        .RoleOn("layer-editor", CollectPermission.EditRecords, "layer.parcels", "layer.permits")
        .Create();

    [Fact]
    public void Role_grants_its_capabilities()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker"));
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.True(authz.Can(CollectPermission.CaptureRecords));
        Assert.True(authz.Can(CollectPermission.SubmitRecords));
    }

    [Fact]
    public void Role_denies_capabilities_it_does_not_grant()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker"));
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.False(authz.Can(CollectPermission.DeleteRecords));
        Assert.False(authz.Can(CollectPermission.ExportData));
    }

    [Fact]
    public void Union_of_roles_grants_all_their_capabilities()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker", "supervisor"));
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.True(authz.Can(CollectPermission.CaptureRecords)); // from field-worker
        Assert.True(authz.Can(CollectPermission.ExportData));     // from supervisor
    }

    [Fact]
    public void Fails_closed_when_no_session()
    {
        var store = new AuthSessionStore();
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.False(authz.Can(CollectPermission.CaptureRecords));
        Assert.Throws<PermissionDeniedException>(() => authz.Require(CollectPermission.CaptureRecords));
    }

    [Fact]
    public void Fails_closed_when_session_holds_no_matching_role()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("unknown-role"));
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.False(authz.Can(CollectPermission.CaptureRecords));
    }

    [Fact]
    public void Resource_scoped_grant_applies_only_to_named_resources()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("layer-editor"));
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.True(authz.Can(CollectPermission.EditRecords, "layer.parcels"));
        Assert.True(authz.Can(CollectPermission.EditRecords, "layer.permits"));
        Assert.False(authz.Can(CollectPermission.EditRecords, "layer.secret"));
        // A resource-scoped grant does not satisfy an unscoped check.
        Assert.False(authz.Can(CollectPermission.EditRecords));
    }

    [Fact]
    public void Unscoped_grant_applies_to_any_resource()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker"));
        var authz = new DeviceAuthorization(store, StandardMap());

        Assert.True(authz.Can(CollectPermission.EditRecords, "any.resource"));
        Assert.True(authz.Can(CollectPermission.EditRecords));
    }

    [Fact]
    public async Task Require_records_a_denial_to_the_audit_sink()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker"));
        var audit = new AuditTrail(new InMemoryAuditStore());
        var authz = new DeviceAuthorization(store, StandardMap(), audit);

        Assert.Throws<PermissionDeniedException>(() => authz.Require(CollectPermission.DeleteRecords));

        var trail = await audit.QueryAsync();
        var denial = Assert.Single(trail);
        Assert.Equal(AuditAction.PermissionDenied, denial.Event.Action);
        Assert.Equal("u1", denial.Event.UserId);
    }

    [Fact]
    public void Sign_out_revokes_authorization_immediately()
    {
        var store = new AuthSessionStore();
        store.Set(SessionWithRoles("field-worker"));
        var authz = new DeviceAuthorization(store, StandardMap());
        Assert.True(authz.Can(CollectPermission.CaptureRecords));

        store.Set(null);
        Assert.False(authz.Can(CollectPermission.CaptureRecords));
    }

    [Fact]
    public void Effective_actions_is_the_union_across_roles()
    {
        var map = StandardMap();
        var effective = map.EffectiveActions(new[] { "field-worker", "supervisor" });

        Assert.Contains(CollectPermission.CaptureRecords, effective);
        Assert.Contains(CollectPermission.ExportData, effective);
        Assert.DoesNotContain(CollectPermission.ConfigureForms, effective);
    }
}
