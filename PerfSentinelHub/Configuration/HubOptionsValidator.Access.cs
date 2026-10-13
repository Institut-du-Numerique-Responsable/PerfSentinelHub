using System.Text.RegularExpressions;

namespace PerfSentinelHub.Configuration;

// Hub:Access, rules A0 to A12 (A3 ships with enforcement). Nothing is read while access is
// off, so nothing is validated either. Each message names the key and its index, and none
// echoes a machine token.
public sealed partial class HubOptionsValidator
{
    private const string InternalPrefix = "internal:";
    private const string MachinePrefix = "machine:";
    private const string LocalPrefix = "local:";
    private const string Wildcard = "*";
    private const int MaxValueLength = 256;

    private const string NameShape =
        "must be 1 to 64 ASCII letters, digits, '.', '_' or '-', unique with case ignored";

    private static void ValidateAccessSettings(HubOptions options, DateTimeOffset now, List<string> errors)
    {
        var access = options.Access;
        if (!access.Enabled) return;

        // A0, until enforcement ships: a Hub that believes it filters would serve everyone everything.
        errors.Add("Hub:Access:Enabled is not available in this build yet.");
        if (!options.Auth.Enabled)
            errors.Add("Hub:Access:Enabled requires Hub:Auth:Enabled: access control needs a signed-in caller.");
        if (options.AckRelay.TrustIdentityHeader)
            errors.Add("With Hub:Access:Enabled only a Hub session or token names a caller; "
                       + "Hub:AckRelay:TrustIdentityHeader cannot be set.");
        if (access.Admins.Count == 0 && access.AdminGroups.Count == 0)
            errors.Add("Hub:Access needs at least one entry in Admins or AdminGroups.");

        ValidateSubjects(access, errors);
        var groups = ValidateInternalGroups(access, errors);
        ValidateGroupReferences(options, groups, errors);
        var projects = ValidateProjects(access, options.Sources, errors);
        var roles = ValidateRoles(access, errors);
        ValidateGrants(access, projects, roles, errors);
        ValidateMachineTokens(options, groups, now, errors);
    }

    // A6: Admins and Members hold subjects. A machine token is no person, and local
    // accounts arrive with their own release.
    private static void ValidateSubjects(AccessOptions access, List<string> errors)
    {
        var subjects = access.Admins.Select((subject, i) => ($"Hub:Access:Admins:{i}", subject))
            .Concat(access.Groups.SelectMany((group, g) =>
                group.Members.Select((subject, m) => ($"Hub:Access:Groups:{g}:Members:{m}", subject))));
        foreach (var (path, subject) in subjects)
            if (!IsValidValue(subject))
                errors.Add($"{path} must be a subject of 1 to {MaxValueLength} characters with no control character.");
            else if (subject.StartsWith(MachinePrefix, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{path}: a machine token is never a person; it gets its group through "
                           + "Hub:Access:MachineTokens.");
            else if (subject.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{path}: local accounts are not available in this build yet.");
    }

    // A7. Returns each declared group's members by name, for the references and A12.
    private static Dictionary<string, IReadOnlyList<string>> ValidateInternalGroups(
        AccessOptions access,
        List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        for (var i = 0; i < access.Groups.Count; i++)
        {
            var group = access.Groups[i];
            if (!IsValidName(group.Name) || !names.Add(group.Name))
                errors.Add($"Hub:Access:Groups:{i}:Name {NameShape}.");
            groups.TryAdd(group.Name, group.Members);
        }

        return groups;
    }

    // A4 and A8, on every place a group is named.
    private static void ValidateGroupReferences(
        HubOptions options,
        Dictionary<string, IReadOnlyList<string>> groups,
        List<string> errors)
    {
        var access = options.Access;
        // A grant without a group is A11's to report.
        var references = access.Grants.Select((grant, i) => ($"Hub:Access:Grants:{i}:Group", grant.Group))
            .Where(reference => !string.IsNullOrWhiteSpace(reference.Group))
            .Concat(access.AdminGroups.Select((group, i) => ($"Hub:Access:AdminGroups:{i}", group)));
        var refersToProvider = false;
        foreach (var (path, reference) in references)
            refersToProvider |= ValidateGroupReference(path, reference, groups, errors);
        if (refersToProvider && string.IsNullOrWhiteSpace(options.Auth.GroupsClaim))
            errors.Add("Hub:Auth:GroupsClaim must name a userinfo field when Hub:Access refers to a provider group.");
    }

    // True for a provider group: the userinfo must then carry a groups field to match it against.
    private static bool ValidateGroupReference(
        string path,
        string reference,
        Dictionary<string, IReadOnlyList<string>> groups,
        List<string> errors)
    {
        if (reference.StartsWith(InternalPrefix, StringComparison.Ordinal))
        {
            if (!groups.ContainsKey(reference[InternalPrefix.Length..]))
                errors.Add($"{path}: '{reference}' names no group in Hub:Access:Groups.");
            return false;
        }

        if (reference.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase))
            errors.Add($"{path}: write the internal: prefix in lower case.");
        else if (!IsValidValue(reference) || reference == Wildcard)
            errors.Add($"{path} must be internal:<name> or a provider group of 1 to {MaxValueLength} characters "
                       + "with no control character, '*' excepted.");
        return true;
    }

    // A9. Returns the declared project names.
    private static HashSet<string> ValidateProjects(
        AccessOptions access,
        IReadOnlyList<SourceOptions> sources,
        List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < access.Projects.Count; i++)
        {
            var project = access.Projects[i];
            if (!IsValidName(project.Name) || !names.Add(project.Name))
                errors.Add($"Hub:Access:Projects:{i}:Name {NameShape}.");
            declared.Add(project.Name);
            if (project.Patterns.Count == 0)
                errors.Add($"Hub:Access:Projects:{i} needs at least one pattern.");
            for (var j = 0; j < project.Patterns.Count; j++)
                ValidatePattern($"Hub:Access:Projects:{i}:Patterns:{j}", project.Patterns[j], sources, errors);
        }

        return declared;
    }

