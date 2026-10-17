using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private const int DefaultRunLimit = 50;
    private const int MaxRunLimit = 500;
    private const int RunIdHexChars = 16;
    private const int MaxIdentityChars = 254;

    public static void MapAnalysisApi(this WebApplication app)
    {
        app.MapPost("/api/analyses", SubmitAnalysisAsync);
        app.MapGet("/api/analyses", ListAnalysesAsync);
        app.MapGet("/api/analyses/{id}", GetAnalysisAsync);
        app.MapGet("/reports/{id}.html", GetReportAsync);
    }

    private static async Task<IResult> SubmitAnalysisAsync(
        HttpRequest request,
        HubDatabase database,
        EngineProbe engine,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var hubOptions = options.Value;
        if (hubOptions.Analysis.EngineBinaryPath is null)
            return Problem(StatusCodes.Status503ServiceUnavailable,
                "No analysis engine is configured on this Hub.");

        var payload = await ReadBodyAsync(request, cancellationToken);
        if (payload is null)
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return Problem(StatusCodes.Status400BadRequest, "The body is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            // A source missing from the caller's /api/sources reads as unknown.
            if (ReadSource(root, access.Sources(request.HttpContext, hubOptions.Sources)) is not { } source)
                return Problem(StatusCodes.Status400BadRequest, "Unknown source_id.");

            var requestElement = root.TryGetProperty("request", out var submitted)
                ? submitted
                : default;
            var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (AnalysisRequest.TryParse(requestElement, source, hubOptions.Analysis, nowMs, out var error) is not
                { } parsed)
                return Problem(StatusCodes.Status400BadRequest, error ?? "The request is invalid.");
            // A knob the embedded engine's `[detection]` does not read is refused
            // here, with both versions named, not by the engine at run time.
            if (parsed.Detection.RefusedBy(engine.Version) is { } refused)
                return Problem(StatusCodes.Status400BadRequest, refused);
            // Starting needs R and analyses.run, so a requester reads their own run while
            // their grant stands, with no requester clause.
            var rule = new RunRule(request.HttpContext, access, database);
            var subject = RunRule.Service(source.Kind, requestElement);
            if (!await rule.HoldsAsync(source.Kind, source.Id, source.Environment, subject,
                    [AccessPermissions.AnalysesRun, .. RunRule.Permissions(source.Kind)], cancellationToken))
                return Problem(StatusCodes.Status403Forbidden, "You may not start this analysis.");

            var run = NewRun(source, requestElement, Identity(request, hubOptions.Analysis), nowMs);
            if (await database.TryInsertRunAsync(run, cancellationToken))
                return TypedResults.Accepted($"/api/analyses/{run.Id}", new SubmittedAnalysis(run.Id, run.Status));

            request.HttpContext.Response.Headers.RetryAfter = "1";
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task ListAnalysesAsync(
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var limit = DefaultRunLimit;
        if (context.Request.Query.TryGetValue("limit", out var raw) &&
            (raw.Count != 1 ||
             !int.TryParse(raw[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit < 1 || limit > MaxRunLimit))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Never 403: a caller who may see no run gets an empty list. Whoever holds every
        // permission R reads on '*' keeps today's SQL.
        var rule = new RunRule(context, access, database);
        var runs = rule.SeesEverything
            ? await database.ListRunsAsync(limit, cancellationToken)
            : await database.ListRunsAsync(limit, rule.SeesAsync, cancellationToken);
        await AnalysisRunWriter.WriteArrayAsync(
            context.Response, runs, loggerFactory.CreateLogger("AnalysisApi"), cancellationToken);
    }

    private static async Task<IResult> GetAnalysisAsync(
        string id,
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!IsRunId(id))
            return TypedResults.NotFound();
        var run = await database.FindRunAsync(id, cancellationToken);
        if (run is null || !await new RunRule(context, access, database).SeesAsync(run, cancellationToken))
            return TypedResults.NotFound();

        await AnalysisRunWriter.WriteObjectAsync(
            context.Response, run, loggerFactory.CreateLogger("AnalysisApi"), cancellationToken);
        return TypedResults.Empty;
    }

    /// <summary>
    ///     Serves a rendered report from the same origin as the launcher, which is
    ///     what lets the dashboard pick up the theme with no URL parameter.
    /// </summary>
    private static async Task<IResult> GetReportAsync(
        string id,
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        AnalysisRunner runner,
        CancellationToken cancellationToken)
    {
        // The id is the only path segment that reaches the filesystem, so it is
        // checked against a hex vocabulary rather than sanitised.
        if (!IsRunId(id))
            return TypedResults.NotFound();

        // The row's rule, R, and the 404 an unknown id gets: never a 403.
        var run = await database.FindRunAsync(id, cancellationToken);
        if (run?.Status != AnalysisStatuses.Succeeded ||
            !await new RunRule(context, access, database).SeesAsync(run, cancellationToken))
            return TypedResults.NotFound();

        var path = runner.ReportPath(id);
        return File.Exists(path)
            ? TypedResults.PhysicalFile(path, "text/html; charset=utf-8")
            : TypedResults.NotFound();
    }

    private static AnalysisRun NewRun(
        SourceOptions source,
        JsonElement request,
        string requestedBy,
        long nowMs)
    {
        return new AnalysisRun(
            NewRunId(),
            AnalysisStatuses.Pending,
            source.Id,
            source.Name,
            source.Environment,
            source.Kind,
            request.ValueKind == JsonValueKind.Undefined ? "{}" : request.GetRawText(),
            requestedBy,
            nowMs,
            null, null, null, null, null, null);
    }

    private static SourceOptions? ReadSource(JsonElement root, IReadOnlyList<SourceOptions> sources)
    {
        return root.TryGetProperty("source_id", out var sourceId) &&
               sourceId.ValueKind == JsonValueKind.String
            ? sources.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, sourceId.GetString(), StringComparison.Ordinal))
            : null;
    }

    /// <summary>
    ///     The signed-in user under Hub:Auth, otherwise the identity a reverse
    ///     proxy established upstream, which the Hub records as a claim unverified.
    /// </summary>
    private static string Identity(HttpRequest request, AnalysisOptions analysis)
    {
        return KnownIdentity(request, analysis) ?? "unknown";
    }

    /// <summary>Null when no proxy established one, rather than a placeholder.</summary>
    private static string? KnownIdentity(HttpRequest request, AnalysisOptions analysis)
    {
        // A session outranks the header, which any client can send. With access on every
        // caller is named, so the header is never reached.
        return Sanitized(SessionName(request) ?? ProxyIdentity(request, analysis));
    }

    private static string? SessionName(HttpRequest request)
    {
        return request.HttpContext.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name }
            ? name
            : null;
    }

    private static string? ProxyIdentity(HttpRequest request, AnalysisOptions analysis)
    {
        return request.Headers.TryGetValue(analysis.IdentityHeader, out var values) &&
               values is [{ Length: > 0 } header]
            ? header
            : null;
    }

    private static string? Sanitized(string? identity)
    {
        if (identity is null)
            return null;
        var trimmed = identity.Length > MaxIdentityChars ? identity[..MaxIdentityChars] : identity;
        return trimmed.Any(char.IsControl) ? null : trimmed;
    }

    private static string NewRunId()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(RunIdHexChars / 2));
    }

    /// <summary>
    ///     Defence in depth ahead of the database lookup. Removing it changes no
    ///     observable response, since an id the Hub never minted finds no row and
    ///     404s anyway, so no test can pin it from outside.
    /// </summary>
    private static bool IsRunId(string id)
    {
        return id.Length == RunIdHexChars && id.All(char.IsAsciiHexDigitLower);
    }

    // The typed overload keeps NativeAOT trimming safe: the reflection-based
    // one is flagged by IL2026 and IL3050 at build time.
    internal static JsonHttpResult<AnalysisProblem> Problem(int statusCode, string detail)
    {
        return TypedResults.Json(
            new AnalysisProblem(detail),
            HubJsonContext.Default.AnalysisProblem,
            statusCode: statusCode);
    }

    /// <summary>
    ///     R, the one rule for a run's row and its report. A daemon run is judged at the
    ///     daemon as a whole, on findings.read and sources.read. A trace-backend run is
    ///     judged on traces.read: at the backend as a whole for a trace ID (or a request that
    ///     no longer parses), and on the requested service otherwise, through every Hub copy
    ///     of that service in the run's environment. The report shows every service its
    ///     traces cross; traces.read is the permission given with that in mind.
    /// </summary>
    private sealed class RunRule(HttpContext context, AccessPolicy access, HubDatabase database)
    {
        // The service subject reads the store, once per environment and service a request needs.
        private readonly Dictionary<(string, string), IReadOnlyList<AccessCopy>> _copies = [];

        public bool SeesEverything => !access.Enabled ||
                                      new[]
                                          {
                                              AccessPermissions.TracesRead, AccessPermissions.FindingsRead,
                                              AccessPermissions.SourcesRead
                                          }
                                          .All(permission => access.Scope(context, permission).Patterns is null);

        public static IReadOnlyList<string> Permissions(string kind)
        {
            return kind == SourceKinds.Daemon
                ? [AccessPermissions.FindingsRead, AccessPermissions.SourcesRead]
                : [AccessPermissions.TracesRead];
        }

        // The requested service of a trace-backend service run; null for a daemon, a trace ID
        // or anything unreadable, which are judged at the source as a whole.
        public static string? Service(string kind, JsonElement request)
        {
            return kind != SourceKinds.Daemon && request.ValueKind == JsonValueKind.Object &&
                   // A trace ID as the parser reads it: a string, so a null one is a service run.
                   !(request.TryGetProperty("trace_id", out var traceId) &&
                     traceId.ValueKind == JsonValueKind.String) &&
                   request.TryGetProperty("service", out var service) && service.ValueKind == JsonValueKind.String
                ? service.GetString()
                : null;
        }

        public async ValueTask<bool> SeesAsync(AnalysisRun run, CancellationToken cancellationToken)
        {
            if (!access.Enabled) return true;
            string? service;
            try
            {
                using var request = JsonDocument.Parse(run.RequestJson);
                service = Service(run.Kind, request.RootElement);
            }
            catch (JsonException)
            {
                service = null;
            }

            return await HoldsAsync(run.Kind, run.SourceId, run.Environment, service, Permissions(run.Kind),
                cancellationToken);
        }

        public async ValueTask<bool> HoldsAsync(
            string kind,
            string sourceId,
            string environment,
            string? service,
            IReadOnlyList<string> permissions,
            CancellationToken cancellationToken)
        {
            foreach (var permission in permissions)
            {
                var scope = access.Scope(context, permission);
                if (scope.Patterns is null)
                    continue;
                if (kind == SourceKinds.Daemon || service is null
                        ? !scope.HoldsAtSource(sourceId)
                        : !scope.CoversSubject(sourceId, service,
                            await CopiesAsync(environment, service, cancellationToken)))
                    return false;
            }

            return true;
        }

        private async ValueTask<IReadOnlyList<AccessCopy>> CopiesAsync(
            string environment,
            string service,
            CancellationToken cancellationToken)
        {
            if (!_copies.TryGetValue((environment, service), out var copies))
                _copies[(environment, service)] = copies =
                    await database.ReadServiceCopiesAsync(environment, service, cancellationToken);
            return copies;
        }
    }
}
