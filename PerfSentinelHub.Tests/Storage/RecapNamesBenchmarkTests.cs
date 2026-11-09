using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Storage;

/// <summary>
///     The names the recap's lists offer, read over a base the size of a fleet's: what the screen waits
///     on before a list fills.
/// </summary>
public sealed class RecapNamesBenchmarkTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-recap-names-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestPool.ClearFor(_databasePath);
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    // Wall-clock asserts are flaky in CI, so this runs on demand and its figure goes to BENCHMARKS.md:
    // PerfSentinelHub.Tests -method '*.The_recap_names_over_100k_findings_on_two_sources' -explicit only
    [Fact(Explicit = true)]
    public async Task The_recap_names_over_100k_findings_on_two_sources()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }), TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var day = today.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
            await ExecuteAsync(connection, Seed(day), cancellationToken);

        var window = RecapPeriod.Of(RecapPeriodKind.Month, today);
        var scope = AccessScope.Of(
        [
            .. Enumerable.Range(0, 6).Select(i => AccessPattern.Compile(new PatternOptions { Service = $"svc-3{i}*" })),
            .. Enumerable.Range(0, 6).Select(i =>
                AccessPattern.Compile(new PatternOptions { Attribute = $"team=team-{i}" }))
        ]);
        foreach (var (label, access) in new[] { ("unrestricted", (AccessScope?)null), ("12 patterns", scope) })
        {
            var clock = Stopwatch.StartNew();
            var services = await database.QueryRecapServicesAsync(window, null, access, cancellationToken);
            var servicesMs = clock.ElapsedMilliseconds;
            clock.Restart();
            var groupings = await database.QueryRecapGroupingsAsync(window, null, access, cancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{label}: {services.Count} services in {servicesMs} ms, " +
                $"{groupings.Count} namespaces in {clock.ElapsedMilliseconds} ms");
            Assert.NotEmpty(services);
            Assert.NotEmpty(groupings);
        }
    }

    // 100,000 findings over 40 services, each held by two of four daemons, observed five days apart
    // over the last two months, with a namespace and a team per copy and a detection count per day seen.
    private static string Seed(int today)
    {
        return $"""
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 99999)
                INSERT INTO findings(
                  signature, service, finding_type, severity, endpoint, template_hash,
                  first_seen_ms, last_seen_ms, max_confidence, max_confidence_rank)
                SELECT 's' || i, 'svc-' || (i % 40), 'slow_sql', 'warning', 'GET /', 'h', 1, 1, 'daemon_production', 4
                FROM n;
                INSERT INTO finding_sources(
                  signature, source_id, source_name, environment, producer_version, first_seen_ms, last_seen_ms,
                  service, first_observed_day)
                SELECT signature, 'daemon-' || ((rowid + k) % 4), 'D', 'prod', '0.43.2',
                       ({today} - 60) * 86400000, {today} * 86400000, service, {today} - 60 + (rowid % 5)
                FROM findings, (SELECT 0 AS k UNION ALL SELECT 1);
                WITH RECURSIVE d(j) AS (SELECT 0 UNION ALL SELECT j + 5 FROM d WHERE j < 55)
                INSERT INTO finding_observations(signature, source_id, day, severity, severity_rank)
                SELECT fs.signature, fs.source_id, fs.first_observed_day + d.j, 'warning', 2
                FROM finding_sources AS fs, d
                WHERE fs.first_observed_day + d.j <= {today};
                INSERT INTO detection_counts(signature, source_id, ledger_id, day, detections)
                SELECT signature, source_id, 'l', day, 3 FROM finding_observations;
                INSERT INTO finding_groupings(signature, source_id, grouping, last_seen_ms)
                SELECT signature, source_id, 'ns-' || (rowid % 30), {today} * 86400000 FROM finding_sources;
                INSERT INTO finding_attributes
                SELECT signature, source_id, 'k8s.namespace.name', 'ns-' || (rowid % 30), 'team', 'team-' || (rowid % 50), 1
                FROM finding_sources;
                """;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 600;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