    private static void ValidatePattern(
        string path,
        PatternOptions pattern,
        IReadOnlyList<SourceOptions> sources,
        List<string> errors)
    {
        if (pattern is { Source: null, Service: null, Attribute: null })
            errors.Add($"{path} must set Source, Service or Attribute.");

        if (pattern.Source is { } source)
        {
            if (!IsValidPart(source) || !IsValidName(source.TrimEnd('*')))
                errors.Add($"{path}:Source must be a source id, or a prefix of one ending in a single '*'.");
            else if (!sources.Any(configured => source.EndsWith('*')
                         ? configured.Id.StartsWith(source[..^1], StringComparison.Ordinal)
                         : configured.Id == source))
                errors.Add($"{path}:Source '{source}' matches no configured source.");
        }

        if (pattern.Service is { } service && !IsValidPart(service))
            errors.Add($"{path}:Service must be 1 to {MaxValueLength} characters with no control character, "
                       + "exact or a prefix ending in a single '*'.");

        if (pattern.Attribute is { } attribute &&
            (attribute.Split('=', 2) is not [var key, var value] ||
             !IsValidValue(key) || key.Contains('*', StringComparison.Ordinal) || !IsValidPart(value)))
            errors.Add($"{path}:Attribute must read key=value: a key of 1 to {MaxValueLength} characters with no "
                       + "control character and no '*', and a value of 1 to 256, exact or a prefix ending in a "
                       + "single '*'.");
    }

    // A10. Returns each role's permissions by name, the built-in ones included.
    private static Dictionary<string, IReadOnlyList<string>> ValidateRoles(AccessOptions access, List<string> errors)
    {
        var names = new HashSet<string>(AccessPermissions.BuiltInRoles.Keys, StringComparer.OrdinalIgnoreCase);
        var roles = new Dictionary<string, IReadOnlyList<string>>(AccessPermissions.BuiltInRoles,
            StringComparer.Ordinal);
        for (var i = 0; i < access.Roles.Count; i++)
        {
            var role = access.Roles[i];
            if (!IsValidName(role.Name) || !names.Add(role.Name))
                errors.Add($"Hub:Access:Roles:{i}:Name {NameShape}, and not reader, member or admin.");
            roles.TryAdd(role.Name, role.Permissions);
            if (role.Permissions.Contains(AccessPermissions.AccessAdmin, StringComparer.Ordinal))
                errors.Add($"Role '{role.Name}': access.admin is held only through the built-in admin role, "
                           + "Hub:Access:Admins or Hub:Access:AdminGroups.");
            else if (role.Permissions.Count == 0 ||
                     role.Permissions.Distinct(StringComparer.Ordinal).Count() != role.Permissions.Count ||
                     role.Permissions.Any(permission =>
                         !AccessPermissions.All.Contains(permission, StringComparer.Ordinal)))
                errors.Add($"Hub:Access:Roles:{i}:Permissions must list one or more of findings.read, traces.read, "
                           + "sources.read, analyses.run and acks.write, each once.");
        }

        return roles;
    }

