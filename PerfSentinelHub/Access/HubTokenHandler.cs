using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Access;

/// <summary>
///     <c>Authorization: Bearer</c> with access on: a terminal token (<c>pshub_</c>) a browser
///     session approved, looked up by its SHA-256, or a machine token from
///     Hub:Access:MachineTokens, compared by fingerprint. A request that carries a bearer is
///     judged on it alone, so an unknown, expired or malformed one is a 401 even next to a valid
///     cookie. The token value is never logged.
/// </summary>
public sealed class HubTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AccessPolicy access,
    HubDatabase database,
    IOptions<HubOptions> hubOptions,
    TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "hub_token";
    private const string Prefix = "Bearer ";
    private const int MaxTokenChars = 512;
    private const string Refused = "The bearer token is unknown or expired.";

    // Exactly one Authorization header, in the Bearer scheme with its name in any case.
    public static bool HasBearer(HttpRequest request)
    {
        return request.Headers.Authorization is [{ } value] &&
               value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!HasBearer(Request))
            return AuthenticateResult.NoResult();

        var token = Request.Headers.Authorization[0]![Prefix.Length..];
        if (token.Length is 0 or > MaxTokenChars)
            return AuthenticateResult.Fail(Refused);
        // A12 keeps machine tokens off the prefix, so a machine token never costs a read.
        if (token.StartsWith(TerminalAccess.TokenPrefix, StringComparison.Ordinal))
            return await TerminalAsync(token);
        if (access.MachineTokenName(token, timeProvider.GetUtcNow()) is not { } name)
            return AuthenticateResult.Fail(Refused);

        var principal = new ClaimsPrincipal(AccessCaller.MachineIdentity(name, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <summary>
    ///     The owner's frozen groups, never admin (AccessPolicy.IsAdmin), and step 1's
    ///     RequiredGroups applied as the cookie event applies it: a token admitted by a group since
    ///     dropped from the list is refused. Nothing is cached, so a revocation holds at once.
    /// </summary>
    private async Task<AuthenticateResult> TerminalAsync(string token)
    {
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var row = await database.FindTerminalTokenAsync(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)), nowMs, Context.RequestAborted);
        var required = hubOptions.Value.Auth.RequiredGroups;
        if (row is null ||
            (required.Count > 0 && !row.Subject.StartsWith("local:", StringComparison.Ordinal) &&
             !required.Contains(row.RequiredGroup, StringComparer.Ordinal)))
            return AuthenticateResult.Fail(Refused);

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, row.Identity),
            new Claim(AccessCaller.SubjectClaim, row.Subject),
            new Claim(AccessCaller.CallerClaim, AccessCaller.TerminalToken),
            new Claim(AccessCaller.TokenIdClaim, row.Id),
            .. row.ProviderGroups.Select(group => new Claim(AccessCaller.GroupClaim, group))
        ], Scheme.Name);
        if (row.RequiredGroup is { } group)
            identity.AddClaim(new Claim(AccessCaller.RequiredGroupClaim, group));
        if (row.LastUsedAtMs is not { } last || nowMs - last >= TerminalAccess.LastUseResolutionMs)
            await database.TouchTerminalTokenAsync(row.Id, nowMs, Context.RequestAborted);

        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.FromUnixTimeMilliseconds(row.ExpiresAtMs) },
            Scheme.Name));
    }

    // A machine never follows a redirect to a provider.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return Task.CompletedTask;
    }
}
