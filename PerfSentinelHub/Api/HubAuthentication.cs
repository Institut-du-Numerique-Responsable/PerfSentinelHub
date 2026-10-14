using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using IPNetwork = System.Net.IPNetwork;

namespace PerfSentinelHub.Api;

/// <summary>
///     Browser sign-in with the framework's own cookie and OAuth2 handlers, when
///     Hub:Auth:Enabled. Off, nothing here is registered and the pipeline is the one
///     the Hub always had. On, every route needs a session except the ones a machine
///     calls: findings for IDE plugins and CI, the import (its own key), health and
///     metrics. With Hub:Access:Enabled the findings need a caller too, and a machine
///     token (HubTokenHandler) is the third credential beside the cookie.
/// </summary>
public static partial class HubAuthentication
{
    internal const string Scheme = "oauth";

    // With access on: the bearer or the cookie, chosen per request.
    private const string PolicyScheme = "hub";
    private const string InternalPrefix = "internal:";
    private const int MaxSubjectChars = 256;

    public static bool AddHubAuthentication(this WebApplicationBuilder builder)
    {
        // Read from configuration rather than IOptions: the handlers are
        // registered or not, which is decided before the container exists.
        var auth = builder.Configuration.GetSection($"{HubOptions.SectionName}:Auth").Get<AuthOptions>();
        if (auth is not { Enabled: true }) return false;
        var access = builder.Configuration.GetValue<bool>($"{HubOptions.SectionName}:Access:Enabled");
        // Local accounts count with access on only, like everything under Hub:Access.
        var local = access && builder.Configuration.GetSection($"{HubOptions.SectionName}:Access:LocalAccounts")
            .GetChildren().Any();

        var authentication = builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = access ? PolicyScheme : CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = access ? PolicyScheme : Scheme;
                // The OAuth callback still writes the cookie, whatever the default reads.
                if (access) options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options => ConfigureCookie(options, auth, access, local))
            .AddOAuth(Scheme, options => ConfigureOAuth(options, auth));

