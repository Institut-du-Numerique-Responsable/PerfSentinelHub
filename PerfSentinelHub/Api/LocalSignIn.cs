using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Api;

/// <summary>
///     The Hub's own sign-in page, mapped only with a local account declared: single sign-on
///     first, and a local break-glass form for when the provider is down. Nothing here
///     contacts the provider until the user clicks single sign-on. A local session is
///     local:&lt;name&gt; for a fixed 8 hours, reaches what internal groups and Admins give that
///     subject, and carries a stamp of the configured hash, so a rotation ends it.
/// </summary>
public static partial class LocalSignIn
{
    public const string Path = "/auth/sign-in";
    public const string AuthenticationType = "local";
    public const string SubjectPrefix = "local:";
    public const string StampClaim = "hub:local_stamp";

    private const int MaxBodyBytes = 4096;
    private const int MaxNameChars = 64;
    private const string Wrong = "The name or the password is wrong.";

    // Inline because the launcher's stylesheets sit behind the fallback policy, and allowed by its hash.
    // The launcher's own palette, so a break-glass sign-in looks like the product it opens.
    private const string Style =
        """
        :root{color-scheme:light dark;--bg:#fff;--card:#fff;--ground:#f7f7f7;--fg:#20261f;--muted:#556b5c;--line:#ebebeb;--brand:#0ba671;--btn-bd:#a7c7bc;--btn-fg:#186b4f;--btn-bg:#e0f4ed;--sso-bg:#107a56;--sso-fg:#fff;--alert:#bf2a22;--alert-bg:#ffe4e1}
        @media (prefers-color-scheme:dark){:root{--bg:#141a17;--card:#1c241f;--ground:#141a17;--fg:#e8ede9;--muted:#a3b5ab;--line:#2b352e;--btn-bd:#2e4d41;--btn-fg:#5ecfa6;--btn-bg:rgba(11,166,113,.13);--sso-bg:#3ecf9d;--sso-fg:#0b1410;--alert:#ffa79e;--alert-bg:#3a1f1c}.light{display:none}.dark{display:block}}
        .dark{display:none}
        body{margin:0;min-height:100vh;display:grid;place-items:center;background:var(--ground);color:var(--fg);font:15px/1.5 'DM Sans',system-ui,-apple-system,sans-serif}
        main{box-sizing:border-box;width:min(26rem,calc(100% - 2rem));padding:2rem;background:var(--card);border:1px solid var(--line);border-radius:14px}
        .brand{display:flex;align-items:center;gap:9px;margin-bottom:1.75rem}
        .brand svg{display:block;height:30px;width:auto}
        .suffix{font:11px ui-monospace,SFMono-Regular,Menlo,monospace;text-transform:uppercase;letter-spacing:.1em;color:var(--muted)}
        h1{margin:0 0 .35rem;font-size:1.15rem;font-weight:600;letter-spacing:-.01em}
        .lede{margin:0 0 1.25rem;color:var(--muted)}
        .sso{display:block;padding:.7rem 1rem;border-radius:999px;background:var(--sso-bg);color:var(--sso-fg);text-align:center;text-decoration:none;font-weight:600}
        a:focus-visible,button:focus-visible,input:focus-visible,summary:focus-visible{outline:2px solid var(--brand);outline-offset:2px}
        details{margin-top:1.5rem;padding-top:1rem;border-top:1px solid var(--line);color:var(--muted)}
        summary{cursor:pointer;font-weight:600;color:var(--fg)}
        p{margin:.75rem 0;font-size:.875rem}
        .alert{padding:.5rem .75rem;border-radius:8px;background:var(--alert-bg);color:var(--alert);font-weight:600}
        label{display:block;margin-top:.75rem;color:var(--fg);font-size:.875rem}
        input{box-sizing:border-box;width:100%;margin-top:.25rem;padding:.55rem .65rem;border:1px solid var(--line);border-radius:8px;background:var(--bg);color:var(--fg);font:inherit}
        button{margin-top:1rem;padding:.5rem 1.1rem;border:1px solid var(--btn-bd);border-radius:999px;background:var(--btn-bg);color:var(--btn-fg);font:inherit;font-weight:600;cursor:pointer}
        """;

    // The launcher's two logos, read once from wwwroot and written inline: the policy loads
    // nothing, and the files themselves sit behind sign-in. Empty when the files are missing,
    // which leaves the page its title.
    private static string _logos = "";

    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(10);

