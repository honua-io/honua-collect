namespace Honua.Collect.Core.Enterprise;

/// <summary>
/// A capability map: which roles grant which <see cref="CollectPermission"/>
/// actions, and (optionally) which resources those grants are scoped to (BACKLOG
/// E2). The map is data, not code — roles are never hardcoded into the
/// enforcement seam; they are supplied by the IdP as session claims and resolved
/// against this map.
/// </summary>
public sealed class CapabilityMap
{
    private readonly Dictionary<string, RoleCapabilities> _roles;

    private CapabilityMap(Dictionary<string, RoleCapabilities> roles) => _roles = roles;

    /// <summary>Begins building a capability map.</summary>
    /// <returns>A new builder.</returns>
    public static Builder Build() => new();

    /// <summary>
    /// Whether any of the supplied roles grant <paramref name="action"/> on
    /// <paramref name="resource"/>. A grant scoped to specific resources only
    /// applies to those resources; an unscoped grant applies to every resource.
    /// </summary>
    /// <param name="roles">Roles held by the session.</param>
    /// <param name="action">Action being attempted.</param>
    /// <param name="resource">Target resource id, or null for a non-resource action.</param>
    /// <returns><see langword="true"/> when at least one role grants the action.</returns>
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

    /// <summary>The set of actions any of the supplied roles can perform on any resource.</summary>
    /// <param name="roles">Roles held by the session.</param>
    /// <returns>The union of granted actions.</returns>
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

    /// <summary>Per-role capability grants.</summary>
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

    /// <summary>Fluent builder for a <see cref="CapabilityMap"/>.</summary>
    public sealed class Builder
    {
        private readonly Dictionary<string, RoleCapabilities> _roles = new(StringComparer.Ordinal);

        /// <summary>Grants a role one or more actions across all resources.</summary>
        /// <param name="role">Role name (matches a session role claim).</param>
        /// <param name="actions">Actions the role may perform.</param>
        /// <returns>This builder.</returns>
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

        /// <summary>Grants a role an action scoped to specific resources only.</summary>
        /// <param name="role">Role name.</param>
        /// <param name="action">Action being granted.</param>
        /// <param name="resources">Resource ids the grant is limited to.</param>
        /// <returns>This builder.</returns>
        public Builder RoleOn(string role, CollectPermission action, params string[] resources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(role);
            ArgumentNullException.ThrowIfNull(resources);
            GetOrAdd(role).Grant(action, resources);
            return this;
        }

        /// <summary>Produces the immutable map.</summary>
        /// <returns>The capability map.</returns>
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

/// <summary>
/// The enforcement seam the Presentation layer queries before offering or running
/// a gated action (BACKLOG E2). It resolves the current <see cref="AuthSession"/>'s
/// role claims against a <see cref="CapabilityMap"/>. Authorization fails closed:
/// no live session, or a session whose roles don't grant the action, denies.
/// </summary>
public interface IDeviceAuthorization
{
    /// <summary>Whether the current session may perform an action on a resource.</summary>
    /// <param name="action">Action being attempted.</param>
    /// <param name="resource">Target resource id (e.g. a form id or layer id), or null.</param>
    /// <returns><see langword="true"/> when permitted; <see langword="false"/> when denied (fail closed).</returns>
    bool Can(CollectPermission action, string? resource = null);

    /// <summary>
    /// Enforces an action, throwing <see cref="PermissionDeniedException"/> when denied.
    /// </summary>
    /// <param name="action">Action being attempted.</param>
    /// <param name="resource">Target resource id, or null.</param>
    void Require(CollectPermission action, string? resource = null);
}

/// <summary>
/// <see cref="IDeviceAuthorization"/> over the live session in an
/// <see cref="IAuthSessionStore"/> and a <see cref="CapabilityMap"/>. Reads the
/// session's role claims at call time, so a sign-out (or a refresh that changes
/// roles) takes effect immediately without re-wiring. Optionally records every
/// denial to an audit sink.
/// </summary>
public sealed class DeviceAuthorization : IDeviceAuthorization
{
    private readonly IAuthSessionStore _sessions;
    private readonly CapabilityMap _capabilities;
    private readonly IAuditSink? _audit;

    /// <summary>Creates the authorization service.</summary>
    /// <param name="sessions">Source of the current session and its role claims.</param>
    /// <param name="capabilities">The role-to-action capability map.</param>
    /// <param name="audit">Optional sink that records permission denials (BACKLOG E3).</param>
    public DeviceAuthorization(IAuthSessionStore sessions, CapabilityMap capabilities, IAuditSink? audit = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _audit = audit;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void Require(CollectPermission action, string? resource = null)
    {
        if (Can(action, resource))
        {
            return;
        }

        var userId = _sessions.Current?.UserId ?? "(anonymous)";
        _audit?.PermissionDenied(userId, action, resource);
        throw new PermissionDeniedException(userId, action);
    }
}
