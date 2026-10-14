using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class FindingIngestionTests : IDisposable
{
    private const long DayMs = 86_400_000;

    private static readonly SourceSnapshot ProductionA = new("production-a", "Production A", "production", "0.11.2");

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-ingestion-{Guid.NewGuid():N}.db");

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "daemon-findings-0.11.2.json");

    public void Dispose()
    {
        TestPool.ClearFor(_databasePath);
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    [Fact]
    public async Task Parser_preserves_the_opaque_envelope_and_indexes_required_fields()
    {
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(
            FixturePath,
            TestContext.Current.CancellationToken));

        var finding = Assert.Single(batch.Findings);
        Assert.Equal("blocking_wait:rider-smoke:checkout:slow-path", finding.Signature);
        Assert.Equal("rider-smoke", finding.Service);
        Assert.Equal("POST /checkout", finding.Endpoint);
        Assert.Equal(1786183200000L, finding.FirstSeenMs);
        Assert.Contains("future_contract_field", finding.EnvelopeJson, StringComparison.Ordinal);
    }

    // The daemon files a finding under its first grouping attribute, the one its own filter matches.
    [Theory]
    [InlineData("[{\"key\": \"k8s.namespace.name\", \"value\": \"test\"}, {\"key\": \"service.namespace\", \"value\": \"other\"}]", "test")]
    [InlineData("[]", null)]
    [InlineData("[{\"key\": \"k8s.namespace.name\", \"value\": \"\"}]", null)]
    [InlineData("[{\"key\": \"k8s.namespace.name\"}]", null)]
    [InlineData("[{\"key\": \"k8s.namespace.name\", \"value\": 3}]", null)]
    [InlineData("[\"test\"]", null)]
    [InlineData("{\"value\": \"test\"}", null)]
    public async Task Parser_reads_the_first_grouping_value(string grouping, string? expected)
    {
        var payload = (await File.ReadAllTextAsync(FixturePath, TestContext.Current.CancellationToken))
            .Replace("\"grouping\": []", $"\"grouping\": {grouping}", StringComparison.Ordinal);

        var finding = Assert.Single(FindingParser.Parse(Encoding.UTF8.GetBytes(payload)).Findings);
        Assert.Equal(expected, finding.Grouping);
    }

    [Fact]
    public async Task Upsert_merges_signature_and_tracks_each_source_and_heartbeat()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));

        await database.UpsertBatchAsync(
            new SourceSnapshot("production-a", "Production A", "production", "0.11.2"),
            batch,
            1000,
            cancellationToken);
        await database.UpsertBatchAsync(
            new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2"),
            batch,
            2000,
            cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM findings;", cancellationToken));
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM finding_sources;", cancellationToken));
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM endpoint_heartbeats;", cancellationToken));
        Assert.Equal(1000L, await ScalarAsync(connection, "SELECT first_seen_ms FROM findings;", cancellationToken));
        Assert.Equal(2000L, await ScalarAsync(connection, "SELECT last_seen_ms FROM findings;", cancellationToken));
        Assert.Equal(
            "daemon_production",
            await TextScalarAsync(connection, "SELECT max_confidence FROM findings;", cancellationToken));
    }

    [Fact]
    public async Task Upsert_keeps_the_newest_observation_when_an_older_one_commits_late()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var older = batch.Findings[0] with { Severity = "warning", TraceId = "stale-trace" };

        await database.UpsertBatchAsync(
            new SourceSnapshot("production-a", "Production A", "production", "0.11.2"),
            batch,
            2000,
            cancellationToken);
        await database.UpsertBatchAsync(
            new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2"),
            new ParsedBatch([older], 0),
            1000,
            cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(
            "critical",
            await TextScalarAsync(connection, "SELECT severity FROM findings;", cancellationToken));
        Assert.Equal(
            "rider-trace-file-line",
            await TextScalarAsync(connection, "SELECT sample_trace_id FROM findings;", cancellationToken));
        Assert.Equal(
            2000L,
            await ScalarAsync(connection, "SELECT last_seen_ms FROM findings;", cancellationToken));
    }

    [Fact]
    public async Task Upsert_honors_the_daemon_reported_first_seen_and_keeps_the_hub_clock_for_last_seen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        // A realistic poll clock: after the fixture's first_seen_ms, so the
        // daemon-reported birth survives the clamp instead of being cut to it.
        const long firstObservedAt = 1786190000000;
        const long secondObservedAt = firstObservedAt + 3_600_000;
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");

        await database.UpsertBatchAsync(source, batch, firstObservedAt, cancellationToken);
        await database.UpsertBatchAsync(source, batch, secondObservedAt, cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(
            1786183200000L,
            await ScalarAsync(connection, "SELECT first_seen_ms FROM findings;", cancellationToken));
        Assert.Equal(
            secondObservedAt,
            await ScalarAsync(connection, "SELECT last_seen_ms FROM findings;", cancellationToken));
        Assert.Equal(
            1786183200000L,
            await ScalarAsync(connection, "SELECT first_seen_ms FROM finding_sources;", cancellationToken));
        Assert.Equal(
            secondObservedAt,
            await ScalarAsync(connection, "SELECT last_seen_ms FROM finding_sources;", cancellationToken));
    }

    [Fact]
    public async Task Parser_rejects_a_first_seen_below_the_epoch_ms_sanity_floor()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(
            FixturePath,
            TestContext.Current.CancellationToken));
        // A seconds-unit bug: the same instant, a thousand times smaller.
        var seconds = fixture.RootElement[0].GetRawText()
            .Replace("1786183200000", "1786183200", StringComparison.Ordinal);
        var payload = Encoding.UTF8.GetBytes($"[{seconds}]");

        var batch = FindingParser.Parse(payload);

        var finding = Assert.Single(batch.Findings);
        Assert.Null(finding.FirstSeenMs);
    }

    [Fact]
    public async Task Parser_rejects_only_the_invalid_array_element()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(
            FixturePath,
            TestContext.Current.CancellationToken));
        var valid = fixture.RootElement[0].GetRawText();
        var payload = Encoding.UTF8.GetBytes($"[{valid},{{\"finding\":{{}}}},{valid}]");

        var batch = FindingParser.Parse(payload);

        Assert.Equal(2, batch.Findings.Count);
        Assert.Equal(1, batch.RejectedCount);
    }

    // SQLite reads the first of two properties of one name and System.Text.Json the last, so
    // a copy's recorded attributes and its grouping would disagree: such a body is refused.
    [Theory]
    [InlineData("""
                "grouping": [{"key": "team", "value": "a"}], "grouping": [{"key": "team", "value": "b"}]
                """)]
    [InlineData("""
                "grouping": [{"key": "team", "value": "a", "value": "b"}]
                """)]
    public async Task A_body_that_repeats_a_property_name_is_refused(string grouping)
    {
        var fixture = await File.ReadAllTextAsync(FixturePath, TestContext.Current.CancellationToken);
        var repeated = fixture.Replace("\"grouping\": []", grouping, StringComparison.Ordinal);
        Assert.NotEqual(fixture, repeated);

        Assert.Throws<InvalidDataException>(() => FindingParser.Parse(Encoding.UTF8.GetBytes(repeated)));
        Assert.Throws<InvalidDataException>(() => FindingParser.ParseImport(
            Encoding.UTF8.GetBytes($$"""{"producer_version": "0.26.2", "findings": {{repeated}}}""")));
    }

    [Fact]
    public async Task Upsert_rolls_back_the_whole_batch_when_a_related_write_fails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                                  CREATE TRIGGER fail_finding_source BEFORE INSERT ON finding_sources
                                  BEGIN SELECT RAISE(ABORT, 'test rollback'); END;
                                  """;
            await trigger.ExecuteNonQueryAsync(cancellationToken);
        }

        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));

        await Assert.ThrowsAsync<SqliteException>(() => database.UpsertBatchAsync(
            new SourceSnapshot("production-a", "Production A", "production", "0.11.2"),
            batch,
            1000,
            cancellationToken));

        await using var reopened = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(0L, await ScalarAsync(reopened, "SELECT COUNT(*) FROM findings;", cancellationToken));
    }

    [Fact]
    public async Task Upsert_links_a_template_mutation_to_its_lone_predecessor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await database.UpsertBatchAsync(source, batch, 1000, cancellationToken);

        var mutated = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:mutated-path",
            TemplateHash = "mutated-template-hash"
        };
        await database.UpsertBatchAsync(source, new ParsedBatch([mutated], 0), 2000, cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM finding_lineage;", cancellationToken));
        Assert.Equal(
            batch.Findings[0].Signature,
            await TextScalarAsync(connection, "SELECT predecessor_signature FROM finding_lineage;", cancellationToken));
        Assert.Equal(
            1000L,
            await ScalarAsync(connection, "SELECT predecessor_first_seen_ms FROM finding_lineage;", cancellationToken));
    }

    [Fact]
    public async Task Upsert_does_not_guess_between_two_lineage_candidates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        var second = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:other-path",
            TemplateHash = "other-template-hash"
        };
        // Same batch: two current problems on the endpoint, never a mutation.
        await database.UpsertBatchAsync(
            source, new ParsedBatch([batch.Findings[0], second], 0), 1000, cancellationToken);

        var mutated = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:mutated-path",
            TemplateHash = "mutated-template-hash"
        };
        await database.UpsertBatchAsync(source, new ParsedBatch([mutated], 0), 2000, cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM finding_lineage;", cancellationToken));
    }

    [Fact]
    public async Task Query_walks_the_lineage_chain_to_the_original_first_seen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await database.UpsertBatchAsync(source, batch, 1000, cancellationToken);
        var second = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:v2",
            TemplateHash = "hash-v2"
        };
        await database.UpsertBatchAsync(source, new ParsedBatch([second], 0), 2000, cancellationToken);
        var third = second with
        {
            Signature = "blocking_wait:rider-smoke:checkout:v3",
            TemplateHash = "hash-v3"
        };
        await database.UpsertBatchAsync(source, new ParsedBatch([third], 0), 3000, cancellationToken);

        var rows = await database.QueryFindingsAsync(
            new FindingQuery(null, null, null, 100), cancellationToken);

        // v2 replaced v1, so only v3 keeps a live predecessor chain of 2.
        var successor = Assert.Single(rows, row => row.Signature == "blocking_wait:rider-smoke:checkout:v3");
        Assert.NotNull(successor.Lineage);
        Assert.Equal(2, successor.Lineage.Predecessors);
        Assert.Equal(1000L, successor.Lineage.OriginalFirstSeenMs);
    }

    /// <summary>
    ///     CREATE TABLE IF NOT EXISTS is a no-op on an existing table, so a
    ///     database written before the denormalization must be upgraded in
    ///     place rather than left with the old three-column lineage table.
    /// </summary>
    [Fact]
    public async Task Initialize_adds_the_lineage_columns_to_a_pre_denormalization_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var seed = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await seed.OpenAsync(cancellationToken);
            await using var create = seed.CreateCommand();
            create.CommandText = """
                                 CREATE TABLE finding_lineage (
                                   successor_signature TEXT NOT NULL,
                                   predecessor_signature TEXT NOT NULL,
                                   predecessor_first_seen_ms INTEGER NOT NULL,
                                   linked_at_ms INTEGER NOT NULL,
                                   method TEXT NOT NULL,
                                   PRIMARY KEY(successor_signature, predecessor_signature)
                                 );
                                 INSERT INTO finding_lineage VALUES ('v2', 'v1', 1000, 2000, 'endpoint_template');
                                 """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var check = connection.CreateCommand();
        check.CommandText =
            "SELECT origin_first_seen_ms, depth FROM finding_lineage WHERE successor_signature = 'v2';";
        await using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(1000L, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt32(1));
        }

        // The columns existing is not enough: the upgraded table must
        // accept the insert the production path issues.
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
                             INSERT INTO finding_lineage(
                               successor_signature, predecessor_signature, predecessor_first_seen_ms,
                               origin_first_seen_ms, depth, linked_at_ms, method)
                             VALUES ('v3', 'v2', 2000, 1000, 2, 3000, 'endpoint_template');
                             """;
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(cancellationToken));
    }

    /// <summary>
    ///     The chain's origin is denormalized at link time, so purging the
    ///     intermediate hop must not shorten the surviving finding's lineage.
    /// </summary>
    [Fact]
    public async Task Lineage_survives_the_purge_of_an_intermediate_hop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await database.UpsertBatchAsync(source, batch, 1000, cancellationToken);
        var second = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:v2",
            TemplateHash = "hash-v2"
        };
        await database.UpsertBatchAsync(source, new ParsedBatch([second], 0), 2000, cancellationToken);
        var third = second with
        {
            Signature = "blocking_wait:rider-smoke:checkout:v3",
            TemplateHash = "hash-v3"
        };
        await database.UpsertBatchAsync(source, new ParsedBatch([third], 0), 3000, cancellationToken);

        // Purge v1 and v2 (last_seen 1000 and 2000), keep v3.
        // A run cutoff of 0 purges no runs: this case is about findings.
        await database.PurgeAsync(2500, 0, 0, cancellationToken);

        var rows = await database.QueryFindingsAsync(
            new FindingQuery(null, null, null, 100), cancellationToken);
        var survivor = Assert.Single(rows, row => row.Signature == "blocking_wait:rider-smoke:checkout:v3");
        Assert.NotNull(survivor.Lineage);
        Assert.Equal(2, survivor.Lineage.Predecessors);
        Assert.Equal(1000L, survivor.Lineage.OriginalFirstSeenMs);
    }

    /// <summary>
    ///     A heartbeat from a source that never carried the finding proves
    ///     nothing about the source that did: the status must stay
    ///     not_observed while the witnessing source is silent.
    /// </summary>
    [Fact]
    public async Task Status_ignores_heartbeats_from_sources_that_never_saw_the_finding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_786_190_000_000));
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            clock);
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var witnessing = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await database.UpsertBatchAsync(
            witnessing, batch, clock.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);

        clock.Advance(TimeSpan.FromDays(8));
        // A sibling source heartbeats the same service and endpoint through
        // a finding of its own, while production-a stays silent.
        var sibling = new SourceSnapshot("production-b", "Production B", "production", "0.11.2");
        var other = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:other",
            TemplateHash = "other-hash"
        };
        await database.UpsertBatchAsync(
            sibling,
            new ParsedBatch([other], 0),
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            cancellationToken);

        var rows = await database.QueryFindingsAsync(
            new FindingQuery(null, null, null, 100), cancellationToken);
        Assert.Equal(
            "not_observed",
            Assert.Single(rows, row => row.Signature == batch.Findings[0].Signature).Status);
    }

    [Fact]
    public async Task Status_derives_from_heartbeats_and_source_reachability()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_786_190_000_000));
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            clock);
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        var query = new FindingQuery(null, null, null, 100);

        var seededAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
        await database.UpsertBatchAsync(source, batch, seededAt, cancellationToken);
        var seeded = Assert.Single(await database.QueryFindingsAsync(query, cancellationToken));
        Assert.Equal("active", seeded.Status);

        // Past the grace with a silent endpoint: nothing proves anything.
        clock.Advance(TimeSpan.FromDays(8));
        var quiet = Assert.Single(await database.QueryFindingsAsync(query, cancellationToken));
        Assert.Equal("not_observed", quiet.Status);

        // The endpoint heartbeats again through another finding while the
        // old one stays silent: presumably fixed.
        var other = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:other",
            TemplateHash = "other-hash"
        };
        await database.UpsertBatchAsync(
            source,
            new ParsedBatch([other], 0),
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            cancellationToken);
        var rows = await database.QueryFindingsAsync(query, cancellationToken);
        Assert.Equal(
            "likely_resolved",
            Assert.Single(rows, row => row.Signature == batch.Findings[0].Signature).Status);

        // An unreachable source withdraws the presumption.
        await database.MarkSourceFailureAsync(
            "production-a",
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            "timeout",
            cancellationToken);
        rows = await database.QueryFindingsAsync(query, cancellationToken);
        Assert.Equal(
            "not_observed",
            Assert.Single(rows, row => row.Signature == batch.Findings[0].Signature).Status);
    }

    [Fact]
    public async Task Status_filter_applies_before_the_page_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_786_190_000_000));
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            clock);
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await database.UpsertBatchAsync(
            source, batch, clock.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
        clock.Advance(TimeSpan.FromDays(8));
        var fresh = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:fresh",
            TemplateHash = "fresh-hash"
        };
        await database.UpsertBatchAsync(
            source,
            new ParsedBatch([fresh], 0),
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            cancellationToken);

        var active = await database.QueryFindingsAsync(
            new FindingQuery(null, null, null, 1, Status: "active"), cancellationToken);
        Assert.Equal("blocking_wait:rider-smoke:checkout:fresh", Assert.Single(active).Signature);

        var resolved = await database.QueryFindingsAsync(
            new FindingQuery(null, null, null, 1, Status: "likely_resolved"),
            cancellationToken);
        Assert.Equal(batch.Findings[0].Signature, Assert.Single(resolved).Signature);
    }

    /// <summary>
    ///     A scope answers for itself: production still carrying the finding
    ///     says nothing about staging, and neither does production's heartbeat.
    /// </summary>
    [Fact]
    public async Task The_scoped_status_is_the_environments_own()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_786_190_000_000));
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            clock);
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var staging = new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2");
        var fleet = new FindingQuery(null, null, null, 100);
        var scope = fleet with { SourceIds = ["staging-a"] };
        var signature = batch.Findings[0].Signature;

        await database.UpsertBatchAsync(staging, batch, clock.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
        clock.Advance(TimeSpan.FromDays(8));
        await database.UpsertBatchAsync(
            ProductionA, batch, clock.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);

        Assert.Equal("active", Assert.Single(await database.QueryFindingsAsync(fleet, cancellationToken)).Status);
        // Production heartbeats the endpoint and saw the finding, from outside the scope.
        Assert.Equal("not_observed", Assert.Single(await database.QueryFindingsAsync(scope, cancellationToken)).Status);

        // Staging's own endpoint heartbeats again through another finding.
        var other = batch.Findings[0] with
        {
            Signature = "blocking_wait:rider-smoke:checkout:other",
            TemplateHash = "other-hash"
        };
        await database.UpsertBatchAsync(
            staging,
            new ParsedBatch([other], 0),
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            cancellationToken);
        Assert.Equal(
            "likely_resolved",
            Assert.Single(
                await database.QueryFindingsAsync(scope, cancellationToken),
                row => row.Signature == signature).Status);
        Assert.Equal(
            "active",
            Assert.Single(
                await database.QueryFindingsAsync(fleet, cancellationToken),
                row => row.Signature == signature).Status);
    }

    [Fact]
    public async Task A_scope_of_several_sources_serves_its_freshest_copy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));

        await database.UpsertBatchAsync(ProductionA, Copy(batch, "a", "info"), 1000, cancellationToken);
        await database.UpsertBatchAsync(
            new SourceSnapshot("production-b", "Production B", "production", "0.11.2"),
            Copy(batch, "b", "warning"),
            3000,
            cancellationToken);
        // Fresher than both, and outside the scope.
        await database.UpsertBatchAsync(
            new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2"),
            Copy(batch, "staging", "critical"),
            5000,
            cancellationToken);

        var scope = new FindingQuery(null, null, null, 100, SourceIds: ["production-a", "production-b"]);
        var row = Assert.Single(await database.QueryFindingsAsync(scope, cancellationToken));

        Assert.Equal("""{"copy":"b"}""", row.EnvelopeJson);
        Assert.Equal(1000, row.FirstSeenMs);
        Assert.Equal(3000, row.LastSeenMs);
        Assert.Equal(["production-a", "production-b"], row.Sources.Select(source => source.SourceId));
        Assert.Single(await database.QueryFindingsAsync(scope with { Severity = "warning" }, cancellationToken));
        Assert.Empty(await database.QueryFindingsAsync(scope with { Severity = "critical" }, cancellationToken));
    }

    [Fact]
    public async Task Each_source_keeps_its_own_envelope_and_severity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var staged = batch.Findings[0] with { Severity = "warning", EnvelopeJson = """{"copy":"staging"}""" };

        await database.UpsertBatchAsync(ProductionA, batch, 2000, cancellationToken);
        await database.UpsertBatchAsync(
            new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2"),
            new ParsedBatch([staged], 0),
            1000,
            cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(
            $"critical {batch.Findings[0].EnvelopeJson}",
            await SourceCopyAsync(connection, "production-a", cancellationToken));
        // The shared findings row kept production's fresher copy, staging's own survives here.
        Assert.Equal(
            """warning {"copy":"staging"}""",
            await SourceCopyAsync(connection, "staging-a", cancellationToken));
    }

    /// <summary>
    ///     A push and a poll of one source each read the clock before waiting
    ///     on the write gate, so the older observation can commit last.
    /// </summary>
    [Fact]
    public async Task A_source_keeps_its_newest_copy_when_an_older_observation_commits_late()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var late = batch.Findings[0] with { Severity = "warning", EnvelopeJson = """{"copy":"late"}""" };

        await database.UpsertBatchAsync(ProductionA, batch, 2000, cancellationToken);
        Assert.True(await database.TryUpsertBatchAsync(
            ProductionA, new ParsedBatch([late], 0), 1000, cancellationToken));

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(
            $"critical {batch.Findings[0].EnvelopeJson}",
            await SourceCopyAsync(connection, "production-a", cancellationToken));
    }

    [Fact]
    public async Task First_observed_day_is_set_once_and_never_moves()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));

        await database.UpsertBatchAsync(ProductionA, batch, 5 * DayMs + 1000, cancellationToken);
        await database.UpsertBatchAsync(ProductionA, batch, 7 * DayMs, cancellationToken);
        // Not even backwards, for an observation that commits late.
        await database.UpsertBatchAsync(ProductionA, batch, 3 * DayMs, cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(
            5L,
            await ScalarAsync(connection, "SELECT first_observed_day FROM finding_sources;", cancellationToken));
        Assert.Equal("3 critical 3,5 critical 3,7 critical 3", await ObservationsAsync(connection, cancellationToken));
    }

    [Fact]
    public async Task The_worst_severity_of_the_day_is_kept_and_a_repeated_observation_is_a_no_op()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var milder = new ParsedBatch([batch.Findings[0] with { Severity = "warning" }], 0);

        await database.UpsertBatchAsync(ProductionA, milder, 9 * DayMs + 1000, cancellationToken);
        await database.UpsertBatchAsync(ProductionA, batch, 9 * DayMs + 2000, cancellationToken);

        // From here on, rewriting a day's row aborts the batch.
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                                  CREATE TRIGGER fail_observation_rewrite BEFORE UPDATE ON finding_observations
                                  BEGIN SELECT RAISE(ABORT, 'observation rewritten'); END;
                                  """;
            await trigger.ExecuteNonQueryAsync(cancellationToken);
        }

        await database.UpsertBatchAsync(ProductionA, batch, 9 * DayMs + 3000, cancellationToken);
        await database.UpsertBatchAsync(ProductionA, milder, 9 * DayMs + 4000, cancellationToken);
        await database.UpsertBatchAsync(ProductionA, milder, 10 * DayMs, cancellationToken);

        Assert.Equal("9 critical 3,10 warning 2", await ObservationsAsync(connection, cancellationToken));
    }

    [Theory]
    [InlineData("critical", 3)]
    [InlineData("warning", 2)]
    [InlineData("info", 1)]
    [InlineData("notice", 0)]
    public async Task Severity_is_ranked_when_the_observation_is_written(string severity, int expectedRank)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));

        await database.UpsertBatchAsync(
            ProductionA,
            new ParsedBatch([batch.Findings[0] with { Severity = severity }], 0),
            1000,
            cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal($"0 {severity} {expectedRank}", await ObservationsAsync(connection, cancellationToken));
    }

    [Fact]
    public async Task A_window_matches_an_observation_day_and_not_a_day_outside_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var fleet = new FindingQuery(null, null, null, 100);

        await database.UpsertBatchAsync(ProductionA, batch, 10 * DayMs + 1000, cancellationToken);

        Assert.Single(await database.QueryFindingsAsync(Days(10, 10), cancellationToken));
        Assert.Single(await database.QueryFindingsAsync(Days(8, 12), cancellationToken));
        Assert.Empty(await database.QueryFindingsAsync(Days(8, 9), cancellationToken));
        Assert.Empty(await database.QueryFindingsAsync(Days(11, 12), cancellationToken));
        // A day is matched whole, so a window that ends before the observation itself still holds it.
        Assert.Single(await database.QueryFindingsAsync(
            fleet with { FromMs = 10 * DayMs, ToMs = 10 * DayMs + 500 }, cancellationToken));
        // Either side stays open when its bound is absent.
        Assert.Single(await database.QueryFindingsAsync(fleet with { FromMs = 10 * DayMs }, cancellationToken));
        Assert.Empty(await database.QueryFindingsAsync(fleet with { FromMs = 11 * DayMs }, cancellationToken));
        Assert.Single(await database.QueryFindingsAsync(fleet with { ToMs = 10 * DayMs }, cancellationToken));
        Assert.Empty(await database.QueryFindingsAsync(fleet with { ToMs = 10 * DayMs - 1 }, cancellationToken));
    }

    /// <summary>
    ///     The daemon saw the finding on day 5 and the Hub recorded it first on
    ///     day 10. Nothing says it went away in between, so it reads as present.
    /// </summary>
    [Fact]
    public async Task The_time_before_the_first_recorded_day_reads_as_presence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var early = new ParsedBatch([batch.Findings[0] with { FirstSeenMs = 5 * DayMs + 1000 }], 0);

        await database.UpsertBatchAsync(ProductionA, early, 10 * DayMs + 1000, cancellationToken);

        Assert.Single(await database.QueryFindingsAsync(Days(6, 7), cancellationToken));
        Assert.Single(await database.QueryFindingsAsync(Days(3, 5), cancellationToken));
        Assert.Empty(await database.QueryFindingsAsync(Days(3, 4), cancellationToken));
    }

    [Fact]
    public async Task A_gap_between_two_observation_days_stays_a_gap_after_the_purge()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));

        await database.UpsertBatchAsync(ProductionA, batch, 10 * DayMs + 1000, cancellationToken);
        await database.UpsertBatchAsync(ProductionA, batch, 14 * DayMs + 1000, cancellationToken);

        Assert.Empty(await database.QueryFindingsAsync(Days(11, 13), cancellationToken));

        // Drops day 10 and keeps the pair, last seen on day 14.
        await database.PurgeAsync(12 * DayMs, 0, 0, cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal("14 critical 3", await ObservationsAsync(connection, cancellationToken));
        // A first day read back as MIN(day) would now be 14 and fill the gap.
        Assert.Empty(await database.QueryFindingsAsync(Days(11, 13), cancellationToken));
        Assert.Single(await database.QueryFindingsAsync(Days(14, 14), cancellationToken));
    }

    [Fact]
    public async Task A_window_over_a_purged_day_lists_nothing_for_a_finding_still_present()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));

        foreach (var day in new long[] { 10, 11, 14 })
            await database.UpsertBatchAsync(ProductionA, batch, day * DayMs + 1000, cancellationToken);
        Assert.Single(await database.QueryFindingsAsync(Days(11, 11), cancellationToken));

        // Drops days 10 and 11. The first recorded day stays 10, so day 11 is not assumed either.
        await database.PurgeAsync(12 * DayMs, 0, 0, cancellationToken);

        Assert.Empty(await database.QueryFindingsAsync(Days(11, 11), cancellationToken));
        Assert.Single(await database.QueryFindingsAsync(Days(14, 14), cancellationToken));
    }

    /// <summary>
    ///     A pair written before the observation days existed has none, and reads
    ///     as present from its first to its last sighting rather than never.
    /// </summary>
    [Fact]
    public async Task A_pair_with_no_recorded_day_matches_from_its_first_seen_to_its_last_seen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));

        await database.UpsertBatchAsync(ProductionA, batch, 10 * DayMs + 1000, cancellationToken);
        await database.UpsertBatchAsync(ProductionA, batch, 14 * DayMs + 1000, cancellationToken);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  UPDATE finding_sources SET first_observed_day = NULL;
                                  DELETE FROM finding_observations;
                                  """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        Assert.Single(await database.QueryFindingsAsync(Days(12, 13), cancellationToken));
        Assert.Single(await database.QueryFindingsAsync(Days(14, 20), cancellationToken));
        Assert.Empty(await database.QueryFindingsAsync(Days(8, 9), cancellationToken));
        Assert.Empty(await database.QueryFindingsAsync(Days(15, 16), cancellationToken));
    }

    [Fact]
    public async Task A_window_honours_the_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var staging = new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2");

        await database.UpsertBatchAsync(staging, batch, 10 * DayMs + 1000, cancellationToken);
        await database.UpsertBatchAsync(ProductionA, batch, 14 * DayMs + 1000, cancellationToken);

        Assert.Single(await database.QueryFindingsAsync(Days(14, 14), cancellationToken));
        Assert.Single(await database.QueryFindingsAsync(
            Days(14, 14) with { SourceIds = ["production-a"] }, cancellationToken));
        // Production observed it that day, from outside the scope.
        Assert.Empty(await database.QueryFindingsAsync(
            Days(14, 14) with { SourceIds = ["staging-a"] }, cancellationToken));
        Assert.Single(await database.QueryFindingsAsync(
            Days(10, 10) with { SourceIds = ["staging-a"] }, cancellationToken));
    }

    [Fact]
    public async Task A_window_applies_before_the_page_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var older = batch.Findings[0];
        var fresher = older with
        {
            Signature = "blocking_wait:rider-smoke:checkout:fresher",
            TemplateHash = "fresher-hash"
        };

        await database.UpsertBatchAsync(ProductionA, new ParsedBatch([older], 0), 10 * DayMs + 1000, cancellationToken);
        await database.UpsertBatchAsync(
            ProductionA, new ParsedBatch([fresher], 0), 14 * DayMs + 1000, cancellationToken);

        // Applied after the limit, the window would cut the page down to the fresher row, then empty it.
        var page = await database.QueryFindingsAsync(Days(10, 10) with { Limit = 1 }, cancellationToken);
        Assert.Equal(older.Signature, Assert.Single(page).Signature);
    }

    [Fact]
    public async Task A_poll_and_an_import_each_record_every_pair_under_the_first_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = await GroupedAsync(
            """[{"key": "k8s.namespace.name", "value": "shop-ns"}, {"key": "team", "value": "shop"}]""",
            cancellationToken);

        await database.UpsertBatchAsync(ProductionA, batch, 1000, cancellationToken);
        Assert.True(await database.TryUpsertBatchAsync(
            new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2"), batch, 2000, cancellationToken));

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(
            "production-a k8s.namespace.name=shop-ns k8s.namespace.name=shop-ns 1000,"
            + "production-a k8s.namespace.name=shop-ns team=shop 1000,"
            + "staging-a k8s.namespace.name=shop-ns k8s.namespace.name=shop-ns 2000,"
            + "staging-a k8s.namespace.name=shop-ns team=shop 2000",
            await AttributesAsync(connection, cancellationToken));
    }

    [Fact]
    public async Task An_observation_without_grouping_records_the_marker_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);

        await database.UpsertBatchAsync(
            ProductionA,
            FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken)),
            1000,
            cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal("production-a = = 1000", await AttributesAsync(connection, cancellationToken));
    }

    // The engine sends at most eight entries, each of 1 to 256 characters: anything else is
    // skipped, and a malformed entry never fails the batch.
    [Fact]
    public async Task Only_the_first_eight_well_formed_entries_are_recorded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var longest = new string('v', 256);
        var batch = await GroupedAsync(
            $$"""
              [{"key": "team", "value": "shop"}, "plain", {"key": "empty", "value": ""},
               {"key": "long", "value": "{{longest}}v"}, {"key": "k4", "value": "v4"}, 3,
               {"key": "k6", "value": "v6"}, {"key": "k7", "value": "{{longest}}"},
               {"key": "ninth", "value": "v8"}]
              """,
            cancellationToken);

        await database.UpsertBatchAsync(ProductionA, batch, 1000, cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(
            "production-a team=shop k4=v4 1000,production-a team=shop k6=v6 1000,"
            + $"production-a team=shop k7={longest} 1000,production-a team=shop team=shop 1000",
            await AttributesAsync(connection, cancellationToken));
    }

    [Theory]
    [InlineData("\"team\"")]
    [InlineData("{\"key\": \"team\"}")]
    [InlineData("{\"key\": \"team\", \"value\": \"\"}")]
    [InlineData("{\"key\": \"\", \"value\": \"shop\"}")]
    [InlineData("{\"key\": \"team\", \"value\": 3}")]
    [InlineData("{\"key\": \"team\", \"value\": {\"name\": \"shop\"}}")]
    public async Task An_invalid_first_entry_records_the_marker_only(string first)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = await GroupedAsync($$"""[{{first}}, {"key": "team", "value": "shop"}]""", cancellationToken);

        await database.UpsertBatchAsync(ProductionA, batch, 1000, cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal("production-a = = 1000", await AttributesAsync(connection, cancellationToken));
    }

    [Fact]
    public async Task An_attribute_keeps_its_latest_observation_when_an_older_one_commits_late()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = await GroupedAsync("""[{"key": "team", "value": "shop"}]""", cancellationToken);

        await database.UpsertBatchAsync(ProductionA, batch, 2000, cancellationToken);
        Assert.True(await database.TryUpsertBatchAsync(ProductionA, batch, 1000, cancellationToken));

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal("production-a team=shop team=shop 2000", await AttributesAsync(connection, cancellationToken));
    }

    [Fact]
    public async Task The_attribute_backfill_covers_every_copy_last_written_without_rows_on_every_boot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateDatabase().InitializeAsync(cancellationToken);
        // Copies an older build wrote: one with its own envelope, one before the per-source
        // envelope existed, one garbled, and one this build already covered.
        await ExecuteAsync(
            """
            INSERT INTO findings(
              signature, finding_json, service, finding_type, severity, endpoint,
              template_hash, sample_trace_id, first_seen_ms, last_seen_ms,
              max_confidence, max_confidence_rank)
            VALUES ('signature', '{"finding":{"grouping":[{"key":"team","value":"shared"}]}}',
              'checkout', 'slow_sql', 'warning', 'POST /checkout', 'template-hash', 'trace',
              500, 900, 'daemon_production', 4);
            INSERT INTO finding_sources(
              signature, source_id, source_name, environment, producer_version,
              first_seen_ms, last_seen_ms, finding_json)
            VALUES
              ('signature', 'own', 'Own', 'staging', '0.26.2', 500, 800,
               '{"finding":{"grouping":[{"key":"team","value":"shop"}]}}'),
              ('signature', 'legacy', 'Legacy', 'staging', '0.11.2', 500, 900, NULL),
              ('signature', 'garbled', 'Garbled', 'staging', '0.26.2', 500, 600, '{not json'),
              ('signature', 'covered', 'Covered', 'staging', '0.26.2', 500, 700,
               '{"finding":{"grouping":[{"key":"team","value":"ignored"}]}}');
            INSERT INTO finding_attributes VALUES ('signature', 'covered', '', '', '', '', 700);
            """,
            cancellationToken);

        await CreateDatabase().InitializeAsync(cancellationToken);
        await using (var connection = await CreateDatabase().OpenConnectionAsync(cancellationToken))
            Assert.Equal(
                "covered = = 700,own team=shop team=shop 800",
                await AttributesAsync(connection, cancellationToken));

        // A second boot finds nothing left to write.
        await ExecuteAsync(
            """
            CREATE TRIGGER fail_attribute_write BEFORE INSERT ON finding_attributes
            BEGIN SELECT RAISE(ABORT, 'attributes backfilled twice'); END;
            """,
            cancellationToken);
        await CreateDatabase().InitializeAsync(cancellationToken);
        await ExecuteAsync("DROP TRIGGER fail_attribute_write;", cancellationToken);

        // An image rolled back below this build observes the copy again, without rows.
        await ExecuteAsync(
            """
            UPDATE finding_sources SET last_seen_ms = 900,
              finding_json = '{"finding":{"grouping":[{"key":"team","value":"billing"}]}}'
            WHERE source_id = 'own';
            """,
            cancellationToken);
        await CreateDatabase().InitializeAsync(cancellationToken);

        await using var reopened = await CreateDatabase().OpenConnectionAsync(cancellationToken);
        Assert.Equal(
            "covered = = 700,own team=billing team=billing 900,own team=shop team=shop 800",
            await AttributesAsync(reopened, cancellationToken));
    }

    // findings.service is fleet-wide and the newest observation from any source overwrites
    // it, so a copy keeps the service its own source reported.
    [Fact]
    public async Task Each_copy_keeps_the_service_its_own_source_reported()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var reused = batch.Findings[0] with { Service = "other-svc" };

        await database.UpsertBatchAsync(ProductionA, batch, 1000, cancellationToken);
        Assert.True(await database.TryUpsertBatchAsync(
            new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2"),
            new ParsedBatch([reused], 0),
            2000,
            cancellationToken));
        // An older observation of the same source committing late keeps the newer service.
        Assert.True(await database.TryUpsertBatchAsync(
            new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2"), batch, 1500, cancellationToken));

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal("other-svc", await TextScalarAsync(connection, "SELECT service FROM findings;", cancellationToken));
        Assert.Equal(
            "production-a rider-smoke,staging-a other-svc",
            await TextScalarAsync(
                connection,
                """
                SELECT group_concat(source_id || ' ' || service, ',')
                FROM (SELECT * FROM finding_sources ORDER BY source_id);
                """,
                cancellationToken));
    }

    [Fact]
    public async Task Boot_fills_a_missing_service_from_the_copys_own_envelope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = CreateDatabase();
        await database.InitializeAsync(cancellationToken);
        await database.UpsertBatchAsync(
            ProductionA,
            FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken)),
            1000,
            cancellationToken);
        // What an older build leaves: no service, and for some copies no usable envelope.
        await ExecuteAsync(
            """
            INSERT INTO finding_sources(
              signature, source_id, source_name, environment, producer_version,
              first_seen_ms, last_seen_ms, finding_json)
            SELECT signature, 'legacy', 'Legacy', 'staging', '0.11.2', 500, 900, NULL FROM findings
            UNION ALL
            SELECT signature, 'garbled', 'Garbled', 'staging', '0.26.2', 500, 900, '{not json' FROM findings
            UNION ALL
            SELECT signature, 'unnamed', 'Unnamed', 'staging', '0.26.2', 500, 900, '{"finding":{}}' FROM findings;
            UPDATE finding_sources SET service = NULL;
            """,
            cancellationToken);

        await CreateDatabase().InitializeAsync(cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        Assert.Equal(
            "garbled -,legacy -,production-a rider-smoke,unnamed -",
            await TextScalarAsync(
                connection,
                """
                SELECT group_concat(source_id || ' ' || COALESCE(service, '-'), ',')
                FROM (SELECT * FROM finding_sources ORDER BY source_id);
                """,
                cancellationToken));
    }

    // The fixture finding with the given grouping array in its envelope.
    private static async Task<ParsedBatch> GroupedAsync(string grouping, CancellationToken cancellationToken)
    {
        var payload = (await File.ReadAllTextAsync(FixturePath, cancellationToken))
            .Replace("\"grouping\": []", $"\"grouping\": {grouping}", StringComparison.Ordinal);
        return FindingParser.Parse(Encoding.UTF8.GetBytes(payload));
    }

    private static Task<string> AttributesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        return TextScalarAsync(
            connection,
            """
            SELECT group_concat(
              source_id || ' ' || grouping_key || '=' || grouping_value || ' '
              || attr_key || '=' || attr_value || ' ' || last_seen_ms, ',')
            FROM (SELECT * FROM finding_attributes ORDER BY source_id, attr_key, attr_value);
            """,
            cancellationToken);
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await CreateDatabase().OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // The fixture finding under another severity, its envelope reduced to a name a test can read back.
    private static ParsedBatch Copy(ParsedBatch batch, string name, string severity)
    {
        return new ParsedBatch(
            [batch.Findings[0] with { Severity = severity, EnvelopeJson = $$"""{"copy":"{{name}}"}""" }], 0);
    }

    // A window of whole days on the Hub clock, both ends included.
    private static FindingQuery Days(long from, long to)
    {
        return new FindingQuery(null, null, null, 100, FromMs: from * DayMs, ToMs: (to + 1) * DayMs - 1);
    }

    private static async Task<string> SourceCopyAsync(
        SqliteConnection connection,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT severity || ' ' || finding_json FROM finding_sources WHERE source_id = $source_id;";
        command.Parameters.AddWithValue("$source_id", sourceId);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static Task<string> ObservationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        return TextScalarAsync(
            connection,
            """
            SELECT group_concat(day || ' ' || severity || ' ' || severity_rank, ',')
            FROM (SELECT * FROM finding_observations ORDER BY day);
            """,
            cancellationToken);
    }

    private HubDatabase CreateDatabase()
    {
        return new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _databasePath }),
            TimeProvider.System);
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<string> TextScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