        if (access)
            AddTokenSchemes(authentication);

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build());

        // Next to the database, the one writable volume: the container's home is
        // read-only, and keys kept in memory would sign everyone out on restart.
        builder.Services.AddOptions<KeyManagementOptions>()
            .Configure<IOptions<HubOptions>, ILoggerFactory>((options, hub, loggers) =>
                options.XmlRepository = new FileSystemXmlRepository(
                    new DirectoryInfo(Path.Combine(Path.GetDirectoryName(hub.Value.DatabasePath)!, "keys")),
                    loggers));

        // TLS ends at the ingress: without the forwarded scheme the redirect_uri
        // sent to the provider would read http. Without TrustedProxyNetworks only the
        // scheme is trusted, and from any peer, since the ingress address is not known
        // in advance. With it, the scheme and the client address are trusted from those
        // networks alone, one hop deep, so a direct client cannot forge either.
        builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<HubOptions>>((options, hub) =>
        {
            var networks = hub.Value.Auth.TrustedProxyNetworks;
            options.ForwardedHeaders = networks.Count == 0
                ? ForwardedHeaders.XForwardedProto
                : ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in networks)
                options.KnownIPNetworks.Add(IPNetwork.Parse(network));
        });
        return true;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions options, AuthOptions auth, bool access, bool local)
    {
        options.Cookie.Name = "hub_session";
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        // Groups are read at sign-in only: a sliding session would keep a
        // member removed at the provider in for as long as they used it.
        // Grants are read with those groups, so access stops it sliding too.
        options.SlidingExpiration = auth.RequiredGroups.Count == 0 && !access;
        if (local)
        {
            // The Hub's own page, so a page request never lands on a provider that is down:
            // single sign-on is one click on it, the local form the fallback.
            options.LoginPath = LocalSignIn.Path;
            options.ReturnUrlParameter = "return_url";
            options.Events.OnRedirectToLogin = context =>
            {
                AnswerChallenge(context.Request, context.Response, context.RedirectUri);
                return Task.CompletedTask;
            };
        }
        // The cookie scheme is reached through "hub" with access on; a page still signs in.
        else if (access) options.ForwardChallenge = Scheme;

        // The keys outlive a restart: a session opened before the gate, or
        // let in by a group since dropped from it, would last its 8 hours.
        // One opened before access was on carries no subject and no group.
        // A local ticket is judged first and alone: RequiredGroups never applies to it.
        options.Events.OnValidatePrincipal = context =>
        {
            var principal = context.Principal;
            if (principal?.Identity?.AuthenticationType == LocalSignIn.AuthenticationType
                    ? !context.HttpContext.RequestServices.GetRequiredService<AccessPolicy>().HoldsLocalTicket(principal)
                    : (auth.RequiredGroups.Count > 0 && !auth.RequiredGroups.Contains(
                          principal?.FindFirst(AccessCaller.RequiredGroupClaim)?.Value, StringComparer.Ordinal)) ||
                      (access && principal?.FindFirst(AccessCaller.AccessClaim) is null))
                context.RejectPrincipal();
            return Task.CompletedTask;
        };
    }

    private static void ConfigureOAuth(OAuthOptions options, AuthOptions auth)
    {
        options.AuthorizationEndpoint = auth.AuthorizationEndpoint!.ToString();
        options.TokenEndpoint = auth.TokenEndpoint!.ToString();
        options.UserInformationEndpoint = auth.UserInformationEndpoint!.ToString();
        options.ClientId = auth.ClientId!;
        options.ClientSecret = auth.ClientSecret!;
        options.CallbackPath = "/auth/callback";
        options.UsePkce = true;
        options.Scope.Clear();
        foreach (var scope in auth.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            options.Scope.Add(scope);
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, auth.IdentityClaim);
        options.Events.OnCreatingTicket = context => ReadUserInformationAsync(context, auth);
        options.Events.OnRemoteFailure = RefuseSignInAsync;
        options.Events.OnRedirectToAuthorizationEndpoint = RedirectToAuthorizationEndpoint;
    }

    private static Task RedirectToAuthorizationEndpoint(RedirectContext<OAuthOptions> context)
    {
        AnswerChallenge(context.Request, context.Response, context.RedirectUri);
        return Task.CompletedTask;
    }

    // A fetch cannot follow a redirect to the provider's origin. The
    // API says 401 and the launcher reloads itself into the sign-in.
    // A report says 401 only inside the launcher's frame: a shared
    // report link opened on its own is a page and signs in like one.
    private static void AnswerChallenge(HttpRequest request, HttpResponse response, string redirectUri)
    {
        if (AnswersUnauthorized(request))
            response.StatusCode = StatusCodes.Status401Unauthorized;
        else
            response.Redirect(redirectUri);
    }

    private static bool AnswersUnauthorized(HttpRequest request)
    {
        return request.Path.StartsWithSegments("/api") ||
               (request.Path.StartsWithSegments("/reports") && request.Headers["Sec-Fetch-Dest"] == "iframe");
    }

    // With access on: the machine token beside the cookie, and "hub" choosing per request.
    private static void AddTokenSchemes(AuthenticationBuilder authentication)
    {
        authentication
            .AddScheme<AuthenticationSchemeOptions, HubTokenHandler>(HubTokenHandler.SchemeName, null)
            .AddPolicyScheme(PolicyScheme, null, options => options.ForwardDefaultSelector = context =>
                HubTokenHandler.HasBearer(context.Request)
                    ? HubTokenHandler.SchemeName
                    : CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public static void UseHubAuthentication(this WebApplication app)
    {
        var access = app.Services.GetRequiredService<AccessPolicy>().Enabled;
        // A same-site page could otherwise frame the launcher and click its writes. Every
        // response, static files and reports and challenges included, unless it set its own.
        if (access)
            app.Use((context, next) =>
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.TryAdd("Content-Security-Policy", "frame-ancestors 'self'");
                    return Task.CompletedTask;
                });
                return next(context);
            });
        app.UseForwardedHeaders();
        app.UseAuthentication();
        app.UseAuthorization();
        if (access) app.Use(RefuseWritesAsync);
    }

    /// <summary>
    ///     The access request gate, after authorization: a machine token only reads, and a
    ///     write without a bearer must come from the Hub's own page. Only a browser holds the
    ///     cookie and every current one sends Sec-Fetch-Site; curl, CI and the CLI use a
    ///     bearer, which a browser never attaches on its own.
    /// </summary>
    private static async Task RefuseWritesAsync(HttpContext context, RequestDelegate next)
    {
        var detail = WriteRefusal(context);
        if (detail is null)
            await next(context);
        else
            await ApiEndpoints.Problem(StatusCodes.Status403Forbidden, detail).ExecuteAsync(context);
    }

    private static string? WriteRefusal(HttpContext context)
    {
        var method = context.Request.Method;
        if (context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null ||
            HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
            return null;
        if (AccessCaller.Of(context)?.Kind == AccessCaller.MachineToken)
            return "A machine token only reads.";
        return !HttpMethods.IsOptions(method) && !HubTokenHandler.HasBearer(context.Request) &&
               context.Request.Headers["Sec-Fetch-Site"] != "same-origin"
            ? "A request from another site is refused."
            : null;
    }

    // At startup, so an old entry is found in the log rather than by the CI job it breaks.
    public static void WarnOnExpiredMachineTokens(AccessPolicy access, TimeProvider timeProvider, ILogger logger)
    {
        foreach (var name in access.ExpiredMachineTokens(timeProvider.GetUtcNow()))
            LogMachineTokenExpired(logger, name);
    }

    [LoggerMessage(
        2001,
        LogLevel.Warning,
        "Machine token '{Name}' has expired and is refused: rotate it, or remove its entry from Hub:Access:MachineTokens.")]
    private static partial void LogMachineTokenExpired(ILogger logger, string name);

    [LoggerMessage(
        1902,
        LogLevel.Debug,
        "Sign-in of {Name} kept no grant for the provider groups {Groups}: no grant and no AdminGroups entry names them.")]
    private static partial void LogGroupsDropped(ILogger logger, string name, string groups);

    /// <summary>
    ///     The ack relay names who acks: a session, or the proxy's identity header
    ///     once Hub:AckRelay:TrustIdentityHeader opts in. With neither it refuses
    ///     every caller, and a configured ack credential is never sent.
    /// </summary>
    public static void WarnWhenAckRelayIdentifiesNobody(HubOptions options, ILogger logger)
    {
        if (options.Auth.Enabled || options.AckRelay.TrustIdentityHeader) return;

        string[] sourceIds = [.. options.Sources.Where(source => source.HasAckCredential).Select(source => source.Id)];
        if (sourceIds.Length > 0)
            LogAckRelayIdentifiesNobody(logger, string.Join(", ", sourceIds));
    }

    [LoggerMessage(
        1901,
        LogLevel.Warning,
        "Neither Hub:Auth:Enabled nor Hub:AckRelay:TrustIdentityHeader is set, so the ack relay can "
        + "identify nobody: the ack credential of {SourceIds} will never be sent.")]
    private static partial void LogAckRelayIdentifiesNobody(ILogger logger, string sourceIds);

    /// <summary>
    ///     A cancelled consent screen, a userinfo without the identity field or a
    ///     user in none of the required groups would otherwise surface as an
    ///     unhandled 500. One line for all three, which tells a caller nothing of
    ///     the user's groups. No redirect: it would send the user straight back to
    ///     the screen they just cancelled. Nor "reload": reloading this callback
    ///     replays a state whose correlation cookie is already spent, and fails
    ///     again. The line names no URL built from the request, whose Host a
    ///     client chooses.
    /// </summary>
    private static async Task RefuseSignInAsync(RemoteFailureContext context)
    {
        LogSignInRefused(
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(HubAuthentication)),
            context.Failure?.Message ?? "unknown");
        context.HandleResponse();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(
            "Sign-in refused. Open the Hub's home page to try again, or ask the Hub's administrator for access.",
            context.HttpContext.RequestAborted);
    }

    [LoggerMessage(1900, LogLevel.Warning, "Sign-in refused: {Reason}")]
    private static partial void LogSignInRefused(ILogger logger, string reason);

    private static async Task ReadUserInformationAsync(OAuthCreatingTicketContext context, AuthOptions auth)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
        response.EnsureSuccessStatusCode();
        using var user = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted),
            cancellationToken: context.HttpContext.RequestAborted);
        context.RunClaimActions(user.RootElement);
        // Fails the sign-in rather than recording a nameless session: the
        // provider's userinfo does not carry the configured field.
        if (context.Identity?.Name is not { Length: > 0 } name)
            throw new InvalidOperationException("The provider's userinfo lacks the Hub:Auth:IdentityClaim field.");
        var access = context.HttpContext.RequestServices.GetRequiredService<AccessPolicy>();
        // One read of the field for both gates, so they can never disagree on a user's groups.
        var groups = auth.RequiredGroups.Count > 0 || access.Enabled
            ? ReadGroups(user.RootElement, auth.GroupsClaim)
            : null;
        if (auth.RequiredGroups.Count > 0)
        {
            // The reasons name the user, for the operator's log, and never their groups.
            if (groups is null)
                throw new InvalidOperationException(
                    $"The provider's userinfo for {name} lacks the Hub:Auth:GroupsClaim field.");
            var member = groups.FirstOrDefault(group => auth.RequiredGroups.Contains(group, StringComparer.Ordinal))
                         ?? throw new InvalidOperationException($"{name} is in none of Hub:Auth:RequiredGroups.");
            context.Identity.AddClaim(new Claim(AccessCaller.RequiredGroupClaim, member));
        }

        if (access.Enabled)
            AddAccessClaims(context, context.Identity, user.RootElement, auth, access, name, groups ?? []);
    }

    /// <summary>
    ///     The subject decides and the identity displays: the subject is an id the
    ///     provider never reassigns, and nothing is authorized on the identity. Only the
    ///     provider groups a grant or AdminGroups names are kept, never one spelled
    ///     internal:, so the cookie grows with the configuration and not with the provider.
    /// </summary>
    private static void AddAccessClaims(
        OAuthCreatingTicketContext context,
        ClaimsIdentity identity,
        JsonElement user,
        AuthOptions auth,
        AccessPolicy access,
        string name,
        IReadOnlyList<string> groups)
    {
        if (!user.TryGetProperty(auth.SubjectClaim, out var field) || field.ValueKind != JsonValueKind.String ||
            field.GetString() is not { Length: > 0 and <= MaxSubjectChars } subject ||
            string.IsNullOrWhiteSpace(subject))
            throw new InvalidOperationException(
                $"The provider's userinfo for {name} lacks a string Hub:Auth:SubjectClaim field of 1 to {MaxSubjectChars} characters.");
        if (IsReserved(subject) || IsReserved(name))
            throw new InvalidOperationException(
                $"{name}: the subject or the identity starts with a reserved prefix, local: or machine:.");

        identity.AddClaim(new Claim(AccessCaller.SubjectClaim, subject));
        var kept = groups.Where(group => !group.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase) &&
                                         access.ProviderGroups.Contains(group)).Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var group in kept)
            identity.AddClaim(new Claim(AccessCaller.GroupClaim, group));
        if (groups.Except(kept, StringComparer.Ordinal).ToList() is { Count: > 0 } dropped)
            LogGroupsDropped(
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(HubAuthentication)),
                name,
                string.Join(", ", dropped));
        identity.AddClaim(new Claim(AccessCaller.AccessClaim, "1"));
        identity.AddClaim(new Claim(AccessCaller.CallerClaim, AccessCaller.Session));
    }

    private static bool IsReserved(string value)
    {
        return value.StartsWith("local:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith(AccessCaller.MachinePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The groups field as step 1 reads it: an array gives its string entries in order,
    ///     a lone string is one group and never split, anything else no group. Null when the
    ///     field is missing, which RequiredGroups refuses and access does not.
    /// </summary>
    private static List<string>? ReadGroups(JsonElement user, string claim)
    {
        if (!user.TryGetProperty(claim, out var groups))
            return null;
        return groups.ValueKind switch
        {
            JsonValueKind.Array =>
            [
                .. groups.EnumerateArray().Where(group => group.ValueKind == JsonValueKind.String)
                    .Select(group => group.GetString()!)
            ],
            JsonValueKind.String => [groups.GetString()!],
            _ => []
        };
    }
}
