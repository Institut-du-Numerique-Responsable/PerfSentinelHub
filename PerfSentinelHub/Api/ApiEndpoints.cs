using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    // A deep OFFSET is a scan: SQLite steps over every skipped row.
    private const int MaxFindingOffset = 1_000_000;

    // One incident carries up to a thousand frozen findings: the cap of one daemon page.
    private const int IncidentImportMaxBytes = 4 * 1024 * 1024;

    // A hundred episodes of two templates each: the daemon's own cap per batch.
    private const int CorrelationImportMaxBytes = 1024 * 1024;

    public static void MapHubApi(this WebApplication app)
    {
        var version = HubVersion.Current;

        app.MapGet("/api/status", async Task<IResult> (
            HttpRequest request,
            EngineProbe engine,
            UpdateChecker updates,
            HubDatabase database,
            AccessPolicy access,
            IOptions<HubOptions> hubOptions,
            CancellationToken cancellationToken) =>
        {
            if (!access.ReachesAnything(request.HttpContext))
                return NoGrant();
            var analysis = hubOptions.Value.Analysis;
            return TypedResults.Ok(new StatusResponse(
                "perf-sentinel-hub",
                version,
                KnownIdentity(request, analysis),
                engine.Version,
                updates.LatestEngineVersion,
                updates.LatestHubVersion,
                await database.CountPendingRunsAsync(cancellationToken),
                analysis.Workers,
                new StatusLimits(
                    analysis.MaxTracesCap,
                    (int)analysis.Timeout.TotalSeconds,
                    (int)analysis.ReportRetention.TotalHours,
                    analysis.MaxTracesEmbedded,
                    hubOptions.Value.MaxReadLimit),
                [.. DetectionOverrides.SchemaFor(engine.Version)],
                hubOptions.Value.Disclosures.Enabled &&
                !access.Scope(request.HttpContext, AccessPermissions.DisclosuresRead).IsNothing
                    ? true
                    : null));
        });
        app.MapGet("/api/sources", GetSourcesAsync);
        app.MapGet("/api/sources/{sourceId}/daemon", GetDaemonViewAsync);
        // Never anonymous: each of the two spends a daemon's write key in the caller's name.
        app.MapGet("/api/sources/{sourceId}/acks", GetSourceAcksAsync);
        app.MapPost("/api/sources/{sourceId}/acks", CreateAckAsync);
        app.MapPost("/api/sources/{sourceId}/acks/revoke", RevokeAckAsync);
        // AllowAnonymous is inert without Hub:Auth. With it, these are the routes a
        // machine calls: IDE plugins and CI read findings, a daemon pushes with its key.
        // With Hub:Access the findings need a caller too, a machine token for a machine.
        var findings = app.MapGet("/api/findings", GetFindingsAsync);
        var byTrace = app.MapGet("/api/findings/{traceId}", GetFindingsByTraceAsync);
        if (app.Services.GetRequiredService<AccessPolicy>().Enabled)
        {
            // Any caller, even one no grant reaches. With access off it is not mapped.
            app.MapGet("/api/access", GetAccessAsync);
        }
        else
        {
            findings.AllowAnonymous();
            byTrace.AllowAnonymous();
        }

        app.MapGet("/api/incidents", GetIncidentsAsync);
        app.MapGet("/api/incidents/{id}", GetIncidentAsync);
        app.MapPost("/api/incidents/refresh", RefreshIncidentsAsync);
        app.MapGet("/api/correlations", GetCorrelationsAsync);
        app.MapPost("/api/import/findings", ImportFindingsAsync).AllowAnonymous();
        app.MapPost("/api/import/incidents", ImportIncidentsAsync).AllowAnonymous();
        app.MapPost("/api/import/correlations", ImportCorrelationsAsync).AllowAnonymous();
        app.MapPost("/api/import/disclosures", ImportDisclosuresAsync).AllowAnonymous();
        app.MapGet("/api/disclosures", GetDisclosuresAsync);
        app.MapGet("/api/disclosures/{id}", GetDisclosureAsync);
        app.MapGet("/api/disclosures/{id}/files/{name}", GetDisclosureFileAsync);
        app.MapPost("/api/disclosures/{id}/share-links", CreateShareLinkAsync);
        app.MapDelete("/api/disclosures/{id}/share-links/{linkId}", RevokeShareLinkAsync);
        app.MapPost("/api/disclosures/{id}/withdraw", WithdrawDisclosureAsync);
        app.MapPost("/api/disclosures/{id}/deliveries/{deliveryId}/retry", RetryDeliveryAsync);
        app.MapPost("/api/disclosures/{id}/render", RenderDisclosureAsync);
        app.MapGet("/health/live", TypedResults.Ok).AllowAnonymous();
        app.MapGet("/health/ready", (HubDatabase database) =>
                database.IsReady ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous();
    }

    // Who the caller is, rather than any record, is refused: no permission anywhere.
    private static JsonHttpResult<AnalysisProblem> NoGrant()
    {
        return Problem(StatusCodes.Status403Forbidden, "No grant on this Hub reaches you.");
    }

    // A listing answers 403 rather than [] when its permission is held nowhere: a CI gate
    // whose token has no grant must fail loudly, not pass on an empty page.
    private static JsonHttpResult<AnalysisProblem> NoFindingsRead()
    {
        return Problem(StatusCodes.Status403Forbidden, "No grant gives you findings.read on this Hub.");
    }

    private static async Task<IResult> GetSourcesAsync(
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        SignatureRate rate,
        CancellationToken cancellationToken)
    {
        if (!access.ReachesAnything(context))
            return NoGrant();
        var states = await database.QuerySourceStatesAsync(cancellationToken);
        var reads = await database.QueryIncidentReadsAsync(cancellationToken);
        var ackReads = await database.QueryAckReadsAsync(cancellationToken);
        IReadOnlyList<SourceResponse> sources =
        [
            .. access.Sources(context, options.Value.Sources).Select(source =>
            {
                states.TryGetValue(source.Id, out var state);
                reads.TryGetValue(source.Id, out var read);
                ackReads.TryGetValue(source.Id, out var ackRead);
                return new SourceResponse(
                    source.Id,
                    source.Name,
                    source.Environment,
                    source.Kind,
                    source.RetentionHours,
                    // A source that has never failed is reachable, including one
                    // that has never been observed at all: the Hub has no evidence
                    // against it, and a trace backend is never polled.
                    state?.UnreachableSinceMs is null,
                    state?.LastAttemptMs,
                    state?.LastSuccessMs,
                    state?.UnreachableSinceMs,
                    state?.ProducerVersion,
                    state?.LastErrorCode,
                    source.PublicEndpointArgument,
                    source.EngineSubcommand,
                    source.PublishedAuthHeaderName,
                    read?.State,
                    read?.LastReadMs,
                    ackRead?.State,
                    ackRead?.LastReadMs,
                    source.HasAckCredential,
                    rate.NewSignaturesLastHour(source.Id),
                    rate.IsAbnormal(source.Id),
                    access.Permissions(context, source));
            })
        ];
        return TypedResults.Ok(sources);
    }

    private static async Task<IResult> ImportFindingsAsync(
        HttpRequest request,
        HubDatabase database,
        ImportAdmission admission,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (AdmitImport(request, options, admission.Gate, admission.Refuse, out var source) is { } refused)
            return refused;

        try
        {
            var payload = await ReadBodyAsync(request, cancellationToken);
            if (payload is null)
                return admission.Refuse(ImportRejection.TooLarge,
                    TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge));

            ParsedImport import;
            try
            {
                import = FindingParser.ParseImport(payload);
            }
            catch (ImportBatchTooLargeException)
            {
                return admission.Refuse(ImportRejection.TooLarge,
                    TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge));
            }
            catch (InvalidDataException)
            {
                return admission.Refuse(ImportRejection.BadRequest, TypedResults.BadRequest());
            }

            if (import.Batch.Findings.Count == 0)
                return admission.Refuse(ImportRejection.BadRequest, TypedResults.BadRequest());
            var stored = await database.TryUpsertBatchAsync(
                new SourceSnapshot(source.Id, source.Name, source.Environment, import.ProducerVersion),
                import.Batch,
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                cancellationToken);
            if (!stored)
            {
                request.HttpContext.Response.Headers.RetryAfter = "1";
                return admission.Refuse(ImportRejection.WriteTimeout,
                    TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable));
            }

            if (import.Batch.RejectedCount > 0)
                LogRejectedImportedFindings(
                    loggerFactory.CreateLogger("ImportApi"),
                    source.Id,
                    import.Batch.RejectedCount);
            return TypedResults.Ok(new ImportResponse(import.Batch.Findings.Count, import.Batch.RejectedCount));
        }
        finally
        {
            admission.Gate.Exit();
        }
    }

    /// <summary>
    ///     What every import checks before reading its body: the query names one
    ///     source, the key is that source's, and a gate slot is free. Null when
    ///     the request may proceed, the refusal otherwise, already counted. The
    ///     caller owns the slot and exits it.
    /// </summary>
    private static IResult? AdmitImport(
        HttpRequest request,
        IOptions<HubOptions> options,
        ImportGate gate,
        Func<ImportRejection, IResult, IResult> refuse,
        out SourceOptions source)
    {
        source = null!;
        if (!HasValidUtf8(request.QueryString.Value) ||
            request.Query.Count != 1 ||
            !request.Query.TryGetValue("source_id", out var sourceIds) ||
            sourceIds.Count != 1 ||
            string.IsNullOrEmpty(sourceIds[0]))
            return refuse(ImportRejection.BadRequest, TypedResults.BadRequest());
        var match = options.Value.Sources.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, sourceIds[0], StringComparison.Ordinal));
        if (match is null || !IsAuthorized(request, match.ImportApiKey))
            return refuse(ImportRejection.Unauthorized, TypedResults.Unauthorized());
        if (!gate.TryEnter())
        {
            request.HttpContext.Response.Headers.RetryAfter = "1";
            return refuse(ImportRejection.GateFull,
                TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }

        source = match;
        return null;
    }

    /// <summary>
    ///     An admitted import's body, read under its cap and parsed, or the refusal it earned,
    ///     already counted: 413 past the cap or the batch limit, 400 for what the parser
    ///     refuses. The incident and correlation imports share it, their parsers differ.
    /// </summary>
    private static async Task<(T Import, IResult? Rejected)> ReadImportAsync<T>(
        HttpRequest request,
        int maxBytes,
        Func<ReadOnlyMemory<byte>, T> parse,
        Func<ImportRejection, IResult, IResult> refuse,
        CancellationToken cancellationToken)
        where T : class
    {
        var payload = await ReadBodyAsync(request, cancellationToken, maxBytes);
        if (payload is null)
            return (null!, refuse(ImportRejection.TooLarge,
                TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge)));
        try
        {
            return (parse(payload), null);
        }
        catch (ImportBatchTooLargeException)
        {
            return (null!, refuse(ImportRejection.TooLarge,
                TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge)));
        }
        catch (InvalidDataException)
        {
            return (null!, refuse(ImportRejection.BadRequest, TypedResults.BadRequest()));
        }
    }

    private static async Task<IResult> ImportIncidentsAsync(
        HttpRequest request,
        HubDatabase database,
        IncidentImportAdmission admission,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (AdmitImport(request, options, admission.Gate, admission.Refuse, out var source) is { } refused)
            return refused;
        try
        {
            var (import, rejected) = await ReadImportAsync(
                request, IncidentImportMaxBytes, IncidentParser.ParseImport, admission.Refuse, cancellationToken);
            if (rejected is not null)
                return rejected;

            if (import.Page.Incidents.Count == 0)
                return admission.Refuse(ImportRejection.BadRequest, TypedResults.BadRequest());
            if (!await database.TryUpsertPushedIncidentsAsync(source.Id, import.Page.Incidents,
                    timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken))
            {
                request.HttpContext.Response.Headers.RetryAfter = "1";
                return admission.Refuse(ImportRejection.WriteTimeout,
                    TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable));
            }

            if (import.Page.RejectedCount > 0)
                LogRejectedImportedIncidents(loggerFactory.CreateLogger("ImportApi"), source.Id,
                    import.Page.RejectedCount);
            return TypedResults.Ok(new ImportResponse(import.Page.Incidents.Count, import.Page.RejectedCount));
        }
        finally
        {
            admission.Gate.Exit();
        }
    }

    private static async Task<IResult> ImportCorrelationsAsync(
        HttpRequest request,
        HubDatabase database,
        CorrelationImportAdmission admission,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (AdmitImport(request, options, admission.Gate, admission.Refuse, out var source) is { } refused)
            return refused;
        try
        {
            var (import, rejected) = await ReadImportAsync(
                request, CorrelationImportMaxBytes, CorrelationParser.ParseImport, admission.Refuse, cancellationToken);
            if (rejected is not null)
                return rejected;
            if (import.Episodes.Count == 0)
                return admission.Refuse(ImportRejection.BadRequest, TypedResults.BadRequest());
            if (!await database.TryUpsertEpisodesAsync(source.Id, import.Episodes,
                    timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken))
            {
                request.HttpContext.Response.Headers.RetryAfter = "1";
                return admission.Refuse(ImportRejection.WriteTimeout,
                    TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable));
            }

            if (import.RejectedCount > 0)
                LogRejectedImportedEpisodes(loggerFactory.CreateLogger("ImportApi"), source.Id, import.RejectedCount);
            return TypedResults.Ok(new ImportResponse(import.Episodes.Count, import.RejectedCount));
        }
        finally
        {
            admission.Gate.Exit();
        }
    }

    private static async Task GetFindingsAsync(
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var scope = access.Scope(context, AccessPermissions.FindingsRead);
        if (scope.IsNothing)
        {
            await NoFindingsRead().ExecuteAsync(context);
            return;
        }

        if (!TryParseQuery(context.Request, options.Value, access.Sources(context, options.Value.Sources),
                out var query))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var rows = await database.QueryFindingsAsync(query with { Access = Narrowing(scope) }, cancellationToken);
        await FindingEnvelopeWriter.WriteArrayAsync(
            context.Response,
            rows,
            timeProvider.GetUtcNow(),
            loggerFactory.CreateLogger("FindingsApi"),
            cancellationToken);
    }

    private static async Task GetFindingsByTraceAsync(
        string traceId,
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var scope = access.Scope(context, AccessPermissions.FindingsRead);
        if (scope.IsNothing)
        {
            await NoFindingsRead().ExecuteAsync(context);
            return;
        }

        var rows = await database.FindByTraceAsync(traceId, Narrowing(scope), cancellationToken);
        await FindingEnvelopeWriter.WriteArrayAsync(
            context.Response,
            rows,
            timeProvider.GetUtcNow(),
            loggerFactory.CreateLogger("FindingsApi"),
            cancellationToken);
    }

    // Null for an unrestricted scope, which keeps today's SQL.
    private static AccessScope? Narrowing(AccessScope scope)
    {
        return scope.Patterns is null ? null : scope;
    }

    private static bool TryParseQuery(
        HttpRequest request,
        HubOptions options,
        IReadOnlyList<SourceOptions> sources,
        out FindingQuery query)
    {
        query = null!;
        if (!HasValidUtf8(request.QueryString.Value) || request.Query.Any(item => item.Value.Count != 1))
            return false;

        if (!TryReadBounded(request, "limit", options.DefaultReadLimit, 1, options.MaxReadLimit, out var limit) ||
            !TryReadBounded(request, "offset", 0, 0, MaxFindingOffset, out var offset) ||
            !TryReadSourceScope(request, sources, out var sourceIds) ||
            !TryReadEpochMs(request, "from", out var fromMs) ||
            !TryReadEpochMs(request, "to", out var toMs) ||
            fromMs > toMs ||
            !TryReadSignature(request, out var signature))
            return false;

        var includeAcked = true;
        if (request.Query.TryGetValue("include_acked", out var rawIncludeAcked) &&
            !bool.TryParse(rawIncludeAcked[0], out includeAcked))
            return false;

        var status = ReadOptional(request, "status");
        if (status is not null and not ("active" or "likely_resolved" or "not_observed"))
            return false;

        query = new FindingQuery(
            ReadOptional(request, "service"),
            ReadOptional(request, "finding_type"),
            ReadOptional(request, "severity"),
            limit,
            includeAcked,
            status,
            offset,
            sourceIds,
            fromMs,
            toMs,
            signature,
            ReadOptional(request, "grouping"));
        return true;
    }

    /// <summary>
    ///     The source scope the findings and the incidents reads share, null for
    ///     the whole fleet. A source id or an environment is configuration, so an
    ///     unknown one is a bad request rather than an empty page: a typo would
    ///     otherwise read as "nothing there". Given together they intersect, and a
    ///     source outside the named environment leaves the empty set, since a
    ///     screen offers the two as filters that both apply. With access on, the
    ///     sources are the caller's own list, so one outside it reads as unknown.
    /// </summary>
    private static bool TryReadSourceScope(
        HttpRequest request,
        IReadOnlyList<SourceOptions> sources,
        out IReadOnlyList<string>? sourceIds)
    {
        sourceIds = null;
        var sourceId = ReadOptional(request, "source_id");
        if (sourceId is not null &&
            !sources.Any(source => string.Equals(source.Id, sourceId, StringComparison.Ordinal)))
            return false;

        var environment = ReadOptional(request, "environment");
        if (environment is not null)
        {
            sourceIds =
            [
                .. sources
                    .Where(source => string.Equals(source.Environment, environment, StringComparison.Ordinal))
                    .Select(source => source.Id)
            ];
            if (sourceIds.Count == 0)
                return false;
        }

        if (sourceId is not null)
            sourceIds = sourceIds is null || sourceIds.Contains(sourceId) ? [sourceId] : [];
        return true;
    }

    // Null when absent or blank. The shape of a signature is the daemon's rule,
    // the Hub bounds what it binds as it bounds what it mirrors.
    private static bool TryReadSignature(HttpRequest request, out string? signature)
    {
        signature = ReadOptional(request, "signature");
        return signature is null ||
               (signature.Length <= AckParser.MaxSignatureLength && !signature.Any(char.IsControl));
    }

    // Null when absent or blank. No sign and no padding: a dashboard passes its
    // time range as plain epoch milliseconds, and anything else is a mistake.
    private static bool TryReadEpochMs(HttpRequest request, string name, out long? value)
    {
        value = null;
        var raw = ReadOptional(request, name);
        if (raw is null)
            return true;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    // A blank value reads as absent: Grafana sends a single space for its All
    // choice, which would otherwise empty the page of a free filter and answer
    // 400 on a closed set.
    private static string? ReadOptional(HttpRequest request, string name)
    {
        return request.Query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value[0])
            ? value[0]
            : null;
    }

    private static bool HasValidUtf8(string? rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery))
            return true;

        var bytes = new List<byte>(rawQuery.Length);
        var index = 0;
        while (index < rawQuery.Length)
            if (rawQuery[index] == '%')
            {
                if (index + 2 >= rawQuery.Length ||
                    !byte.TryParse(rawQuery.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                        out var value))
                    return false;
                bytes.Add(value);
                index += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(rawQuery[index].ToString()));
                index++;
            }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsAuthorized(HttpRequest request, string? apiKey)
    {
        if (apiKey is null)
            return false;
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(request.Headers["X-API-Key"].ToString()));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static async Task<byte[]?> ReadBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken,
        int maxBodyBytes = 2 * 1024 * 1024)
    {
        if (request.ContentLength > maxBodyBytes)
            return null;

        using var body = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return body.ToArray();
            if (body.Length + read > maxBodyBytes)
                return null;
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    // Every [LoggerMessage] of this class stays in this file: see LoggerMessageLayoutTests.
    [LoggerMessage(1301, LogLevel.Warning, "Source {SourceId} rejected {RejectedCount} imported findings.")]
    private static partial void LogRejectedImportedFindings(
        ILogger logger,
        string sourceId,
        int rejectedCount);

    [LoggerMessage(1302, LogLevel.Warning, "Source {SourceId} rejected {RejectedCount} imported incidents.")]
    private static partial void LogRejectedImportedIncidents(
        ILogger logger,
        string sourceId,
        int rejectedCount);

    [LoggerMessage(1303, LogLevel.Warning,
        "Source {SourceId} rejected {RejectedCount} imported correlation episodes.")]
    private static partial void LogRejectedImportedEpisodes(
        ILogger logger,
        string sourceId,
        int rejectedCount);

    [LoggerMessage(2010, LogLevel.Information,
        "Terminal sign-in approved by {Identity} ({Subject}) for {ClientAddress}, from another network: {OtherNetwork}.")]
    private static partial void LogDeviceApproved(
        ILogger logger, string identity, string subject, string clientAddress, bool otherNetwork);

    [LoggerMessage(2015, LogLevel.Information,
        "Terminal token {TokenId} issued to {ClientAddress} for {Identity} ({Subject}).")]
    private static partial void LogTokenIssued(ILogger logger, string tokenId, string? clientAddress, string identity,
        string subject);

    [LoggerMessage(2011, LogLevel.Information,
        "Terminal sign-in denied by {Identity} ({Subject}) for {ClientAddress}.")]
    private static partial void LogDeviceDenied(ILogger logger, string identity, string subject, string clientAddress);

    [LoggerMessage(2012, LogLevel.Information, "Terminal token {TokenId} of {Owner} revoked by {Actor}.")]
    private static partial void LogTokenRevoked(ILogger logger, string actor, string owner, string tokenId);

    [LoggerMessage(2013, LogLevel.Warning,
        "Terminal sign-in refused for {ClientAddress}: too many codes pending, from this address or in all.")]
    private static partial void LogDeviceCap(ILogger logger, string clientAddress);

    [LoggerMessage(2014, LogLevel.Warning,
        "Terminal sign-in is off: Hub:Auth:TrustedProxyNetworks is empty, so the Hub cannot tell clients apart behind a proxy.")]
    private static partial void LogTerminalSignInOff(ILogger logger);
}

// Bounds how many import bodies may be buffered at once (MaxImports * 2 MiB). It is not a write
// lock -- HubDatabase serializes writes -- so one slow uploader must not stall the whole fleet.
public sealed class ImportGate() : RequestGate(MaxImports)
{
    public const int MaxImports = 4;
}