    private static readonly string Policy =
        $"default-src 'none'; style-src 'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Style)))}'; " +
        "form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    public static void MapLocalSignIn(this WebApplication app)
    {
        _logos = Logo(app.Environment, "light") + Logo(app.Environment, "dark");
        // Always the page, signed in or not: a redirect would loop with /device, which
        // re-challenges a session that has less than an hour left.
        app.MapGet(Path,
                (HttpContext context) => Page(context, StatusCodes.Status200OK, null,
                    ReturnUrl(context.Request.Query["return_url"])))
            .AllowAnonymous();
        app.MapGet(Path + "/sso", (HttpContext context) =>
        {
            Secure(context.Response);
            return TypedResults.Challenge(
                new AuthenticationProperties { RedirectUri = ReturnUrl(context.Request.Query["return_url"]) },
                [HubAuthentication.Scheme]);
        }).AllowAnonymous();
        app.MapPost(Path + "/local", SignInAsync).AllowAnonymous();
    }

    /// <summary>
    ///     The local form's POST, in order: the Hub's own page only, a small url-encoded form,
    ///     the throttle before any hashing, one hash at a time, then the same 403 and the same
    ///     bytes for a wrong password, an unknown name or an empty field. An unknown name is
    ///     hashed against a dummy, so the answer's timing does not tell names apart either.
    /// </summary>
    private static async Task<IResult> SignInAsync(
        HttpContext context,
        AccessPolicy access,
        LocalSignInThrottle throttle,
        LocalSignInGate gate,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        Secure(context.Response);
        if (Refusal(request) is { } refusal) return refusal;

        var form = await request.ReadFormAsync(cancellationToken);
        var returnUrl = ReturnUrl(form["return_url"]);
        // Not trimmed: a name with a space is simply not a declared one.
        var name = Single(form["name"]).ToLowerInvariant();
        var password = Single(form["password"]);
        // A real client address: A13 requires Hub:Auth:TrustedProxyNetworks with local accounts.
        var address = ClientAddress.Normalize(context.Connection.RemoteIpAddress);
        var key = $"{address}\n{(name.Length > MaxNameChars ? name[..MaxNameChars] : name)}";

        if (throttle.TryReserve(address, key) is { } wait)
        {
            var seconds = (long)Math.Ceiling(wait.TotalSeconds);
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return Page(context, StatusCodes.Status429TooManyRequests,
                $"Too many failed attempts. Try again in {seconds} seconds.", returnUrl);
        }

        if (!await gate.TryEnterAsync(GateWait, cancellationToken))
        {
            throttle.GiveBack(address, key);
            context.Response.Headers.RetryAfter = "1";
            return Page(context, StatusCodes.Status503ServiceUnavailable, "The Hub is busy. Try again in a moment.",
                returnUrl);
        }

        var account = access.FindLocalAccount(name);
        bool verified;
        try
        {
            verified = Verify(name, password, account, gate);
        }
        finally
        {
            gate.Exit();
        }

        var logger = loggerFactory.CreateLogger(typeof(LocalSignIn));
        if (!verified)
        {
            // An undeclared name is never logged: it may be a password typed in the wrong field.
            LogRefused(logger, account is null ? "(unknown name)" : name, address, throttle.Failures(key));
            return Page(context, StatusCodes.Status403Forbidden, Wrong, returnUrl);
        }

        throttle.Succeeded(key);
        await IssueSessionAsync(context, name, account!.Stamp, timeProvider.GetUtcNow());
        LogSignedIn(logger, name, address);
        return TypedResults.LocalRedirect(returnUrl);
    }

