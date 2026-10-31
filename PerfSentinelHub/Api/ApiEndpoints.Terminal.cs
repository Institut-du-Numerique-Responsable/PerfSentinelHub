using System.Buffers.Text;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private const int MaxOAuthBodyBytes = 4096;
    private const int MaxDecisionBodyBytes = 1024;
    private const int TokenIdHexChars = 32;
    private const string NoBrowserSession = "Approving a sign-in or managing tokens needs a browser session.";
    private const string NoSuchToken = "No such token.";
    private const string InvalidRequest = "invalid_request";

    // Served by GET /device alone, never as a static file: only this route sends the page's
    // policy, no-store and the re-challenge of a session about to end. No inline script, and
    // the input carries no value, so no link can fill it. fonts.css embeds its faces as data:
    // URIs, which the policy admits for fonts alone, so the page sets the launcher's type.
    private const string DevicePage =
        """
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta content="width=device-width, initial-scale=1" name="viewport">
            <title>Perf Sentinel Hub - terminal sign-in</title>
            <link href="fonts.css" rel="stylesheet">
            <link href="tokens.css" rel="stylesheet">
            <link href="launcher.css" rel="stylesheet">
            <script src="text-en.js"></script>
            <script src="text-fr.js"></script>
            <script src="launcher.js"></script>
            <script src="device.js"></script>
        </head>
        <body>
        <main class="shell-main device-main">
            <div aria-label="Perf Sentinel Hub" class="shell-wordmark device-brand" role="img">
                <img alt="" data-logo="light" src="logo-horiz-light.svg">
                <img alt="" data-logo="dark" src="logo-horiz-dark.svg">
                <span aria-hidden="true" class="shell-wordmark-suffix">hub</span>
            </div>
            <span class="overline"><span aria-hidden="true">// </span>terminal sign-in</span>
            <h1 class="page-title">Approve a terminal</h1>
            <p class="page-sub" id="device-intro">Enter the code your terminal shows. Only enter a code from a perf-sentinel login you started yourself, just now: approving gives that terminal your access until your session ends.</p>
            <form class="card device-card" id="device-form">
                <label class="field-label" for="device-code">Code</label>
                <input aria-describedby="device-code-hint" autocapitalize="characters" autocomplete="off" autofocus class="input" id="device-code" maxlength="9" name="user_code" placeholder="XXXX-XXXX" required spellcheck="false" type="text">
                <p class="device-hint" id="device-code-hint">Eight letters, as perf-sentinel login prints them. Case and the dash do not matter.</p>
                <button class="pill-button pill-primary" type="submit">Continue</button>
            </form>
            <section aria-labelledby="device-request" class="card device-card" hidden id="device-details">
                <p id="device-request" tabindex="-1"></p>
                <div class="banner" data-tone="warn" hidden id="device-alert" role="alert"></div>
                <div class="device-actions">
                    <button aria-describedby="device-alert" class="pill-button pill-primary" id="device-approve" type="button">Approve</button>
                    <button class="pill-button" id="device-deny" type="button">Deny</button>
                </div>
            </section>
            <div aria-live="polite" class="device-result" id="device-result" tabindex="-1"></div>
        </main>
        </body>
        </html>
        """;

    private const string TerminalLog = "TerminalAccess";

    private const string NoPendingCode =
        "No sign-in is waiting for this code: it was decided, it expired, or it was mistyped. Run perf-sentinel login again.";

    /// <summary>
    ///     Terminal sign-in, mapped with Hub:Access:Enabled only. The token routes always, so a
    ///     token issued earlier can still be listed and revoked; the device flow only with
    ///     Hub:Auth:TrustedProxyNetworks, without which every client behind an ingress shares one
    ///     address, the approval page would show the proxy's and the per-address cap would be
    ///     Hub-wide.
    /// </summary>
    public static void MapTerminalAccess(this WebApplication app)
    {
        app.MapPost("/auth/revoke", RevokeTokenAsync).AllowAnonymous();
        app.MapGet("/api/tokens", ListTokensAsync);
        app.MapDelete("/api/tokens/{id}", DeleteTokenAsync);
        if (app.Services.GetRequiredService<IOptions<HubOptions>>().Value.Auth.TrustedProxyNetworks.Count == 0)
        {
            LogTerminalSignInOff(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(TerminalLog));
            return;
        }

        app.MapPost("/auth/device", StartDeviceAsync).AllowAnonymous();
        app.MapPost("/auth/device/token", PollDeviceAsync).AllowAnonymous();
        app.MapGet("/device", DevicePageAsync);
        app.MapPost("/api/device/verify", VerifyDeviceAsync);
        app.MapPost("/api/device/approve", ApproveDeviceAsync);
    }

    // RFC 8628 section 3.1. Codes are never logged.
    private static async Task<IResult> StartDeviceAsync(
        HttpRequest request,
        DeviceAuthorizations devices,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var (_, refusal) = await ReadOAuthRequestAsync(request, true, cancellationToken);
        if (refusal is not null) return refusal;
        var address = ClientAddress.Normalize(request.HttpContext.Connection.RemoteIpAddress);
        // The refusal stays the guard, and the answer comes last.
        // ReSharper disable once InvertIf
        if (devices.Start(address) is not { } start)
        {
            LogDeviceCap(loggerFactory.CreateLogger(TerminalLog), address);
            request.HttpContext.Response.Headers.RetryAfter = "60";
            return OAuth("temporarily_unavailable", StatusCodes.Status429TooManyRequests);
        }

        return TypedResults.Json(
            new DeviceAuthorizationResponse(
                start.DeviceCode,
                DeviceAuthorizations.Display(start.UserCode),
                // The CLI prints the URL it was given, so a forged Host misleads only its sender.
                $"{request.Scheme}://{request.Host}{request.PathBase}/device",
                TerminalAccess.ExpiresInSeconds,
                TerminalAccess.IntervalSeconds),
            HubJsonContext.Default.DeviceAuthorizationResponse);
    }

    // RFC 8628 section 3.4. No refresh token: a token ends with the session that approved it.
    private static async Task<IResult> PollDeviceAsync(
        HttpRequest request,
        DeviceAuthorizations devices,
        HubDatabase database,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var (form, refusal) = await ReadOAuthRequestAsync(request, true, cancellationToken);
        if (refusal is not null) return refusal;
        if (!form!.TryGetValue("grant_type", out var grant) || !form.TryGetValue("device_code", out var deviceCode))
            return OAuth(InvalidRequest);
        if (grant != TerminalAccess.DeviceCodeGrant) return OAuth("unsupported_grant_type");
        var poll = devices.Poll(deviceCode.ToString());
        if (poll.Approval is not { } approval) return OAuth(poll.Error!);

        var now = timeProvider.GetUtcNow();
        var token = TerminalAccess.TokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var row = new TerminalToken(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenIdHexChars / 2)),
            SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            approval.Subject,
            approval.Identity,
            approval.ProviderGroups,
            approval.RequiredGroup,
            poll.ClientAddress,
            now.ToUnixTimeMilliseconds(),
            approval.ExpiresUtc.ToUnixTimeMilliseconds(),
            null);
        bool stored;
        try
        {
            stored = await database.TryInsertTerminalTokenAsync(row, now.ToUnixTimeMilliseconds(), cancellationToken);
        }
        catch
        {
            devices.Unclaim(deviceCode.ToString());
            throw;
        }

        if (!stored)
        {
            devices.Unclaim(deviceCode.ToString());
            return OAuth("authorization_pending");
        }

        devices.Issued(deviceCode.ToString());
        LogTokenIssued(loggerFactory.CreateLogger(TerminalLog), row.Id, row.ClientAddress, approval.Identity,
            approval.Subject);
        return TypedResults.Json(
            new DeviceTokenResponse(token, "Bearer", (long)(approval.ExpiresUtc - now).TotalSeconds),
            HubJsonContext.Default.DeviceTokenResponse);
    }

    // RFC 7009 by possession of the token: 200 whether or not it existed.
    private static async Task<IResult> RevokeTokenAsync(
        HttpRequest request,
        HubDatabase database,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var (form, refusal) = await ReadOAuthRequestAsync(request, false, cancellationToken);
        if (refusal is not null) return refusal;
        if (!form!.TryGetValue("token", out var token) || StringValues.IsNullOrEmpty(token))
            return OAuth(InvalidRequest);
        var (stored, deleted) = await database.TryDeleteTerminalTokenByHashAsync(
            SHA256.HashData(Encoding.UTF8.GetBytes(token.ToString())), cancellationToken);
        if (!stored)
        {
            request.HttpContext.Response.Headers.RetryAfter = "1";
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (deleted is not null)
            LogTokenRevoked(loggerFactory.CreateLogger(TerminalLog), "its holder", deleted.Subject, deleted.Id);
        return TypedResults.Ok();
    }

    /// <summary>
    ///     The approval page, for a cookie session with at least an hour left: a shorter one, or
    ///     none, is sent through the default challenge, which comes back with a fresh session.
    ///     The query string is ignored.
    /// </summary>
    private static async Task<IResult> DevicePageAsync(HttpContext context, TimeProvider timeProvider)
    {
        var session = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!session.Succeeded || EndsWithinTheHour(session, timeProvider))
            return TypedResults.Challenge(new AuthenticationProperties
            {
                RedirectUri = context.Request.PathBase + "/device"
            });
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; font-src 'self' data:; frame-ancestors 'none'";
        return TypedResults.Content(DevicePage, "text/html; charset=utf-8");
    }

    private static async Task<IResult> VerifyDeviceAsync(
        HttpContext context,
        DeviceAuthorizations devices,
        CancellationToken cancellationToken)
    {
        var (session, caller, refusal) = await DeviceSessionAsync(context, devices);
        if (refusal is not null) return refusal;
        using var body = await ReadDecisionAsync(context.Request, cancellationToken);
        if (UserCodeOf(body) is not { } userCode)
            return Failed(devices, caller!, StatusCodes.Status400BadRequest,
                "Enter the eight letters your terminal shows.");
        if (devices.Find(userCode) is not { } pending)
            return Failed(devices, caller!, StatusCodes.Status404NotFound, NoPendingCode);
        return TypedResults.Json(
            new DeviceVerifyResponse(
                DeviceAuthorizations.Display(pending.UserCode),
                pending.ClientAddress,
                pending.ClientAddress == ClientAddress.Normalize(context.Connection.RemoteIpAddress),
                pending.RequestedAtMs,
                session!.Principal!.Identity?.Name,
                session.Properties!.ExpiresUtc?.ToUnixTimeMilliseconds()),
            HubJsonContext.Default.DeviceVerifyResponse);
    }

    private static async Task<IResult> ApproveDeviceAsync(
        HttpContext context,
        DeviceAuthorizations devices,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var (session, caller, refusal) = await DeviceSessionAsync(context, devices);
        if (refusal is not null) return refusal;
        using var body = await ReadDecisionAsync(context.Request, cancellationToken);
        if (UserCodeOf(body) is not { } userCode ||
            !body!.RootElement.TryGetProperty("approve", out var approveField) ||
            approveField.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Failed(devices, caller!, StatusCodes.Status400BadRequest, "The request is invalid.");
        if (EndsWithinTheHour(session!, timeProvider))
            return Problem(StatusCodes.Status409Conflict,
                "Your session ends within the hour. Reload the page to sign in again.");
        if (devices.Find(userCode) is not { } pending)
            return Failed(devices, caller!, StatusCodes.Status404NotFound, NoPendingCode);

        var principal = session!.Principal!;
        // Sanitized as on the ack and run paths: a provider name may carry a line break.
        var identity = Sanitized(principal.Identity?.Name) ?? caller!.Subject;
        var logger = loggerFactory.CreateLogger(TerminalLog);
        var approval = approveField.GetBoolean()
            ? new DeviceApproval(
                caller!.Subject,
                identity,
                caller.ProviderGroups,
                principal.FindFirst(AccessCaller.RequiredGroupClaim)?.Value,
                session.Properties!.ExpiresUtc!.Value,
                pending.ClientAddress != ClientAddress.Normalize(context.Connection.RemoteIpAddress))
            : null;
        if (!devices.Decide(userCode, approval))
            return Failed(devices, caller!, StatusCodes.Status404NotFound, NoPendingCode);
        // Logged when given, so an approval nobody collects still leaves a line; the token's
        // id follows in event 2015 when the terminal collects it.
        if (approval is null) LogDeviceDenied(logger, identity, caller!.Subject, pending.ClientAddress);
        else LogDeviceApproved(logger, identity, approval.Subject, pending.ClientAddress, approval.OtherNetwork);
        return TypedResults.NoContent();
    }

    // Own tokens, or everyone's for an admin. A terminal token is never an admin, and never
    // reaches here: the caller comes from the cookie alone.
    private static async Task<IResult> ListTokensAsync(
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (await BrowserCallerAsync(context) is not { } caller)
            return Problem(StatusCodes.Status401Unauthorized, NoBrowserSession);
        var tokens = await database.ListTerminalTokensAsync(
            access.IsAdmin(caller) ? null : caller.Subject,
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            options.Value.MaxReadLimit,
            cancellationToken);
        IReadOnlyList<TerminalTokenResponse> listed =
        [
            .. tokens.Select(token => new TerminalTokenResponse(
                token.Id, token.Identity, token.Subject, token.ClientAddress, token.CreatedAtMs, token.ExpiresAtMs,
                token.LastUsedAtMs))
        ];
        return TypedResults.Ok(listed);
    }

    private static async Task<IResult> DeleteTokenAsync(
        string id,
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (await BrowserCallerAsync(context) is not { } caller)
            return Problem(StatusCodes.Status401Unauthorized, NoBrowserSession);
        if (RefuseForeignRequest(context.Request) is { } refusal)
            return refusal;
        // Another user's token reads as a missing one.
        if (id.Length != TokenIdHexChars || !id.All(char.IsAsciiHexDigitLower))
            return Problem(StatusCodes.Status404NotFound, NoSuchToken);
        var (stored, owner) = await database.TryDeleteTerminalTokenAsync(
            id, access.IsAdmin(caller) ? null : caller.Subject, cancellationToken);
        if (!stored)
        {
            context.Response.Headers.RetryAfter = "1";
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (owner is null) return Problem(StatusCodes.Status404NotFound, NoSuchToken);
        LogTokenRevoked(loggerFactory.CreateLogger(TerminalLog), caller.Subject, owner, id);
        return TypedResults.NoContent();
    }

    /// <summary>
    ///     The front of the RFC routes: never a browser, which has no reason to call them, so a
    ///     hostile page cannot spend a visitor's address on the caps; a form body of at most
    ///     4 KiB, read by hand rather than bound, with each parameter once; the CLI's client_id
    ///     where asked. Every answer is no-store.
    /// </summary>
    private static async Task<(Dictionary<string, StringValues>? Form, IResult? Refusal)> ReadOAuthRequestAsync(
        HttpRequest request,
        bool needsClient,
        CancellationToken cancellationToken)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        if (request.Headers.ContainsKey("Sec-Fetch-Site"))
            return (null, Problem(StatusCodes.Status403Forbidden, "A browser never calls this route."));
        if (await ReadBodyAsync(request, cancellationToken, MaxOAuthBodyBytes) is not { } body)
            return (null, TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge));
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType, "application/x-www-form-urlencoded",
                StringComparison.OrdinalIgnoreCase))
            return (null, OAuth(InvalidRequest));
        var form = QueryHelpers.ParseQuery(Encoding.UTF8.GetString(body));
        if (form.Values.Any(value => value.Count != 1))
            return (null, OAuth(InvalidRequest));
        if (!needsClient) return (form, null);
        if (!form.TryGetValue("client_id", out var client)) return (null, OAuth(InvalidRequest));
        return client == TerminalAccess.ClientId ? (form, null) : (null, OAuth("invalid_client"));
    }

    private static JsonHttpResult<OAuthError> OAuth(string error, int statusCode = StatusCodes.Status400BadRequest)
    {
        return TypedResults.Json(new OAuthError(error), HubJsonContext.Default.OAuthError, statusCode: statusCode);
    }

    // The cookie ticket, never HttpContext.User: a bearer never approves, lists or revokes.
    private static async Task<AccessCaller?> BrowserCallerAsync(HttpContext context)
    {
        var session = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return session.Succeeded ? AccessCaller.Of(session.Principal) : null;
    }

    // The approval routes' front: a cookie session, a same-origin JSON request, and a subject
    // still under its failed-code limit.
    private static async Task<(AuthenticateResult? Session, AccessCaller? Caller, IResult? Refusal)> DeviceSessionAsync(
        HttpContext context,
        DeviceAuthorizations devices)
    {
        var session = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!session.Succeeded || AccessCaller.Of(session.Principal) is not { } caller)
            return (null, null, Problem(StatusCodes.Status401Unauthorized, NoBrowserSession));
        if (RefuseForeignRequest(context.Request) is { } refusal)
            return (null, null, refusal);
        // The refusal stays the guard, and the answer comes last.
        // ReSharper disable once InvertIf
        if (devices.Throttled(caller.Subject) is { } wait)
        {
            context.Response.Headers.RetryAfter =
                ((long)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return (null, null, Problem(StatusCodes.Status429TooManyRequests,
                "Too many codes that match no sign-in. Wait, then try again."));
        }

        return (session, caller, null);
    }

    private static bool EndsWithinTheHour(AuthenticateResult session, TimeProvider timeProvider)
    {
        return session.Properties?.ExpiresUtc is not { } expires ||
               expires - timeProvider.GetUtcNow() < TimeSpan.FromMilliseconds(TerminalAccess.MinTokenLifetimeMs);
    }

    private static async Task<JsonDocument?> ReadDecisionAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (await ReadBodyAsync(request, cancellationToken, MaxDecisionBodyBytes) is not { } body)
            return null;
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? UserCodeOf(JsonDocument? body)
    {
        return body?.RootElement is { ValueKind: JsonValueKind.Object } root &&
               root.TryGetProperty("user_code", out var field) && field.ValueKind == JsonValueKind.String
            ? DeviceAuthorizations.NormalizeUserCode(field.GetString()!)
            : null;
    }

    private static JsonHttpResult<AnalysisProblem> Failed(DeviceAuthorizations devices, AccessCaller caller,
        int statusCode, string detail)
    {
        devices.Fail(caller.Subject);
        return Problem(statusCode, detail);
    }
}
