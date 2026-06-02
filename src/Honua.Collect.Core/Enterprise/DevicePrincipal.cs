namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// Actions whose availability can be gated by role on the device (BACKLOG E2).
/// </summary>
public enum CollectPermission
{
    /// <summary>Create new records.</summary>
    CaptureRecords,

    /// <summary>Edit existing records.</summary>
    EditRecords,

    /// <summary>Delete records.</summary>
    DeleteRecords,

    /// <summary>Submit records for review/sync.</summary>
    SubmitRecords,

    /// <summary>Approve or reject submitted records.</summary>
    ReviewRecords,

    /// <summary>Export captured data.</summary>
    ExportData,

    /// <summary>Generate per-record reports.</summary>
    GenerateReports,

    /// <summary>Create and dispatch assignments.</summary>
    ManageAssignments,

    /// <summary>Configure forms and app settings.</summary>
    ConfigureForms,
}

/// <summary>Raised when a principal attempts an action they are not permitted to perform.</summary>
public sealed class PermissionDeniedException : InvalidOperationException
{
    /// <summary>Creates the exception for a denied permission.</summary>
    /// <param name="userId">The principal that was denied.</param>
    /// <param name="permission">The permission that was required.</param>
    public PermissionDeniedException(string userId, CollectPermission permission)
        : base($"User '{userId}' does not have permission '{permission}'.")
    {
        UserId = userId;
        Permission = permission;
    }

    /// <summary>The principal that was denied.</summary>
    public string UserId { get; }

    /// <summary>The permission that was required.</summary>
    public CollectPermission Permission { get; }
}

/// <summary>
/// A named role granting a set of permissions.
/// </summary>
public sealed record CollectRole
{
    /// <summary>Role name (e.g. <c>field-worker</c>, <c>supervisor</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Permissions the role grants.</summary>
    public IReadOnlySet<CollectPermission> Permissions { get; init; } = new HashSet<CollectPermission>();

    /// <summary>Creates a role from a name and permissions.</summary>
    /// <param name="name">Role name.</param>
    /// <param name="permissions">Granted permissions.</param>
    /// <returns>The role.</returns>
    public static CollectRole Create(string name, params CollectPermission[] permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new CollectRole { Name = name, Permissions = new HashSet<CollectPermission>(permissions) };
    }
}

/// <summary>
/// The signed-in user and the roles they hold, used to enforce role-based access
/// on the device (BACKLOG E2). The effective permission set is the union of all
/// role permissions; the device checks it before offering or running a gated
/// action.
/// </summary>
public sealed class DevicePrincipal
{
    private readonly HashSet<CollectPermission> _effective;

    /// <summary>Creates a principal from a user id and roles.</summary>
    /// <param name="userId">Signed-in user id.</param>
    /// <param name="roles">Roles the user holds.</param>
    public DevicePrincipal(string userId, IEnumerable<CollectRole> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(roles);

        UserId = userId;
        Roles = roles.ToList();
        _effective = Roles.SelectMany(r => r.Permissions).ToHashSet();
    }

    /// <summary>The signed-in user id.</summary>
    public string UserId { get; }

    /// <summary>Roles the user holds.</summary>
    public IReadOnlyList<CollectRole> Roles { get; }

    /// <summary>The union of all role permissions.</summary>
    public IReadOnlySet<CollectPermission> EffectivePermissions => _effective;

    /// <summary>Whether the principal has a permission.</summary>
    /// <param name="permission">Permission to check.</param>
    /// <returns><see langword="true"/> when granted.</returns>
    public bool Has(CollectPermission permission) => _effective.Contains(permission);

    /// <summary>Enforces a permission, throwing <see cref="PermissionDeniedException"/> when absent.</summary>
    /// <param name="permission">Permission to require.</param>
    public void Require(CollectPermission permission)
    {
        if (!Has(permission))
        {
            throw new PermissionDeniedException(UserId, permission);
        }
    }
}