    // The cross-site gate, then a small url-encoded form: refused before anything is read.
    private static IResult? Refusal(HttpRequest request)
    {
        if (ForeignOrigin(request))
            return TypedResults.Text("A request from another site is refused.", "text/plain; charset=utf-8",
                statusCode: StatusCodes.Status403Forbidden);
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType, "application/x-www-form-urlencoded",
                StringComparison.OrdinalIgnoreCase))
            return TypedResults.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (request.ContentLength is not (>= 0 and <= MaxBodyBytes))
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        return null;
    }

    // Called inside the gate. An empty field hashes nothing; an unknown name hashes a dummy.
    private static bool Verify(string name, string password, AccessPolicy.LocalAccount? account, LocalSignInGate gate)
    {
        if (name.Length == 0 || password.Length is 0 or > LocalPassword.MaxLength)
            return false;
        if (account is null)
        {
            // The same cost as a declared name, and the result is thrown away.
            _ = LocalPassword.Verify(password, gate.DummySalt, gate.DummyKey);
            return false;
        }

        return LocalPassword.Verify(password, account.Salt, account.Key);
    }

    private static Task IssueSessionAsync(HttpContext context, string name, string stamp, DateTimeOffset now)
    {
        var subject = SubjectPrefix + name;
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, subject),
            new Claim(AccessCaller.SubjectClaim, subject),
            new Claim(AccessCaller.AccessClaim, "1"),
            new Claim(AccessCaller.CallerClaim, AccessCaller.Session),
            new Claim(StampClaim, stamp)
        ], AuthenticationType);
        return context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = false,
                IssuedUtc = now,
                ExpiresUtc = now.AddHours(8)
            });
    }

    // The Hub's own page sends Sec-Fetch-Site: same-origin, and an Origin only its own.
    private static bool ForeignOrigin(HttpRequest request)
    {
        return (request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site != "same-origin") ||
               (request.Headers.ContainsKey("Origin") && !string.Equals(
                   request.Headers.Origin.ToString(), $"{request.Scheme}://{request.Host}",
                   StringComparison.OrdinalIgnoreCase));
    }

    // A path on this Hub, or the root: never //host, /\host or an absolute URL.
    private static string ReturnUrl(StringValues value)
    {
        return value is [{ } url] && url.StartsWith('/') && RedirectHttpResult.IsLocalUrl(url) ? url : "/";
    }

    private static string Single(StringValues value)
    {
        return value is [{ } single] ? single : "";
    }

    private static void Secure(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "same-origin";
        response.Headers.ContentSecurityPolicy = Policy;
    }

    private static string Logo(IWebHostEnvironment environment, string theme)
    {
        var file = environment.WebRootFileProvider.GetFileInfo($"logo-horiz-{theme}.svg");
        if (!file.Exists)
            return "";
        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return $"<span class=\"{theme}\">{reader.ReadToEnd()}</span>";
    }

    // No script, and nothing submitted is echoed but the return path, which is checked and encoded.
    private static ContentHttpResult Page(HttpContext context, int statusCode, string? alert, string returnUrl)
    {
        Secure(context.Response);
        var encoder = HtmlEncoder.Default;
        var pathBase = encoder.Encode(context.Request.PathBase.Value ?? "");
        var html =
            $$"""
              <!doctype html>
              <html lang="en">
              <head>
              <meta charset="utf-8">
              <meta content="width=device-width, initial-scale=1" name="viewport">
              <title>Sign in · Perf Sentinel Hub</title>
              <style>{{Style}}</style>
              </head>
              <body>
              <main>
              <div aria-label="Perf Sentinel Hub" class="brand" role="img">{{_logos}}<span aria-hidden="true" class="suffix">hub</span></div>
              <h1>Sign in to Perf Sentinel Hub</h1>
              <p class="lede">With your organisation's single sign-on. A local account is for when it is down.</p>
              <a class="sso" href="{{pathBase}}/auth/sign-in/sso?return_url={{encoder.Encode(Uri.EscapeDataString(returnUrl))}}">Sign in with single sign-on</a>
              <details{{(alert is null ? "" : " open")}}>
              <summary>Sign in with a local account</summary>
              <p>For when single sign-on is unavailable. Every local sign-in is logged.</p>
              {{(alert is null ? "" : $"<p class=\"alert\" role=\"alert\">{encoder.Encode(alert)}</p>")}}
              <form action="{{pathBase}}/auth/sign-in/local" method="post">
              <label for="name">Name</label>
              <input autocapitalize="none" autocomplete="username"{{(alert is null ? "" : " autofocus")}} id="name" maxlength="64" name="name" required spellcheck="false">
              <label for="password">Password</label>
              <input autocomplete="current-password" id="password" maxlength="1024" name="password" required type="password">
              <input name="return_url" type="hidden" value="{{encoder.Encode(returnUrl)}}">
              <button type="submit">Sign in</button>
              </form>
              </details>
              </main>
              </body>
              </html>
              """;
        return TypedResults.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }

    [LoggerMessage(2020, LogLevel.Warning, "Local account {Account} signed in from {ClientAddress}.")]
    private static partial void LogSignedIn(ILogger logger, string account, string clientAddress);

    [LoggerMessage(2021, LogLevel.Warning,
        "Local sign-in refused for {Account} from {ClientAddress} after {Failures} consecutive failures.")]
    private static partial void LogRefused(ILogger logger, string account, string clientAddress, int failures);
}

/// <summary>
///     One local password hashed at a time. Unlike RequestGate it waits, up to the caller's
///     limit, so the operator's attempt queues behind a flood instead of failing at once; at
///     most eight wait, and the ninth is refused straight away. It also holds the dummy hash an
///     unknown name is verified against, random for each process.
/// </summary>
public sealed class LocalSignInGate : IDisposable
{
    public const int MaxWaiting = 8;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private int _waiting;

    public byte[] DummySalt { get; } = RandomNumberGenerator.GetBytes(LocalPassword.SaltBytes);
    public byte[] DummyKey { get; } = RandomNumberGenerator.GetBytes(LocalPassword.KeyBytes);

    public void Dispose()
    {
        _slot.Dispose();
    }

    public async Task<bool> TryEnterAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        if (await _slot.WaitAsync(TimeSpan.Zero, cancellationToken)) return true;
        if (Interlocked.Increment(ref _waiting) > MaxWaiting)
        {
            Interlocked.Decrement(ref _waiting);
            return false;
        }

        try
        {
            return await _slot.WaitAsync(wait, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    public void Exit()
    {
        _slot.Release();
    }
}
