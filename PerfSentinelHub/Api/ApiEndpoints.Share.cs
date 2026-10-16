using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Disclosures;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    /// <summary>
    ///     The two anonymous share routes: the five files under a live token, and the same bare
    ///     404 for everything else under /share/. Never a page, never a redirect, nothing of the
    ///     request reflected, and outside the access policy.
    /// </summary>
    public static void MapShare(this WebApplication app)
    {
        app.MapGet("/share/{token}/{name}", ServeSharedFileAsync).AllowAnonymous();
        // The catch-all only matches: nothing under it is read, let alone reflected.
#pragma warning disable ASP0018
        app.MapMethods("/share/{**rest}", [HttpMethods.Get, HttpMethods.Head], NotShared).AllowAnonymous();
#pragma warning restore ASP0018
    }

    // Every parameter is the route's own or a collaborator the router injects.
#pragma warning disable S107
    private static async Task<IResult> ServeSharedFileAsync(
        string token,
        string name,
        HttpContext context,
        HubDatabase database,
        ShareDownloadCounter counter,
        ShareGate gate,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
#pragma warning restore S107
    {
        Guard(context.Response);
        if (!IsShareToken(token) || !DisclosureDownloads.IsServed(name))
            return TypedResults.NotFound();
        // Released once the file is sent, not when this returns: its bytes live until then.
        if (!gate.TryEnter())
        {
            context.Response.Headers.RetryAfter = "1";
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        context.Response.OnCompleted(() =>
        {
            gate.Exit();
            return Task.CompletedTask;
        });
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (await database.FindShareLinkAsync(SHA256.HashData(Encoding.UTF8.GetBytes(token)), nowMs, cancellationToken)
            is not var (linkId, disclosureId))
            return TypedResults.NotFound();
        var served = await DisclosureFileAsync(database, disclosureId, name, context.Response, cancellationToken);
        if (served is not NotFound)
            counter.Add(linkId, nowMs,
                options.Value.Auth.TrustedProxyNetworks.Count > 0
                    ? ClientAddress.Normalize(context.Connection.RemoteIpAddress)
                    : null);
        return served;
    }

    private static NotFound NotShared(HttpContext context)
    {
        Guard(context.Response);
        return TypedResults.NotFound();
    }

    // Set before anything else, so a 404 carries them as a file does: one answer for every case.
    private static void Guard(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["X-Robots-Tag"] = "noindex";
    }

    // pshare_ and base64url, the shape IssueShareLinkAsync writes: anything else is not looked up.
    private static bool IsShareToken(string token)
    {
        return token.Length is >= 20 and <= 100 && token.StartsWith("pshare_", StringComparison.Ordinal) &&
               Base64Url.IsValid(token.AsSpan(7));
    }
}
