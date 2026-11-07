using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Analysis;

/// <summary>
///     Executes one run: obtain the engine's report JSON, then render it to a
///     self-contained HTML file. Two steps, because the query subcommands emit
///     text, JSON or SARIF and only `report` writes HTML.
/// </summary>
public sealed partial class AnalysisRunner(
    DaemonClient daemonClient,
    EngineProbe engine,
    IOptions<HubOptions> options,
    ILogger<AnalysisRunner> logger,
    CgroupMemory? memory = null)
{
    // The intermediate report JSON. Well past any real run, small enough that
    // a runaway engine cannot exhaust the container's memory.
    private const long MaxReportJsonBytes = 256L * 1024 * 1024;
    private static readonly string[] ScratchFilePatterns = ["*.input.json", "*.config.toml"];

    private readonly AnalysisOptions _analysis = options.Value.Analysis;
    private readonly CgroupMemory _memory = memory ?? CgroupMemory.Container;

    public string ReportPath(string runId)
    {
        return Path.Combine(_analysis.ReportDirectory, $"{runId}.html");
    }

    /// <summary>Removes a report whose lifetime ran out. Missing is fine.</summary>
    public void DeleteReport(string runId)
    {
        TryDelete(ReportPath(runId));
    }

    /// <summary>
    ///     Deletes scratch input files a previous process left behind. The finally
    ///     in RenderAsync cannot run when the container is killed, and nothing else
    ///     ever removes them: report expiry only knows about the .html.
    /// </summary>
    public int SweepScratchFiles()
    {
        if (!Directory.Exists(_analysis.ReportDirectory))
            return 0;

        // The listing is materialised first: deleting while the directory walk
        // is still open lets entries be skipped. Counted on the delete, not on
        // the listing, so a read-only volume cannot have the log announce
        // removals that never happened.
        return ScratchFilePatterns
            .SelectMany(pattern => Directory.GetFiles(_analysis.ReportDirectory, pattern))
            .Count(TryDelete);
    }

    public async Task<RunOutcome> RunAsync(
        AnalysisRun run,
        SourceOptions source,
        AnalysisRequest request,
        CancellationToken cancellationToken)
    {
        if (_analysis.EngineBinaryPath is null)
            return Failed(AnalysisErrorCodes.Internal);
        if (LacksMemory(run.Id))
            return Failed(AnalysisErrorCodes.HubMemoryLow);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_analysis.Timeout);
        var configPath = WriteRunConfig(run.Id, request);
        try
        {
            var reportJson = source.Kind == SourceKinds.Daemon
                ? await daemonClient.FetchReportSnapshotAsync(source, _analysis.Timeout, timeout.Token)
                : await QueryBackendAsync(run.Id, request, source, configPath, timeout.Token);
            if (reportJson is null)
                return Failed(AnalysisErrorCodes.BinaryFailed);

            // Checked again before the render: the report now sits in the Hub's own memory.
            if (LacksMemory(run.Id))
                return Failed(AnalysisErrorCodes.HubMemoryLow);

            var summary = ReportSummary.TryParse(reportJson, out var binaryVersion);
            // Checked first on purpose: output that is not a report must never
            // reach the renderer.
            if (summary is null)
                return Failed(AnalysisErrorCodes.BinaryFailed);
            if (await RenderWithDbStatAsync(run.Id, reportJson, configPath, source, timeout.Token) is { } renderFailure)
                return Failed(renderFailure);

            summary.ReportBytes = RenderedBytes(run.Id);
            return new RunOutcome(AnalysisStatuses.Succeeded, null, binaryVersion, summary);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(AnalysisErrorCodes.Timeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var code = Classify(exception);
            LogRunFailed(logger, exception, run.Id, code);
            return Failed(code);
        }
        finally
        {
            if (configPath is not null)
                TryDelete(configPath);
        }
    }

    private bool LacksMemory(string runId)
    {
        var floor = _analysis.MinFreeMemoryMiB * 1024L * 1024;
        if (floor == 0 || _memory.HeadroomBytes() is not { } headroom || headroom >= floor)
            return false;
        LogMemoryLow(logger, runId, headroom / (1024 * 1024), _analysis.MinFreeMemoryMiB);
        return true;
    }

    /// <summary>
    ///     Writes the run's own `[detection]` section, or returns null when the
    ///     operator changed nothing. Handed to both subprocesses through `-c`, so
    ///     the query and the render agree on what counts as a problem.
    /// </summary>
    private string? WriteRunConfig(string runId, AnalysisRequest request)
    {
        if (request.Detection.IsEmpty)
            return null;

        Directory.CreateDirectory(_analysis.ReportDirectory);
        var path = Path.Combine(_analysis.ReportDirectory, $"{runId}.config.toml");
        File.WriteAllText(path, request.Detection.ToToml());
        return path;
    }

    /// <summary>
    ///     Returns the engine's report JSON, or throws when the engine refused. The
    ///     stderr it wrote names an owner and goes to the log, and is never returned.
    /// </summary>
    private async Task<byte[]> QueryBackendAsync(
        string runId,
        AnalysisRequest request,
        SourceOptions source,
        string? configPath,
        CancellationToken cancellationToken)
    {
        var result = await EngineProcess.RunAsync(
            _analysis.EngineBinaryPath!,
            // A global flag, so ahead of the subcommand.
            [.. engine.MemoryLimitArguments, .. request.ToEngineArguments(source, configPath)],
            MaxReportJsonBytes,
            _analysis.ReportDirectory,
            cancellationToken,
            source.AuthHeaderName is null
                ? null
                : new Dictionary<string, string>
                {
                    // Curl form, the shape --auth-header-env documents.
                    [AnalysisRequest.AuthTokenVariable] =
                        $"{source.AuthHeaderName}: {source.AuthHeaderValue}"
                });
        if (result.Succeeded)
            return result.StandardOutput;

        var errorCode = EngineFailure.Classify(result.StandardError);
        LogQueryFailed(logger, runId, result.ExitCode, errorCode, Loggable(result.StandardError));
        throw new EngineFailedException(errorCode);
    }

    /// <summary>
    ///     The URL a daemon-source report can go live against, or null. Three ways
    ///     to get null, and all three render a static report rather than fail a
    ///     run: a source that is not a daemon, a daemon reached through a
    ///     path-based ingress (the engine takes an origin and rejects path, query,
    ///     userinfo and trailing slash at parse), and an engine binary built
    ///     without its `daemon` feature, which does not know the flag at all.
    ///     The viewer's browser makes these calls, so the URL is the public one.
    /// </summary>
    public static string? LiveDaemonUrl(SourceOptions source, bool engineTakesTheFlag)
    {
        return engineTakesTheFlag && source is { Kind: SourceKinds.Daemon, PublishedUrl.AbsolutePath: "/" }
            ? source.PublicEndpointArgument
            : null;
    }

    /// <summary>
    ///     How a daemon-source report goes live, or nothing for a static one. Through
    ///     this Hub's relay when the source holds an ack key and the engine takes
    ///     `--hub-relay`: the reader acks in their own name, under acks.write, with no
    ///     key in the browser and no CORS entry at the daemon. Otherwise straight at the
    ///     daemon, which only access off allows, since that path goes around acks.write.
    /// </summary>
    private string[] LiveArguments(SourceOptions source)
    {
        // The engine refuses the two ids a browser would read as path segments.
        if (engine.SupportsHubRelay &&
            source is { Kind: SourceKinds.Daemon, HasAckCredential: true, Id: not ("." or "..") })
            return ["--hub-relay", source.Id];
        return LiveDaemonUrl(source, engine.SupportsDaemonUrl && !options.Value.Access.Enabled) is { } url
            ? ["--daemon-url", url]
            : [];
    }

    // The engine fails a whole render on a scrape it cannot make, so a Prometheus that
    // is down or a selector it refuses costs the tab, and not the report, on a second try.
    // Null when a render went through, otherwise the code of the last one tried.
    private async Task<string?> RenderWithDbStatAsync(
        string runId,
        byte[] reportJson,
        string? configPath,
        SourceOptions source,
        CancellationToken cancellationToken)
    {
        var live = LiveArguments(source);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] dbStat =
        [
            .. DbStatArguments("pg-stat", source.PgStat, "PERF_SENTINEL_PGSTAT_AUTH_HEADER", environment),
            .. DbStatArguments("mysql-stat", source.MySqlStat, "PERF_SENTINEL_MYSQLSTAT_AUTH_HEADER", environment)
        ];
        var failure = await RenderAsync(
            runId, reportJson, configPath, [.. live, .. dbStat], environment, cancellationToken);
        if (failure is null || dbStat.Length == 0)
            return failure;
        LogRenderedWithoutDbStat(logger, runId, source.Id);
        return await RenderAsync(runId, reportJson, configPath, live, null, cancellationToken);
    }

    private static string[] DbStatArguments(
        string family,
        DbStatOptions? stats,
        string authVariable,
        Dictionary<string, string> environment)
    {
        if (stats?.Prometheus is not { } prometheus)
            return [];
        if (stats is { AuthHeaderName: { } headerName, AuthHeaderValue: { } headerValue })
            environment[authVariable] = $"{headerName}: {headerValue}";
        var arguments = new List<string> { $"--{family}-prometheus", prometheus.ToString().TrimEnd('/') };
        foreach (var (flag, value) in new[]
                 {
                     ("metric", stats.Metric), ("query-label", stats.QueryLabel), ("calls-metric", stats.CallsMetric),
                     ("unit", stats.Unit), ("selector", stats.Selector),
                     ("top", stats.Top?.ToString(CultureInfo.InvariantCulture))
                 })
            if (value is not null)
                arguments.AddRange($"--{family}-{flag}", value);
        return [.. arguments];
    }

    private async Task<string?> RenderAsync(
        string runId,
        byte[] reportJson,
        string? configPath,
        string[] extraArguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_analysis.ReportDirectory);
        // The engine reads a path rather than stdin here: the same bytes are
        // parsed twice otherwise, once by the Hub for the summary and once by
        // the renderer, and a file keeps the two independent.
        var inputPath = Path.Combine(_analysis.ReportDirectory, $"{runId}.input.json");
        try
        {
            await File.WriteAllBytesAsync(inputPath, reportJson, cancellationToken);
            var arguments = new List<string>(engine.MemoryLimitArguments)
            {
                "report", "--input", inputPath, "--output", ReportPath(runId),
                // Always passed. The flag opts the sink out of size targeting,
                // so every finding reaches the report and only the span trees
                // are capped. A wide sweep otherwise loses the tail of the list.
                // No --sort: impact is the engine's own default from 0.17.0,
                // and the released 0.16.0 rejects the flag outright, so
                // passing it would break every run on the binary most
                // deployments hold today for a value it already gets.
                "--max-traces-embedded",
                _analysis.MaxTracesEmbedded.ToString(CultureInfo.InvariantCulture)
            };
            // With --daemon-url the report's controls talk to the daemon from the
            // viewer's browser, which only works when [daemon.cors] allowed_origins
            // carries this Hub's origin; without it the report shows itself
            // disconnected and stays static.
            arguments.AddRange(extraArguments);

            if (configPath is not null)
            {
                arguments.Add("-c");
                arguments.Add(configPath);
            }

            var result = await EngineProcess.RunAsync(
                _analysis.EngineBinaryPath!,
                arguments,
                MaxReportJsonBytes,
                _analysis.ReportDirectory,
                cancellationToken,
                environment);
            if (result.Succeeded)
                return null;

            // A refused render is deterministic (bad flag, unreadable input),
            // and its stderr names the cause. Without this line the run is
            // stored as binary_failed with zero diagnostic anywhere.
            var errorCode = EngineFailure.Classify(result.StandardError);
            LogRenderFailed(logger, runId, result.ExitCode, errorCode, Loggable(result.StandardError));
            return errorCode;
        }
        finally
        {
            TryDelete(inputPath);
        }
    }

    /// <summary>The rendered file's size, null when it cannot be read.</summary>
    private long? RenderedBytes(string runId)
    {
        try
        {
            var info = new FileInfo(ReportPath(runId));
            return info.Exists ? info.Length : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A weight is context on the result, not the result.
            return null;
        }
    }


    private static RunOutcome Failed(string errorCode)
    {
        return new RunOutcome(AnalysisStatuses.Failed, errorCode, null, null);
    }

    private static string Classify(Exception exception)
    {
        return exception switch
        {
            EngineFailedException engineFailed => engineFailed.ErrorCode,
            DaemonTimeoutException => AnalysisErrorCodes.Timeout,
            ResponseTooLargeException or EngineOutputTooLargeException => AnalysisErrorCodes.OutputTooLarge,
            HttpRequestException http => ClassifyHttp(http.StatusCode),
            _ => AnalysisErrorCodes.Internal
        };
    }

    private static string ClassifyHttp(HttpStatusCode? statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AnalysisErrorCodes.SourceAuthFailed,
            // No status at all means the request never reached a server.
            null => AnalysisErrorCodes.SourceUnreachable,
            >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError =>
                AnalysisErrorCodes.SourceRejectedRequest,
            _ => AnalysisErrorCodes.SourceUnreachable
        };
    }

    // A blank message reads as a logging bug, so a silent crash says it was silent.
    private static string Loggable(string standardError)
    {
        return standardError.Length == 0 ? "(empty)" : standardError;
    }

    /// <summary>Best effort: false when the file is still there.</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The run's outcome does not depend on the scratch file going away.
            return false;
        }
    }

    [LoggerMessage(
        1602,
        LogLevel.Warning,
        "Analysis run {RunId} refused: the container has {HeadroomMiB} MiB free, under the {FloorMiB} MiB of "
        + "Hub:Analysis:MinFreeMemoryMiB. Raise the Hub's memory limit, or run the analysis when it is quieter.")]
    private static partial void LogMemoryLow(ILogger logger, string runId, long headroomMiB, int floorMiB);

    [LoggerMessage(1600, LogLevel.Warning, "Analysis run {RunId} failed with {ErrorCode}.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, string runId, string errorCode);

    [LoggerMessage(
        1601,
        LogLevel.Warning,
        "Render for run {RunId} exited {ExitCode} as {ErrorCode}: {StandardError}")]
    private static partial void LogRenderFailed(
        ILogger logger, string runId, int exitCode, string errorCode, string standardError);

    [LoggerMessage(
        1603,
        LogLevel.Warning,
        "Render for run {RunId} failed with the database statistics of source {SourceId}: rendering without them.")]
    private static partial void LogRenderedWithoutDbStat(ILogger logger, string runId, string sourceId);

    [LoggerMessage(
        1604,
        LogLevel.Warning,
        "Query for run {RunId} exited {ExitCode} as {ErrorCode}: {StandardError}")]
    private static partial void LogQueryFailed(
        ILogger logger, string runId, int exitCode, string errorCode, string standardError);
}

internal sealed class EngineFailedException(string errorCode)
    : IOException($"The engine refused the request: {errorCode}.")
{
    public string ErrorCode { get; } = errorCode;
}
