using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

/// <summary>
///     The SQL fragments and the C# matcher must give one answer for one scope, on the
///     rows H1 actually writes: the attributes and the incident pairs are derived by the
///     Hub's own boot backfill, marker rows included.
/// </summary>
public sealed class AccessScopeParityTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-access-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestPool.ClearFor(_databasePath);
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("source=daemon-a", "s1 daemon-a,s2 daemon-a,s4 daemon-a,s6 daemon-a", "i1 daemon-a")]
    [InlineData("source=daemon*", "s1 daemon-a,s1 daemon-b,s2 daemon-a,s4 daemon-a,s5 daemon-b,s6 daemon-a", "i1 daemon-a,i1 daemon-b,i2 daemon-b")]
    [InlineData("source=Daemon-a", "", "")]
    [InlineData("service=cart", "s1 daemon-a,s5 daemon-b", "i1 daemon-a,i2 daemon-b")]
    [InlineData("service=cart*", "s1 daemon-a,s1 daemon-b,s5 daemon-b", "i1 daemon-a,i1 daemon-b,i2 daemon-b")]
    [InlineData("service=c*", "s1 daemon-a,s1 daemon-b,s5 daemon-b", "i1 daemon-a,i1 daemon-b,i2 daemon-b")]
    [InlineData("service=Shop", "s3 lab", "")]
    [InlineData("attribute=team=shop", "s1 daemon-a,s2 daemon-a,s4 daemon-a", "i1 daemon-a")]
    [InlineData("attribute=team=sh*", "s1 daemon-a,s2 daemon-a,s4 daemon-a", "i1 daemon-a")]
    [InlineData("attribute=team=Shop", "s3 lab", "")]
    [InlineData("attribute=team=a=b", "s5 daemon-b", "i1 daemon-b")]
    [InlineData("attribute=team=a=*", "s5 daemon-b", "i1 daemon-b")]
    [InlineData("attribute=k8s.namespace.name=shop-ns", "s1 daemon-a", "")]
    [InlineData("source=daemon-b;service=cart", "s5 daemon-b", "i2 daemon-b")]
    [InlineData("source=daemon-a;attribute=team=shop|service=invoice*", "s1 daemon-a,s2 daemon-a,s4 daemon-a", "i1 daemon-a,i3 lab")]
    [InlineData("source=lab;service=Sh*;attribute=team=S*", "s3 lab", "")]
    public async Task The_sql_fragments_read_what_the_matcher_reads(string patterns, string findings, string incidents)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);
        var scope = AccessScope.Of([.. Patterns(patterns)]);

        await using var connection = await Database().OpenConnectionAsync(cancellationToken);
        var copies = await CopiesAsync(connection, cancellationToken);
        var incidentCopies = await IncidentsAsync(connection, cancellationToken);

        Assert.Equal(findings, string.Join(',', Matching(copies, scope)));
        Assert.Equal(incidents, string.Join(',', Matching(incidentCopies, scope)));
        Assert.Equal(
            Matching(copies, scope),
            await KeysAsync(
                connection,
                $"WITH {HubDatabase.AccessPatternsCte}, {HubDatabase.VisibleCopiesCte} " +
                "SELECT signature || ' ' || source_id FROM visible",
                scope,
                cancellationToken));
        Assert.Equal(
            Matching(incidentCopies, scope),
            await KeysAsync(
                connection,
                $"WITH {HubDatabase.AccessPatternsCte} " +
                $"SELECT id || ' ' || source_id FROM incidents WHERE {HubDatabase.IncidentAccess}",
                scope,
                cancellationToken));
    }

    // The table holds the cases parity has to cover, so a seed that stops producing them
    // would let every comparison above pass on rows that no longer test anything.
    [Fact]
    public async Task The_seed_holds_the_markers_the_null_service_and_a_copy_seen_in_two_groupings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);
        await using var connection = await Database().OpenConnectionAsync(cancellationToken);

        var copies = await CopiesAsync(connection, cancellationToken);
        Assert.Contains(new AccessAttribute("", "", "", ""), copies["s1 daemon-b"].Rows);
        Assert.Null(copies["s4 daemon-a"].Service);
        Assert.Empty(copies["s6 daemon-a"].Rows);
        Assert.Equal(2, copies["s2 daemon-a"].Rows.Select(row => (row.GroupingKey, row.GroupingValue)).Distinct().Count());

        var incidents = await IncidentsAsync(connection, cancellationToken);
        Assert.Equal(new AccessAttribute("", "", "team", "shop"), Assert.Single(incidents["i1 daemon-a"].Rows));
        Assert.Equal(new AccessAttribute("", "", "", ""), Assert.Single(incidents["i2 daemon-b"].Rows));
        Assert.Equal(new AccessAttribute("", "", "", ""), Assert.Single(incidents["i3 lab"].Rows));
    }

    // Wall-clock asserts are flaky in CI, so this runs on demand and its figure goes to BENCHMARKS.md:
    // PerfSentinelHub.Tests -method '*.The_visible_cte_over_100k_copies_and_12_patterns' -explicit only
    [Fact(Explicit = true)]
    public async Task The_visible_cte_over_100k_copies_and_12_patterns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = Database();
        await database.InitializeAsync(cancellationToken);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            """
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 99999)
            INSERT INTO findings(
              signature, finding_json, service, finding_type, severity, endpoint, template_hash,
              first_seen_ms, last_seen_ms, max_confidence, max_confidence_rank)
            SELECT 's' || i, '{}', 'svc-' || (i % 40), 'slow_sql', 'warning', 'GET /', 'h', 1, 1, 'daemon_production', 4
            FROM n;
            INSERT INTO finding_sources(
              signature, source_id, source_name, environment, producer_version, first_seen_ms, last_seen_ms, service)
            SELECT signature, 'daemon-' || (rowid % 4), 'D', 'prod', '0.26.2', 1, 1, service FROM findings;
            INSERT INTO finding_attributes
            SELECT signature, source_id, 'team', 'team-' || (rowid % 50), 'team', 'team-' || (rowid % 50), 1
            FROM finding_sources;
            """,
            cancellationToken);
        var scope = AccessScope.Of(
        [
            .. Enumerable.Range(0, 6).Select(i => Compile(new PatternOptions { Service = $"svc-3{i}*" })),
            .. Enumerable.Range(0, 6).Select(i => Compile(new PatternOptions { Attribute = $"team=team-{i}" }))
        ]);

        var clock = Stopwatch.StartNew();
        var visible = (await KeysAsync(
            connection,
            $"WITH {HubDatabase.AccessPatternsCte}, {HubDatabase.VisibleCopiesCte} " +
            "SELECT signature || ' ' || source_id FROM visible",
            scope,
            cancellationToken)).Count;
        clock.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"{visible} visible copies in {clock.ElapsedMilliseconds} ms");
        Assert.True(visible > 0);
    }

    private HubDatabase Database()
    {
        return new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            TimeProvider.System);
    }

    // Copies and incidents as an older build left them, then a boot, whose backfill derives
    // every attribute row, the copies' own services and the incident pairs the way H1 does.
    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await Database().InitializeAsync(cancellationToken);
        await using (var connection = await Database().OpenConnectionAsync(cancellationToken))
            await ExecuteAsync(
                connection,
                """
                INSERT INTO findings(
                  signature, finding_json, service, finding_type, severity, endpoint, template_hash,
                  first_seen_ms, last_seen_ms, max_confidence, max_confidence_rank)
                SELECT value, '{}', 'fleet-wide', 'slow_sql', 'warning', 'GET /', 'h', 1, 1, 'daemon_production', 4
                FROM json_each('["s1", "s2", "s3", "s4", "s5", "s6"]');
                INSERT INTO finding_sources(
                  signature, source_id, source_name, environment, producer_version, first_seen_ms, last_seen_ms,
                  finding_json)
                VALUES
                  ('s1', 'daemon-a', 'A', 'prod', '0.26.2', 1, 1,
                   '{"finding": {"service": "cart", "grouping": [{"key": "k8s.namespace.name", "value": "shop-ns"},
                                                                  {"key": "team", "value": "shop"}]}}'),
                  ('s1', 'daemon-b', 'B', 'prod', '0.26.2', 1, 1, '{"finding": {"service": "cart-api", "grouping": []}}'),
                  ('s2', 'daemon-a', 'A', 'prod', '0.26.2', 1, 1,
                   '{"finding": {"service": "invoice-api", "grouping": [{"key": "team", "value": "billing"}]}}'),
                  ('s3', 'lab', 'Lab', 'lab', '0.26.2', 1, 1,
                   '{"finding": {"service": "Shop", "grouping": [{"key": "team", "value": "Shop"}]}}'),
                  ('s4', 'daemon-a', 'A', 'prod', '0.26.2', 1, 1,
                   '{"finding": {"grouping": [{"key": "team", "value": "shop"}]}}'),
                  ('s5', 'daemon-b', 'B', 'prod', '0.26.2', 1, 1,
                   '{"finding": {"service": "cart", "grouping": [{"key": "team", "value": "a=b"}]}}'),
                  ('s6', 'daemon-a', 'A', 'prod', '0.11.2', 1, 1, NULL);
                INSERT INTO incidents(
                  id, source_id, service, kind, at_ms, window_from_ms, window_to_ms, finding_count,
                  incident_json, first_seen_ms, last_seen_ms, findings_json)
                VALUES
                  ('i1', 'daemon-a', 'cart', 'latency', 1, 0, 1, 2, '{}', 1, 7,
                   '[{"finding": {"grouping": [{"key": "team", "value": "shop"}, {"key": "zone", "value": "eu"}]}},
                     {"finding": {"grouping": [{"key": "team", "value": "shop"}]}}]'),
                  ('i1', 'daemon-b', 'cart-api', 'latency', 1, 0, 1, 1, '{}', 1, 7,
                   '[{"finding": {"grouping": [{"key": "team", "value": "a=b"}]}}]'),
                  ('i2', 'daemon-b', 'cart', 'latency', 1, 0, 1, 2, '{}', 1, 7,
                   '[{"finding": {"grouping": [{"key": "team", "value": "shop"}]}},
                     {"finding": {"grouping": [{"key": "team", "value": "billing"}]}}]'),
                  ('i3', 'lab', 'invoice-api', 'latency', 1, 0, 1, 0, '{}', 1, 7, '[]');
                """,
                cancellationToken);

        await Database().InitializeAsync(cancellationToken);

        // The same copy observed later under a second grouping, as a poll records it.
        await using var reopened = await Database().OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(
            reopened,
            "INSERT INTO finding_attributes VALUES ('s2', 'daemon-a', 'team', 'shop', 'team', 'shop', 2);",
            cancellationToken);
    }

    private static async Task<Dictionary<string, AccessCopy>> CopiesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        return await ReadAsync(
            connection,
            """
            SELECT fs.signature || ' ' || fs.source_id, fs.source_id, fs.service,
                   fa.grouping_key, fa.grouping_value, fa.attr_key, fa.attr_value
            FROM finding_sources AS fs
            LEFT JOIN finding_attributes AS fa ON fa.signature = fs.signature AND fa.source_id = fs.source_id
            """,
            cancellationToken);
    }

    private static async Task<Dictionary<string, AccessCopy>> IncidentsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        return await ReadAsync(
            connection,
            """
            SELECT i.id || ' ' || i.source_id, i.source_id, i.service, '', '', ia.attr_key, ia.attr_value
            FROM incidents AS i
            LEFT JOIN incident_attributes AS ia ON ia.id = i.id AND ia.source_id = i.source_id
            """,
            cancellationToken);
    }

    private static async Task<Dictionary<string, AccessCopy>> ReadAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var copies = new Dictionary<string, AccessCopy>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            if (!copies.TryGetValue(key, out var copy))
                copies[key] = copy = new AccessCopy(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    new List<AccessAttribute>());
            if (!reader.IsDBNull(5))
                ((List<AccessAttribute>)copy.Rows).Add(new AccessAttribute(
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        }

        return copies;
    }

    private static List<string> Matching(Dictionary<string, AccessCopy> copies, AccessScope scope)
    {
        return [.. copies.Where(copy => scope.Reads(copy.Value)).Select(copy => copy.Key).Order(StringComparer.Ordinal)];
    }

    private static async Task<List<string>> KeysAsync(
        SqliteConnection connection,
        string sql,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$access_patterns", scope.ToJson());
        command.Parameters.AddWithValue("$source_ids", DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            keys.Add(reader.GetString(0));
        return [.. keys.Order(StringComparer.Ordinal)];
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // "source=a;service=b|attribute=k=v": patterns split on '|', parts on ';', each on its first '='.
    private static IEnumerable<AccessPattern> Patterns(string patterns)
    {
        return patterns.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(pattern =>
        {
            var parts = pattern.Split(';').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);
            return Compile(new PatternOptions
            {
                Source = parts.GetValueOrDefault("source"),
                Service = parts.GetValueOrDefault("service"),
                Attribute = parts.GetValueOrDefault("attribute")
            });
        });
    }

    private static AccessPattern Compile(PatternOptions pattern)
    {
        return AccessPattern.Compile(pattern);
    }
}
