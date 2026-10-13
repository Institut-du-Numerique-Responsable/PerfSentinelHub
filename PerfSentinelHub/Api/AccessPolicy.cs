using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Api;

/// <summary>
///     Hub:Access compiled once from the validated configuration: patterns, role
///     permissions, grants, internal membership by subject, the admin sets and each
///     machine token's SHA-256 fingerprint, never the token. Always registered, so the
///     handlers shared by both modes can take it as a service: with access off it is a
///     disabled instance that builds nothing and whose every scope is Unrestricted.
/// </summary>
public sealed class AccessPolicy
{
    private const string InternalPrefix = "internal:";

    private readonly HashSet<string> _admins = new(StringComparer.Ordinal);

    // AdminGroups, and every group the built-in admin role is granted to on '*'.
    private readonly HashSet<string> _adminGroups = new(StringComparer.Ordinal);
    private readonly List<Grant> _grants = [];
    private readonly Dictionary<string, List<string>> _internalGroupsBySubject = new(StringComparer.Ordinal);
    private readonly List<MachineToken> _machineTokens = [];

    public AccessPolicy(IOptions<HubOptions> options)
    {
        var access = options.Value.Access;
        Enabled = access.Enabled;
        if (!Enabled) return;

        _admins.UnionWith(access.Admins);
        _adminGroups.UnionWith(access.AdminGroups);
        foreach (var group in access.Groups)
            foreach (var member in group.Members)
            {
                if (!_internalGroupsBySubject.TryGetValue(member, out var groups))
                    _internalGroupsBySubject[member] = groups = [];
                groups.Add(InternalPrefix + group.Name);
            }

        var projects = access.Projects.ToDictionary(
            project => project.Name,
            IReadOnlyList<AccessPattern> (project) => [.. project.Patterns.Select(AccessPattern.Compile)],
            StringComparer.Ordinal);
        var roles = new Dictionary<string, IReadOnlyList<string>>(AccessPermissions.BuiltInRoles, StringComparer.Ordinal);
        foreach (var role in access.Roles)
            roles[role.Name] = role.Permissions;
        foreach (var grant in access.Grants)
        {
            // Patterns null stands for '*', which also covers records no project matches.
            var patterns = grant.Project == "*" ? null : projects[grant.Project];
            _grants.Add(new Grant(grant.Group, patterns, roles[grant.Role].ToHashSet(StringComparer.Ordinal)));
            if (patterns is null && grant.Role == AccessPermissions.AdminRole)
                _adminGroups.Add(grant.Group);
        }

        foreach (var token in access.MachineTokens)
            _machineTokens.Add(new MachineToken(
                token.Name,
                token.Group,
                SHA256.HashData(Encoding.UTF8.GetBytes(token.Token!)),
                token.ExpiresAt!.Value));
    }

    public bool Enabled { get; }

    // A person's frozen provider groups and the internal groups listing their subject, read
    // live; a machine token's configured group alone. A provider never hands out internal:.
    public IReadOnlySet<string> EffectiveGroups(AccessCaller caller)
    {
        if (caller.Kind == AccessCaller.MachineToken)
            return _machineTokens
                .Where(token => AccessCaller.MachinePrefix + token.Name == caller.Subject)
                .Select(token => token.Group)
                .ToHashSet(StringComparer.Ordinal);

        var groups = caller.ProviderGroups
            .Where(group => !group.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.Ordinal);
        if (_internalGroupsBySubject.TryGetValue(caller.Subject, out var internalGroups))
            groups.UnionWith(internalGroups);
        return groups;
    }

    // A browser session only: a terminal token keeps the person's grants, not the whole Hub.
    public bool IsAdmin(AccessCaller caller)
    {
        return Enabled && caller.Kind == AccessCaller.Session &&
               (_admins.Contains(caller.Subject) || _adminGroups.Overlaps(EffectiveGroups(caller)));
    }

    public AccessScope Scope(AccessCaller caller, string permission)
    {
        if (!Enabled || IsAdmin(caller)) return AccessScope.Unrestricted;

        var groups = EffectiveGroups(caller);
        if (caller.Kind == AccessCaller.MachineToken)
        {
            // Its group's grants, AdminGroups included, intersected with what a machine may hold.
            if (!AccessPermissions.MachineTokenPermissions.Contains(permission)) return AccessScope.Nothing;
            if (_adminGroups.Overlaps(groups)) return AccessScope.Unrestricted;
        }

        if (permission == AccessPermissions.AccessAdmin) return AccessScope.Nothing;

        var patterns = new List<AccessPattern>();
        foreach (var grant in _grants.Where(grant => groups.Contains(grant.Group) && grant.Permissions.Contains(permission)))
        {
            if (grant.Patterns is null) return AccessScope.Unrestricted;
            patterns.AddRange(grant.Patterns);
        }

        return AccessScope.Of(patterns);
    }

    // The name of the machine token whose fingerprint this is, unless it has expired. Every
    // fingerprint is compared in fixed time with no early exit, as the import key is.
    public string? MachineTokenName(string token, DateTimeOffset now)
    {
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        string? name = null;
        foreach (var machineToken in _machineTokens)
            if (CryptographicOperations.FixedTimeEquals(machineToken.Fingerprint, fingerprint) &&
                machineToken.ExpiresAt > now)
                name = machineToken.Name;
        return name;
    }

    private sealed record Grant(string Group, IReadOnlyList<AccessPattern>? Patterns, HashSet<string> Permissions);

    private sealed record MachineToken(string Name, string Group, byte[] Fingerprint, DateTimeOffset ExpiresAt);
}
