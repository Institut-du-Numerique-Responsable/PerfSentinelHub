using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Access;

/// <summary>
///     Hub:Access compiled once from the validated configuration: patterns, role
///     permissions, grants, internal membership by subject, the admin sets and each
///     machine token's SHA-256 fingerprint, never the token, and each local account's parsed
///     hash and stamp. Always registered, so the
///     handlers shared by both modes can take it as a service: with access off it is a
///     disabled instance that builds nothing and whose every scope is Unrestricted.
/// </summary>
public sealed class AccessPolicy
{
    private const string InternalPrefix = "internal:";

    // AdminGroups, and every group the built-in admin role is granted to on '*'.
    private readonly HashSet<string> _adminGroups = new(StringComparer.Ordinal);

    private readonly HashSet<string> _admins = new(StringComparer.Ordinal);
    private readonly List<Grant> _grants = [];
    private readonly Dictionary<string, List<string>> _internalGroupsBySubject = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocalAccount> _localAccounts = new(StringComparer.Ordinal);
    private readonly List<MachineToken> _machineTokens = [];
    private readonly HashSet<string> _providerGroups = new(StringComparer.Ordinal);

    public AccessPolicy(IOptions<HubOptions> options)
    {
        var access = options.Value.Access;
        Enabled = access.Enabled;
        if (!Enabled) return;

        _admins.UnionWith(access.Admins);
        _adminGroups.UnionWith(access.AdminGroups);
        IndexInternalGroups(access.Groups);
        CompileGrants(access);

        // A session keeps only the provider groups the configuration names, so its cookie
        // grows with the configuration and not with the provider.
        _providerGroups.UnionWith(access.Grants.Select(grant => grant.Group).Concat(access.AdminGroups)
            .Where(group => !group.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase)));

        foreach (var token in access.MachineTokens)
            _machineTokens.Add(new MachineToken(
                token.Name,
                token.Group,
                SHA256.HashData(Encoding.UTF8.GetBytes(token.Token!)),
                token.ExpiresAt!.Value));

        // The stamp a local session carries: a rotated hash, or a removed account, ends its sessions.
        foreach (var account in access.LocalAccounts)
        {
            // The validator refuses such a hash at startup; an empty key here would be a bug, never an account.
            if (!LocalPassword.TryParse(account.PasswordHash!, out var salt, out var key))
                throw new InvalidOperationException(
                    $"Hub:Access:LocalAccounts {account.Name} has an unparsable PasswordHash.");
            var stamp = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(account.PasswordHash!))
                .AsSpan(0, 16));
            _localAccounts[account.Name] = new LocalAccount(salt, key, stamp);
        }
    }

    public bool Enabled { get; }

    public IReadOnlySet<string> ProviderGroups => _providerGroups;

    public bool HasLocalAccounts => _localAccounts.Count > 0;

    public LocalAccount? FindLocalAccount(string name)
    {
        return _localAccounts.GetValueOrDefault(name);
    }

    // A local ticket holds while access is on, its account is declared and its hash unchanged.
    public bool HoldsLocalTicket(ClaimsPrincipal principal)
    {
        return principal.FindFirst(AccessCaller.SubjectClaim)?.Value is { } subject &&
               subject.StartsWith(LocalSignIn.SubjectPrefix, StringComparison.Ordinal) &&
               _localAccounts.TryGetValue(subject[LocalSignIn.SubjectPrefix.Length..], out var account) &&
               principal.FindFirst(LocalSignIn.StampClaim)?.Value == account.Stamp;
    }

    // The request's caller with one permission. Off, or with no caller, it never narrows or
    // widens anything on its own: Unrestricted off, Nothing for a principal with no claims.
    public AccessScope Scope(HttpContext context, string permission)
    {
        if (!Enabled) return AccessScope.Unrestricted;
        return AccessCaller.Of(context) is { } caller ? Scope(caller, permission) : AccessScope.Nothing;
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

        // A grant on '*' carries no patterns and reaches everything.
        var granted = _grants
            .Where(grant => groups.Contains(grant.Group) && grant.Permissions.Contains(permission))
            .Select(grant => grant.Patterns)
            .ToList();
        if (granted.Exists(patterns => patterns is null)) return AccessScope.Unrestricted;
        return AccessScope.Of([.. granted.SelectMany(patterns => patterns!)]);
    }

    // Any permission anywhere: what /api/status and /api/sources ask of a caller.
    public bool ReachesAnything(HttpContext context)
    {
        return !Enabled || AccessPermissions.All.Any(permission => !Scope(context, permission).IsNothing);
    }

    // What makes sense at each kind of source, listed in /api/sources. Null with access off,
    // which leaves the payload as it was. Source-level permissions need the source as a whole.
    public IReadOnlyList<string>? Permissions(HttpContext context, SourceOptions source)
    {
        if (!Enabled) return null;
        string[] listed = source.Kind == SourceKinds.Daemon
            ?
            [
                AccessPermissions.FindingsRead, AccessPermissions.SourcesRead, AccessPermissions.AnalysesRun,
                AccessPermissions.AcksWrite
            ]
            : [AccessPermissions.TracesRead, AccessPermissions.AnalysesRun];
        return
        [
            .. listed.Where(permission =>
                source.Kind == SourceKinds.Daemon &&
                permission is AccessPermissions.SourcesRead or AccessPermissions.AnalysesRun
                    ? Scope(context, permission).HoldsAtSource(source.Id)
                    : Scope(context, permission).ReachesSource(source.Id))
        ];
    }

    // The sources the caller's /api/sources lists; a source outside it reads as unknown.
    public IReadOnlyList<SourceOptions> Sources(HttpContext context, IReadOnlyList<SourceOptions> sources)
    {
        return Enabled ? [.. sources.Where(source => Permissions(context, source) is { Count: > 0 })] : sources;
    }

    // Names only: never the token.
    public IEnumerable<string> ExpiredMachineTokens(DateTimeOffset now)
    {
        return _machineTokens.Where(token => token.ExpiresAt <= now).Select(token => token.Name);
    }

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

    private void IndexInternalGroups(IReadOnlyList<InternalGroupOptions> groups)
    {
        foreach (var group in groups)
            foreach (var member in group.Members)
            {
                if (!_internalGroupsBySubject.TryGetValue(member, out var subjectGroups))
                    _internalGroupsBySubject[member] = subjectGroups = [];
                subjectGroups.Add(InternalPrefix + group.Name);
            }
    }

    private void CompileGrants(AccessOptions access)
    {
        var projects = access.Projects.ToDictionary(
            project => project.Name,
            IReadOnlyList<AccessPattern> (project) => [.. project.Patterns.Select(AccessPattern.Compile)],
            StringComparer.Ordinal);
        var roles = new Dictionary<string, IReadOnlyList<string>>(AccessPermissions.BuiltInRoles,
            StringComparer.Ordinal);
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
    }

    private sealed record Grant(string Group, IReadOnlyList<AccessPattern>? Patterns, HashSet<string> Permissions);

    public sealed record LocalAccount(byte[] Salt, byte[] Key, string Stamp);

    private sealed record MachineToken(string Name, string Group, byte[] Fingerprint, DateTimeOffset ExpiresAt);
}
