// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Enterprise;

public enum CollectPermission
{
    CaptureRecords,

    EditRecords,

    DeleteRecords,

    SubmitRecords,

    ReviewRecords,

    ExportData,

    GenerateReports,

    ManageAssignments,

    ConfigureForms,
}

public sealed class PermissionDeniedException : InvalidOperationException
{
    public PermissionDeniedException(string userId, CollectPermission permission)
        : base($"User '{userId}' does not have permission '{permission}'.")
    {
        UserId = userId;
        Permission = permission;
    }

    public string UserId { get; }

    public CollectPermission Permission { get; }
}

public sealed record CollectRole
{
    public required string Name { get; init; }

    public IReadOnlySet<CollectPermission> Permissions { get; init; } = new HashSet<CollectPermission>();

    public static CollectRole Create(string name, params CollectPermission[] permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new CollectRole { Name = name, Permissions = new HashSet<CollectPermission>(permissions) };
    }
}

public sealed class DevicePrincipal
{
    private readonly HashSet<CollectPermission> _effective;

    public DevicePrincipal(string userId, IEnumerable<CollectRole> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(roles);

        UserId = userId;
        Roles = roles.ToList();
        _effective = Roles.SelectMany(r => r.Permissions).ToHashSet();
    }

    public string UserId { get; }

    public IReadOnlyList<CollectRole> Roles { get; }

    public IReadOnlySet<CollectPermission> EffectivePermissions => _effective;

    public bool Has(CollectPermission permission) => _effective.Contains(permission);

    public void Require(CollectPermission permission)
    {
        if (!Has(permission))
        {
            throw new PermissionDeniedException(UserId, permission);
        }
    }
}
