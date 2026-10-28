using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Disclosures;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

/// <summary>
///     The Prometheus exposition format, written by hand. The whole surface is
///     nineteen metric families over data the Hub already holds, which is not worth a
///     dependency in a NativeAOT service whose only two packages are SQLite.
///     Cardinality is bounded by configuration, not by requests: `source` takes the
///     ids and `environment` the environments in `Hub:Sources`, fixed at startup
///     and validated there, and the run `status` takes the six constants in
///     <see cref="AnalysisStatuses" />. On the findings family `finding_type` and
///     `severity` are free text a daemon sends, so both fold through the closed
///     lists of <see cref="FindingLabels" /> with an `other` bucket, and `status`
///     is one of the three values the read-time CASE yields. Nothing a caller
///     sends reaches a label, and nothing a daemon sends reaches one unfolded.
/// </summary>
public static partial class MetricsEndpoint
{
    private const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>
    ///     Hub:MetricsPort: /metrics answers on that port alone, and nothing else answers
    ///     there. Judged on the port the connection reached, never on the Host header a
    ///     client writes. Setting the key does not make Kestrel listen, and a listener
    ///     added in code would make Kestrel ignore the configured URLs, so once the server
    ///     has started the Hub checks the port is bound beside another one, and stops if not.
    /// </summary>
    public static void UseMetricsPort(this WebApplication app, int port, Action refuse)
    {
        app.Use((context, next) =>
        {
            // Judged on the endpoint routing chose, which also takes /metrics/ and /METRICS.
            if (context.Connection.LocalPort == port ==
                context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: "/metrics" })
                return next(context);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            // TestServer binds nothing and publishes no address; Kestrel always does.
            if (app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                is not { Count: > 0 } addresses)
                return;
            if (IsBoundBesidesAnother(port, addresses))
                return;
            LogMetricsPortNotBound(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MetricsEndpoint"),
                port);
            refuse();
            app.Lifetime.StopApplication();
        });
    }

    // The metrics port must be one the server listens on, and not its only one.
    public static bool IsBoundBesidesAnother(int port, IEnumerable<string> addresses)
    {
        var ports = addresses.Select(address => BindingAddress.Parse(address).Port).ToHashSet();
        return ports.Contains(port) && ports.Count > 1;
    }

    [LoggerMessage(
        2002,
        LogLevel.Critical,
        "Hub:MetricsPort {Port} must be a port the Hub listens on besides its main one; add it to "
        + "ASPNETCORE_HTTP_PORTS, for example \"8080;{Port}\".")]
    private static partial void LogMetricsPortNotBound(ILogger logger, int port);

    public static void MapMetrics(this WebApplication app)
    {
        var version = HubVersion.Current;
        // Every parameter is a collaborator the router injects, one per metric family source.
#pragma warning disable S107
        app.MapGet("/metrics", async (
                HubDatabase database,
                IOptions<HubOptions> options,
                ImportMetrics imports,
                IncidentImportMetrics incidentImports,
                CorrelationImportMetrics correlationImports,
                DetectionImportMetrics detectionImports,
                DisclosureMetrics disclosures,
                FindingMetrics findings,
                RingWatch rings,
                DetectionWatch detections,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
#pragma warning restore S107
        {
            var body = await RenderAsync(
                database, options.Value, imports, incidentImports, correlationImports, detectionImports, disclosures,
                findings, rings, detections, timeProvider, version, cancellationToken);
            return Results.Text(body, ContentType);
        }).AllowAnonymous();
    }

    // Every parameter is a collaborator the router injects, one per metric family source.
#pragma warning disable S107
    private static async Task<string> RenderAsync(
        HubDatabase database,
        HubOptions options,
        ImportMetrics imports,
        IncidentImportMetrics incidentImports,
        CorrelationImportMetrics correlationImports,
        DetectionImportMetrics detectionImports,
        DisclosureMetrics disclosures,
        FindingMetrics findings,
        RingWatch rings,
        DetectionWatch detections,
        TimeProvider timeProvider,
        string version,
        CancellationToken cancellationToken)
#pragma warning restore S107
    {
        var (states, pushes) = await database.QuerySourceObservationsAsync(cancellationToken);
        var runs = await database.CountRunsByStatusAsync(cancellationToken);
        var counted = await findings.SeriesAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        // Read from the same snapshot as the per-status series rather than from
        // a second query: two counts of the same rows taken a moment apart can
        // disagree inside one scrape.
        var queued = Runs(runs, AnalysisStatuses.Pending);

        var text = new StringBuilder();
        Family(text, "perf_sentinel_hub_build_info", "The running version, always 1.");
        // Escaped like any label value, though an assembly version cannot carry a
        // reserved character.
        Line(text, "perf_sentinel_hub_build_info", $"version=\"{Escape(version)}\"", 1);

        // Only a daemon is polled, so reachability is meaningless for a trace
        // backend and no series is written for one.
        //
        // The three families below are three loops on purpose. Every sample of a
        // family has to be contiguous, so merging them into one pass over the
        // sources would interleave families and produce a scrape Prometheus
        // rejects.
        var daemons = options.Sources
            .Where(source => source.Kind == SourceKinds.Daemon)
            .Select(source => (
                source.Id,
                State: states.GetValueOrDefault(source.Id),
                LastImportMs: pushes.TryGetValue(source.Id, out var at) ? at : (long?)null))
            .ToList();

        // A daemon with no source_state row has never been attempted, or was
        // attempted so long ago that retention dropped the row. Either way the
        // Hub has observed nothing, so all three families below stay silent for
        // it rather than publishing a value that reads as a healthy answer.
        Family(text, "perf_sentinel_hub_source_reachable",
            "1 when the Hub's last poll of this daemon succeeded, 0 while it is unreachable. "
            + "Absent for a daemon the Hub has never observed.");
        foreach (var (id, state, _) in daemons)
            if (state is not null)
                Line(text, "perf_sentinel_hub_source_reachable", Label(id),
                    state.UnreachableSinceMs is null ? 1 : 0);

        Family(text, "perf_sentinel_hub_source_unreachable_seconds",
            "How long this daemon has been unreachable, 0 while it answers. "
            + "Absent for a daemon the Hub has never observed.");
        foreach (var (id, state, _) in daemons)
            if (state is not null)
                Line(text, "perf_sentinel_hub_source_unreachable_seconds", Label(id),
                    state.UnreachableSinceMs is { } since ? Seconds(now - since) : 0);

        // Zero here would read as "succeeded just now", the opposite of never.
        Family(text, "perf_sentinel_hub_source_last_success_seconds",
            "Age of the last successful poll. Absent for a daemon never polled successfully.");
        foreach (var (id, state, _) in daemons)
            if (state?.LastSuccessMs is { } success)
                Line(text, "perf_sentinel_hub_source_last_success_seconds", Label(id),
                    Seconds(now - success));

        // Not a heartbeat, and the HELP says so: the daemon exporter sends
        // nothing at all while it has no findings, so an old value here means
        // "no new finding since" and not "the push path is broken". The
        // rejection counter below is the unambiguous half.
        Family(text, "perf_sentinel_hub_source_last_import_seconds",
            "Age of this daemon's last accepted push. A daemon with no new findings pushes "
            + "nothing, so this rises on a quiet fleet. Absent for one that has never pushed.");
        foreach (var (id, _, lastImportMs) in daemons)
            if (lastImportMs is { } pushedAt)
                Line(text, "perf_sentinel_hub_source_last_import_seconds", Label(id),
                    Seconds(now - pushedAt));

        // Published for every polled daemon from startup, like the rejection
        // counters: a series that appears only on the first miss reads as a
        // scrape gap instead.
        Counter(text, "perf_sentinel_hub_source_missed_findings_total",
            "Findings that left this daemon's ring between two polls before the Hub read them, since the "
            + "Hub started. Counted only for a daemon on perf-sentinel 0.31.2 or later whose pushes do not reach the Hub.");
        foreach (var (id, _, _) in daemons)
            Line(text, "perf_sentinel_hub_source_missed_findings_total", Label(id), rings.Missed(id));

        Counter(text, "perf_sentinel_hub_detection_reads_total",
            "Poll passes over this daemon's detection ledger since the Hub started, by outcome. Every outcome "
            + "is published from startup, and a daemon without a ledger stays at zero.");
        foreach (var (id, _, _) in daemons)
            foreach (var outcome in DetectionWatch.Outcomes)
                Line(text, "perf_sentinel_hub_detection_reads_total",
                    $"{Label(id)},outcome=\"{DetectionWatch.Label(outcome)}\"", detections.Reads(id, outcome));

        Counter(text, "perf_sentinel_hub_detection_rows_total",
            "Detection count rows the Hub merged from this daemon's ledger by poll since it started.");
        foreach (var (id, _, _) in daemons)
            Line(text, "perf_sentinel_hub_detection_rows_total", Label(id), detections.Rows(id));

        Counter(text, "perf_sentinel_hub_import_rejected_total",
            "Imports the Hub refused, by reason. Every reason is published from startup, since a "
            + "series that appears only on the first failure reads as a scrape gap instead.");
        foreach (var (reason, count) in imports.Snapshot())
            Line(text, "perf_sentinel_hub_import_rejected_total", $"reason=\"{reason}\"", count);

        Counter(text, "perf_sentinel_hub_incident_import_rejected_total",
            "Incident imports the Hub refused, by reason, published from startup like the findings ones.");
        foreach (var (reason, count) in incidentImports.Snapshot())
            Line(text, "perf_sentinel_hub_incident_import_rejected_total", $"reason=\"{reason}\"", count);

        Counter(text, "perf_sentinel_hub_correlation_import_rejected_total",
            "Correlation imports the Hub refused, by reason, published from startup like the findings ones.");
        foreach (var (reason, count) in correlationImports.Snapshot())
            Line(text, "perf_sentinel_hub_correlation_import_rejected_total", $"reason=\"{reason}\"", count);

        Counter(text, "perf_sentinel_hub_detection_import_rejected_total",
            "Detection ledger pages the Hub refused, by reason, published from startup like the findings ones.");
        foreach (var (reason, count) in detectionImports.Snapshot())
            Line(text, "perf_sentinel_hub_detection_import_rejected_total", $"reason=\"{reason}\"", count);

        // Only while disclosures are on: a Hub without them scrapes as it did before.
        if (options.Disclosures.Enabled)
        {
            Counter(text, "perf_sentinel_hub_disclosure_import_rejected_total",
                "Disclosure publications the Hub refused, by reason, published from startup like the findings ones.");
            foreach (var (reason, count) in disclosures.Snapshot())
                Line(text, "perf_sentinel_hub_disclosure_import_rejected_total", $"reason=\"{reason}\"", count);
            Counter(text, "perf_sentinel_hub_disclosure_deliveries_failed_total",
                "Disclosure webhook deliveries that gave up after their last attempt.");
            Line(text, "perf_sentinel_hub_disclosure_deliveries_failed_total", null, disclosures.DeliveriesFailed);
        }

        Family(text, "perf_sentinel_hub_analysis_queue_depth",
            "Analysis runs accepted and not yet claimed by a worker.");
        Line(text, "perf_sentinel_hub_analysis_queue_depth", null, queued);

        // A gauge, not a counter: a run moves between statuses and retention
        // removes it, so a per-status series falls as well as rises.
        Family(text, "perf_sentinel_hub_analysis_runs",
            "Analysis runs stored, by status. A run moves between statuses and finished rows age "
            + "out on Hub:Analysis:RunRetention, so every series falls as well as rises.");
        foreach (var status in AnalysisStatuses.All)
            Line(text, "perf_sentinel_hub_analysis_runs", $"status=\"{status}\"",
                Runs(runs, status));

        AppendFindings(text, counted?.Value ?? []);
        Family(text, "perf_sentinel_hub_findings_age_seconds",
            "Age of the count behind perf_sentinel_hub_findings. A count that takes longer than a scrape "
            + "can wait is served from the previous one, so this rises while the count lags. Absent until "
            + "a first count lands.");
        if (counted is not null)
            Line(text, "perf_sentinel_hub_findings_age_seconds", null,
                Seconds(now - counted.CountedAt.ToUnixTimeMilliseconds()));

        // Read from the file system, not through SQLite: a full volume refuses every
        // write but still reads, so this still answers in the case it exists for.
        Family(text, "perf_sentinel_hub_database_bytes",
            "Size of the Hub's database file and its write-ahead log, in bytes.");
        Line(text, "perf_sentinel_hub_database_bytes", null, DatabaseBytes(options.DatabasePath));
        return text.ToString();
    }

    // No zero cross-product, unlike analysis_runs. Six run statuses are six
    // series, while the zeros here would be every environment times thirteen
    // types, four severities and three statuses, nearly all of them empty for
    // good. The family is read through sum(), where an absent series is a zero.
    private static void AppendFindings(StringBuilder text, IReadOnlyList<FindingSeries> series)
    {
        Family(text, "perf_sentinel_hub_findings",
            "Distinct finding signatures stored, per environment. The status is the environment's "
            + "own: a finding still seen in staging does not keep production's copy active. A type or a "
            + "severity outside the engine's vocabulary counts under other. An empty combination has "
            + "no series.");
        foreach (var (environment, findings) in series)
            Line(text, "perf_sentinel_hub_findings",
                $"environment=\"{Escape(environment)}\",finding_type=\"{findings.FindingType}\","
                + $"severity=\"{findings.Severity}\",status=\"{findings.Status}\"",
                findings.Count);
    }

    /// <summary>
    ///     Declares a gauge. Most of this surface is one, analysis_runs included: a
    ///     run moves between statuses, so a per-status series falls as well as
    ///     rises and a counter would be a lie.
    /// </summary>
    private static void Family(StringBuilder text, string name, string help)
    {
        Family(text, name, "gauge", help);
    }

    private static void Family(StringBuilder text, string name, string type, string help)
    {
        text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        text.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
    }

    /// <summary>Declares a counter, which only ever rises or resets to zero.</summary>
    private static void Counter(StringBuilder text, string name, string help)
    {
        Family(text, name, "counter", help);
    }

    private static void Line(StringBuilder text, string name, string? labels, double value)
    {
        text.Append(name);
        if (labels is not null) text.Append('{').Append(labels).Append('}');

        text.Append(' ').Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Label(string sourceId)
    {
        return $"source=\"{Escape(sourceId)}\"";
    }

    /// <summary>
    ///     A stored timestamp ahead of the Hub's clock, from skew or from a restored
    ///     backup, clamps to zero. Zero here reads as "just now", which understates
    ///     an age rather than overstating it, so a duration alert can miss. The
    ///     alternative, publishing a negative age, breaks every threshold instead.
    /// </summary>
    private static double Seconds(long milliseconds)
    {
        return Math.Max(0, milliseconds) / 1000.0;
    }

    private static long DatabaseBytes(string databasePath)
    {
        return new[] { databasePath, databasePath + "-wal" }
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .Sum(file => file.Length);
    }

    private static int Runs(IReadOnlyDictionary<string, int> runs, string status)
    {
        return runs.GetValueOrDefault(status);
    }

    /// <summary>
    ///     Backslash, quote and newline, the three the exposition format reserves in
    ///     a label value. A source id carries none: `HubOptions.IsValidSourceId`
    ///     allows only ASCII alphanumerics, '.', '_' and '-', and the escape stays so
    ///     that loosening that rule cannot silently produce a malformed scrape. An
    ///     environment is only refused its control characters, so a quote or a
    ///     backslash does reach here.
    /// </summary>
    private static string Escape(string value)
    {
        return value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
