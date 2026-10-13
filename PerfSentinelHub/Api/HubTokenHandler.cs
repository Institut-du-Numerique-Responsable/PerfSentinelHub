using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PerfSentinelHub.Api;

/// <summary>
///     <c>Authorization: Bearer</c> with access on: a machine token from Hub:Access:MachineTokens,
///     compared by fingerprint. A request that carries a bearer is judged on it alone, so an
///     unknown, expired or malformed one is a 401 even next to a valid cookie. The token value
///     is never logged.
/// </summary>
public sealed class HubTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AccessPolicy access,
    TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "hub_token";
    private const string Prefix = "Bearer ";
    private const int MaxTokenChars = 512;

    // Terminal tokens arrive with their own release, and A12 keeps machine tokens off the prefix.
    private const string TerminalPrefix = "pshub_";

    // Exactly one Authorization header, in the Bearer scheme with its name in any case.
    public static bool HasBearer(HttpRequest request)
    {
        return request.Headers.Authorization is [{ } value] &&
               value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!HasBearer(Request))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = Request.Headers.Authorization[0]![Prefix.Length..];
        if (token.Length is 0 or > MaxTokenChars || token.StartsWith(TerminalPrefix, StringComparison.Ordinal) ||
            access.MachineTokenName(token, timeProvider.GetUtcNow()) is not { } name)
            return Task.FromResult(AuthenticateResult.Fail("The bearer token is unknown or expired."));

        var principal = new ClaimsPrincipal(AccessCaller.MachineIdentity(name, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    // A machine never follows a redirect to a provider.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return Task.CompletedTask;
    }
}
