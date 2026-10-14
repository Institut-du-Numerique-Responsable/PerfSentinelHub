using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    // Each admin list stops here, with truncated set.
    private const int MaxAccessEntries = 200;

    /// <summary>
    ///     GET /api/access, mapped with Hub:Access:Enabled only. Any caller reads who they
    ///     are and what they reach, a caller no grant reaches included, since this page is
    ///     how they find their subject to give an administrator. An admin also reads the
    ///     whole configuration, what no project covers and the copies whose groupings
    ///     disagree on a project attribute. It reads; rights live in configuration only.
    /// </summary>
    private static async Task<IResult> GetAccessAsync(
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> hubOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var options = hubOptions.Value;
        var configured = options.Access;
        // With access on, the cookie event refuses a principal without the access claims.
        var caller = AccessCaller.Of(context)!;
        var machine = caller.Kind == AccessCaller.MachineToken;
        var admin = access.IsAdmin(caller);
        var groups = access.EffectiveGroups(caller);
        var now = timeProvider.GetUtcNow();

        var reach = new List<AccessReach>();
        // Admins and AdminGroups hold the built-in admin role on '*'. A terminal token is never an
        // admin, and a machine token keeps findings.read from it.
        if (admin && configured.Admins.Contains(caller.Subject, StringComparer.Ordinal))
            reach.Add(new AccessReach(null, "*", AccessPermissions.AdminRole, AccessPermissions.All));
        if (admin || machine)
            reach.AddRange(configured.AdminGroups.Where(groups.Contains).Select(group =>
                new AccessReach(group, "*", AccessPermissions.AdminRole, Effective(AccessPermissions.All))));
        var roles = configured.Roles.ToDictionary(role => role.Name, role => role.Permissions, StringComparer.Ordinal);
        reach.AddRange(configured.Grants.Where(grant => groups.Contains(grant.Group)).Select(grant =>
            new AccessReach(grant.Group, grant.Project, grant.Role,
                Effective(AccessPermissions.BuiltInRoles.GetValueOrDefault(grant.Role) ?? roles[grant.Role]))));

        return TypedResults.Ok(new AccessResponse(
            KnownIdentity(context.Request, options.Analysis),
            caller.Subject,
            caller.Kind,
            machine
                ? configured.MachineTokens.First(token => AccessCaller.MachinePrefix + token.Name == caller.Subject)
                    .ExpiresAt!.Value.ToUnixTimeMilliseconds()
                : (await context.AuthenticateAsync()).Properties?.ExpiresUtc?.ToUnixTimeMilliseconds(),
            admin,
            [
                .. groups
                    .Select(group => group.StartsWith("internal:", StringComparison.Ordinal)
                        ? new AccessGroup(group["internal:".Length..], "internal")
                        : new AccessGroup(group, "provider"))
                    .OrderBy(group => group.Origin == "internal")
                    .ThenBy(group => group.Name, StringComparer.Ordinal)
            ],
            reach,
            [.. configured.Projects.Where(project => reach.Exists(entry => entry.Project == project.Name))],
            admin ? Configuration(configured, now) : null,
            admin ? await UnmatchedAsync(database, options, now, cancellationToken) : null,
            admin ? await ConflictsAsync(database, options, now, cancellationToken) : null));

        // What the grant gives this caller: findings.read alone to a machine, never access.admin
        // outside an admin's session.
        IReadOnlyList<string> Effective(IReadOnlyList<string> permissions)
        {
            return
            [
                .. permissions.Where(permission =>
                    (!machine || AccessPermissions.MachineTokenPermissions.Contains(permission)) &&
                    (permission != AccessPermissions.AccessAdmin || admin))
            ];
        }
    }

    private static AccessConfiguration Configuration(AccessOptions configured, DateTimeOffset now)
    {
        return new AccessConfiguration(
            configured.Projects,
            [
                .. AccessPermissions.BuiltInRoles.Select(role => new AccessRole(role.Key, role.Value, true)),
                .. configured.Roles.Select(role => new AccessRole(role.Name, role.Permissions, false))
            ],
            configured.Grants,
            configured.Groups,
            configured.Admins,
            configured.AdminGroups,
            // Never the token, nor its fingerprint.
            [
                .. configured.MachineTokens.Select(token => new AccessMachineToken(
                    token.Name, token.Group, token.ExpiresAt!.Value.ToUnixTimeMilliseconds(), token.ExpiresAt > now))
            ]);
    }

    private static async Task<AccessUnmatched> UnmatchedAsync(
        HubDatabase database,
        HubOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var patterns = ProjectPatterns(options.Access);
        var copies = await database.QueryUnmatchedAsync(
            AccessScope.Of(patterns), Since(options, now), MaxAccessEntries + 1, cancellationToken);
        // A source no pattern names in its source part. A trace backend holds no copy to count.
        var sources = options.Sources
            .Where(source => !patterns.Any(pattern => pattern.Source is not null && pattern.MatchesSource(source.Id)))
            .Select(source => new UnmatchedSource(source.Id,
                source.Kind == SourceKinds.Daemon ? copies.CountsBySource.GetValueOrDefault(source.Id) : null))
            .OrderByDescending(source => source.Findings ?? -1)
            .ThenBy(source => source.SourceId, StringComparer.Ordinal)
            .ToList();
        return new AccessUnmatched(
            [.. copies.Services.Take(MaxAccessEntries)],
            [.. copies.Attributes.Take(MaxAccessEntries)],
            [.. sources.Take(MaxAccessEntries)],
            copies.Services.Count > MaxAccessEntries || copies.Attributes.Count > MaxAccessEntries ||
            sources.Count > MaxAccessEntries);
    }

    private static async Task<AccessConflicts> ConflictsAsync(
        HubDatabase database,
        HubOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var conflicts = await database.QueryConflictsAsync(
            AccessScope.Of(ProjectPatterns(options.Access)), Since(options, now), MaxAccessEntries + 1,
            cancellationToken);
        return new AccessConflicts([.. conflicts.Take(MaxAccessEntries)], conflicts.Count > MaxAccessEntries);
    }

    private static IReadOnlyList<AccessPattern> ProjectPatterns(AccessOptions configured)
    {
        return [.. configured.Projects.SelectMany(project => project.Patterns).Select(AccessPattern.Compile)];
    }

    // Copies the daily purge has not reached yet are past retention all the same.
    private static long Since(HubOptions options, DateTimeOffset now)
    {
        return (now - options.Retention).ToUnixTimeMilliseconds();
    }
}
