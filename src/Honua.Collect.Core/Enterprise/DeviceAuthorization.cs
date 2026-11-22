// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Enterprise;

public sealed class CapabilityMap
{
    private readonly Dictionary<string, RoleCapabilities> _roles;

    private CapabilityMap(Dictionary<string, RoleCapabilities> roles) => _roles = roles;

    public static Builder Build() => new();

    public bool Grants(IEnumerable<string> roles, CollectPermission action, string? resource)
    {
        ArgumentNullException.ThrowIfNull(roles);
        foreach (var role in roles)
        {
            if (role is not null && _roles.TryGetValue(role, out var caps) && caps.Grants(action, resource))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlySet<CollectPermission> EffectiveActions(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var effective = new HashSet<CollectPermission>();
        foreach (var role in roles)
        {
            if (role is not null && _roles.TryGetValue(role, out var caps))
            {
                effective.UnionWith(caps.Actions);
            }
        }

        return effective;
    }

    private sealed class RoleCapabilities
    {
        // action -> resource scope. A null scope set means "all resources".
        private readonly Dictionary<CollectPermission, HashSet<string>?> _grants = new();

        public IEnumerable<CollectPermission> Actions => _grants.Keys;

        public void Grant(CollectPermission action, IReadOnlyCollection<string>? resources)
        {
            if (resources is null || resources.Count == 0)
            {
                // Unscoped grant wins over (and replaces) any prior resource scoping.
                _grants[action] = null;
                return;
            }

            if (!_grants.TryGetValue(action, out var existing))
            {
                _grants[action] = new HashSet<string>(resources, StringComparer.Ordinal);
                return;
            }

            // An existing unscoped grant already covers everything.
            existing?.UnionWith(resources);
        }

        public bool Grants(CollectPermission action, string? resource)
        {
            if (!_grants.TryGetValue(action, out var scope))
            {
                return false;
            }

            // Unscoped grant: applies to every resource.
            if (scope is null)
            {
                return true;
            }

            // Scoped grant: a resource-targeted check must name an in-scope resource.
            return resource is not null && scope.Contains(resource);
        }
    }

    public sealed class Builder
    {
        private readonly Dictionary<string, RoleCapabilities> _roles = new(StringComparer.Ordinal);

        public Builder Role(string role, params CollectPermission[] actions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(role);
            ArgumentNullException.ThrowIfNull(actions);
            var caps = GetOrAdd(role);
            foreach (var action in actions)
            {
                caps.Grant(action, null);
            }

            return this;
        }

        public Builder RoleOn(string role, CollectPermission action, params string[] resources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(role);
            ArgumentNullException.ThrowIfNull(resources);
            GetOrAdd(role).Grant(action, resources);
            return this;
        }

        public CapabilityMap Create() => new(_roles);

        private RoleCapabilities GetOrAdd(string role)
        {
            if (!_roles.TryGetValue(role, out var caps))
            {
                caps = new RoleCapabilities();
                _roles[role] = caps;
            }

            return caps;
        }
    }
}

public interface IDeviceAuthorization
{
    bool Can(CollectPermission action, string? resource = null);

    void Require(CollectPermission action, string? resource = null);

    Task RequireAsync(CollectPermission action, string? resource = null, CancellationToken ct = default);
}

public sealed class DeviceAuthorization : IDeviceAuthorization
{
    private readonly IAuthSessionStore _sessions;
    private readonly CapabilityMap _capabilities;
    private readonly IAuditSink? _audit;

    public DeviceAuthorization(IAuthSessionStore sessions, CapabilityMap capabilities, IAuditSink? audit = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _audit = audit;
    }

    public bool Can(CollectPermission action, string? resource = null)
    {
        var session = _sessions.Current;
        if (session is null)
        {
            // Fail closed: no identity, no authority.
            return false;
        }

        return _capabilities.Grants(session.Roles, action, resource);
    }

    public void Require(CollectPermission action, string? resource = null)
    {
        if (Can(action, resource))
        {
            return;
        }

        var userId = _sessions.Current?.UserId ?? "(anonymous)";
        if (_audit is not null)
        {
            _ = RecordDenialBestEffortAsync(_audit, userId, action, resource);
        }

        throw new PermissionDeniedException(userId, action);
    }

    public async Task RequireAsync(CollectPermission action, string? resource = null, CancellationToken ct = default)
    {
        if (Can(action, resource))
        {
            return;
        }

        var userId = _sessions.Current?.UserId ?? "(anonymous)";
        if (_audit is not null)
        {
            await _audit.PermissionDeniedAsync(userId, action, resource, ct).ConfigureAwait(false);
        }

        throw new PermissionDeniedException(userId, action);
    }

    private static async Task RecordDenialBestEffortAsync(
        IAuditSink audit,
        string userId,
        CollectPermission action,
        string? resource)
    {
        try
        {
            await audit.PermissionDeniedAsync(userId, action, resource).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fire-and-forget path for synchronous Require: never surface unobserved task exceptions.
        }
    }
}
