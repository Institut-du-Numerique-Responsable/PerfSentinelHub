using System.Security.Claims;

namespace PerfSentinelHub.Access;

/// <summary>
///     Who a decision is made for. The subject is the authorization key and nothing
///     else is compared; the groups are the provider groups frozen at sign-in.
///     A machine token's subject is <c>machine:&lt;name&gt;</c> and its groups come
///     from the configuration alone.
/// </summary>
public sealed record AccessCaller(string Kind, string Subject, IReadOnlyList<string> ProviderGroups)
{
    public const string Session = "session";
    public const string TerminalToken = "terminal_token";
    public const string MachineToken = "machine_token";
    public const string MachinePrefix = "machine:";

    // The claims a principal carries with access on. ClaimTypes.Name stays the identity.
    public const string SubjectClaim = "hub:subject";
    public const string CallerClaim = "hub:caller";
    public const string GroupClaim = "hub:group";
    public const string AccessClaim = "hub:access";
    public const string MachineTokenClaim = "hub:machine_token";
    public const string TokenIdClaim = "hub:token_id";

    // The step-1 group that admitted a provider session, which a terminal token carries on.
    public const string RequiredGroupClaim = "hub_required_group";

    // Resolved once per request. Null for a principal that carries no access claims.
    public static AccessCaller? Of(HttpContext context)
    {
        if (context.Items.TryGetValue(typeof(AccessCaller), out var cached))
            return (AccessCaller?)cached;

        var caller = Of(context.User);
        context.Items[typeof(AccessCaller)] = caller;
        return caller;
    }

    // Any principal, such as the cookie ticket a route reads beside a bearer.
    public static AccessCaller? Of(ClaimsPrincipal? user)
    {
        return user?.FindFirst(CallerClaim)?.Value is { } kind && user.FindFirst(SubjectClaim)?.Value is { } subject
            ? new AccessCaller(kind, subject, [.. user.FindAll(GroupClaim).Select(claim => claim.Value)])
            : null;
    }

    public static ClaimsIdentity MachineIdentity(string name, string authenticationType)
    {
        var subject = MachinePrefix + name;
        return new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, subject),
            new Claim(SubjectClaim, subject),
            new Claim(CallerClaim, MachineToken),
            new Claim(MachineTokenClaim, name)
        ], authenticationType);
    }
}