    // A11. An exact duplicate grants nothing more, so it is allowed.
    private static void ValidateGrants(
        AccessOptions access,
        HashSet<string> projects,
        Dictionary<string, IReadOnlyList<string>> roles,
        List<string> errors)
    {
        for (var i = 0; i < access.Grants.Count; i++)
        {
            var grant = access.Grants[i];
            if (string.IsNullOrWhiteSpace(grant.Group) || string.IsNullOrWhiteSpace(grant.Project) ||
                string.IsNullOrWhiteSpace(grant.Role))
            {
                errors.Add($"Hub:Access:Grants:{i} needs Group, Project and Role.");
                continue;
            }

            if (grant.Project != Wildcard && !projects.Contains(grant.Project))
                errors.Add($"Hub:Access:Grants:{i}:Project '{grant.Project}' is neither '*' nor a project in "
                           + "Hub:Access:Projects.");
            if (!roles.ContainsKey(grant.Role))
                errors.Add($"Hub:Access:Grants:{i}:Role '{grant.Role}' is neither a built-in role nor one in "
                           + "Hub:Access:Roles.");
            else if (grant.Role == AccessPermissions.AdminRole && grant.Project != Wildcard)
                errors.Add($"Grant {i}: the admin role applies to the whole Hub; grant it on '*', or use "
                           + "Hub:Access:Admins or Hub:Access:AdminGroups.");
        }
    }

    // A12. A token's grants are sized for a machine, so its group may have no person in it.
    private static void ValidateMachineTokens(
        HubOptions options,
        Dictionary<string, IReadOnlyList<string>> groups,
        DateTimeOffset now,
        List<string> errors)
    {
        var tokens = options.Access.MachineTokens;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var secrets = new HashSet<string>(
            options.Sources.Select(source => source.ImportApiKey).OfType<string>(),
            StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var path = $"Hub:Access:MachineTokens:{i}";
            if (!MachineTokenName().IsMatch(token.Name) || !names.Add(token.Name))
                errors.Add($"{path}:Name must match ^[a-z0-9][a-z0-9._-]{{0,63}}$ and be unique.");

            if (!token.Group.StartsWith(InternalPrefix, StringComparison.Ordinal))
                errors.Add($"{path}:Group must name an internal group: a machine token gets no provider group.");
            else if (!groups.TryGetValue(token.Group[InternalPrefix.Length..], out var members))
                errors.Add($"{path}:Group: '{token.Group}' names no group in Hub:Access:Groups.");
            else if (members.Count > 0)
                errors.Add($"Group '{token.Group[InternalPrefix.Length..]}' backs machine token '{token.Name}' "
                           + "and must have no members: its grants are sized for a machine.");

            if (token.Token is null || !MachineTokenValue().IsMatch(token.Token) ||
                token.Token.StartsWith("pshub_", StringComparison.Ordinal))
                errors.Add($"{path}:Token must be 32 to 512 characters of A-Z, a-z, 0-9, '-', '.', '_', '~', '+' "
                           + "and '/', with trailing '=' allowed, and must not start with pshub_.");
            else if (!secrets.Add(token.Token))
                errors.Add($"{path}:Token must differ from every other machine token and every import API key.");

            // An expired one is not an error: it is refused at each request and never keeps the Hub down.
            if (token.ExpiresAt is not { } expiresAt || expiresAt > now.AddDays(365))
                errors.Add($"{path}:ExpiresAt must be set, at most 365 days from now.");
        }
    }

    // A part is exact, or a prefix: at least one character, then a single '*' at the end.
    private static bool IsValidPart(string part)
    {
        return IsValidValue(part) && part.IndexOf('*', StringComparison.Ordinal) is var star &&
               (star < 0 || (star == part.Length - 1 && star > 0));
    }

    private static bool IsValidValue(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Length <= MaxValueLength && !value.Any(char.IsControl);
    }

    // The RFC 6750 b64token alphabet.
    [GeneratedRegex(@"^(?=.{32,512}\z)[A-Za-z0-9._~+/-]+=*\z")]
    private static partial Regex MachineTokenValue();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}\z")]
    private static partial Regex MachineTokenName();
}
