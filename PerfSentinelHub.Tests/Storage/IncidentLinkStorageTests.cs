using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Links;
using PerfSentinelHub.Storage;
using PerfSentinelHub.Tests.Support;

namespace PerfSentinelHub.Tests.Storage;

public sealed class IncidentLinkStorageTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private const long DayMs = 86_400_000;
    private const long AtMs = 100 * DayMs + 3_600_000;
    private const long Day = AtMs / DayMs;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // One distinct source per test, so the shared database never mixes their rows.
    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string Frozen(params (string Signature, string Type, long Seen, long First)[] rows) =>
        "[" + string.Join(",", rows.Select(row =>
            $$"""{"finding":{"type":"{{row.Type}}","signature":"{{row.Signature}}"},"stored_at_ms":1,"first_seen_ms":{{row.First}},"seen_count":{{row.Seen}}}""")) + "]";

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await factory.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Token);
    }

    private Task SeedIncidentAsync(string id, string source, long atMs, string findings,
        string service = "checkout-svc", string kind = "oom_kill", string incidentJson = "{}") =>
        ExecuteAsync("""
                     INSERT INTO incidents(id, source_id, service, kind, at_ms, window_from_ms, window_to_ms,
                       finding_count, incident_json, first_seen_ms, last_seen_ms, findings_json)
                     VALUES ($id, $source, $service, $kind, $at, $from, $to, 1, $json, $at, $at, $findings);
                     """,
            ("$id", id), ("$source", source), ("$service", service), ("$kind", kind), ("$at", atMs),
            ("$from", atMs - 300_000), ("$to", atMs), ("$json", incidentJson), ("$findings", findings));

    private Task SeedDayAsync(string source, long day, string ledger = "l1", int gap = 0, long overflow = 0) =>
        ExecuteAsync("""
                     INSERT INTO detection_days(source_id, ledger_id, day, sealed, gap, overflow_detections,
                       shed_traces, ingest_rejected, sampling_rate, last_read_ms)
                     VALUES ($source, $ledger, $day, 1, $gap, $overflow, 0, 0, 1.0, 0);
                     """,
            ("$source", source), ("$ledger", ledger), ("$day", day), ("$gap", gap), ("$overflow", overflow));

    private Task SeedCountAsync(string source, string signature, long day, long detections, string ledger = "l1") =>
        ExecuteAsync("""
                     INSERT INTO detection_counts(signature, source_id, ledger_id, day, detections)
                     VALUES ($signature, $source, $ledger, $day, $detections);
                     """,
            ("$signature", signature), ("$source", source), ("$ledger", ledger), ("$day", day),
            ("$detections", detections));

    private async Task<long> CountLinksAsync(string id)
    {
        await using var connection = await factory.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM incident_finding_links WHERE incident_id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Context_sums_rows_of_one_signature_and_drops_empty_signatures()
    {
        var id = NewId();
        var source = NewId();
        await SeedIncidentAsync(id, source, AtMs, Frozen(
                ("sig-a", "n_plus_one_sql", 3, 900), ("sig-a", "n_plus_one_sql", 4, 700),
                ("", "slow_sql", 9, 1), ("sig-b", "slow_http", 2, 50)),
            incidentJson: """{"saturation":0.95}""");

        var context = await factory.Database.LoadLinkContextAsync(new IncidentKey(id, source), [source], Token);

        Assert.NotNull(context);
        Assert.Equal("checkout-svc", context.Service);
        Assert.Equal("oom_kill", context.Kind);
        Assert.Equal(AtMs, context.AtMs);
        Assert.Equal(AtMs - 300_000, context.WindowFromMs);
        Assert.Equal(AtMs, context.WindowToMs);
        Assert.Equal(0.95, context.Saturation);
        Assert.Equal(["sig-a", "sig-b"], context.Signatures.Select(s => s.Signature).Order());
        Assert.Equal(new FrozenSignature("sig-a", "n_plus_one_sql", 7, 700),
            context.Signatures.Single(s => s.Signature == "sig-a"));
    }

    [Fact]
    public async Task Context_is_null_when_the_incident_is_gone_and_saturation_null_when_not_a_number()
    {
        var id = NewId();
        var source = NewId();
        Assert.Null(await factory.Database.LoadLinkContextAsync(new IncidentKey(id, source), [source], Token));

        await SeedIncidentAsync(id, source, AtMs, Frozen(("sig-a", "slow_sql", 1, 1)),
            incidentJson: """{"saturation":"high"}""");
        var context = await factory.Database.LoadLinkContextAsync(new IncidentKey(id, source), [source], Token);
        Assert.NotNull(context);
        Assert.Null(context.Saturation);
    }

    [Fact]
    public async Task Baseline_counts_only_covered_days_without_gap_or_overflow()
    {
        var id = NewId();
        var source = NewId();
        await SeedIncidentAsync(id, source, AtMs, Frozen(("sig-a", "n_plus_one_sql", 1, AtMs - 1), ("sig-b", "slow_sql", 1, 1)));
        // Day-1 covered, day-2 overflowed, day-3 has a gap on one of its two ledgers, day-4 covered with
        // a second clean ledger, day-5..-7 missing; the incident's own day is outside the baseline.
        await SeedDayAsync(source, Day - 1);
        await SeedDayAsync(source, Day - 2, overflow: 5);
        await SeedDayAsync(source, Day - 3);
        await SeedDayAsync(source, Day - 3, ledger: "l2", gap: 1);
        await SeedDayAsync(source, Day - 4);
        await SeedDayAsync(source, Day - 4, ledger: "l2");
        await SeedDayAsync(source, Day);
        await SeedCountAsync(source, "sig-a", Day - 1, 10);
        await SeedCountAsync(source, "sig-a", Day - 2, 1000);
        await SeedCountAsync(source, "sig-a", Day - 4, 5);
        await SeedCountAsync(source, "sig-a", Day - 4, 6, ledger: "l2");
        await SeedCountAsync(source, "sig-a", Day, 99);
        await SeedCountAsync(source, "sig-b", Day - 2, 1);

        var context = await factory.Database.LoadLinkContextAsync(new IncidentKey(id, source), [source], Token);

        Assert.NotNull(context);
        Assert.Equal(new Baseline(2, 2, 21), context.Baselines["sig-a"]);
        Assert.Equal(new Baseline(2, 0, 0), context.Baselines["sig-b"]);
        // Seen in any of the 7 days, covered or not.
        Assert.Equal(["sig-a", "sig-b"], context.SeenBeforeDeploy.Order());
    }

    [Fact]
    public async Task Recurrence_counts_earlier_incidents_of_the_environment_once_per_id()
    {
        var id = NewId();
        var source = NewId();
        var twin = NewId();
        var other = NewId();
        var frozen = Frozen(("sig-a", "n_plus_one_sql", 1, 1));
        await SeedIncidentAsync(id, source, AtMs, frozen);
        // Two earlier incidents, one stored by two sources of the environment: counts once.
        var shared = NewId();
        await SeedIncidentAsync(shared, source, AtMs - DayMs, frozen);
        await SeedIncidentAsync(shared, twin, AtMs - DayMs, frozen);
        await SeedIncidentAsync(NewId(), source, AtMs - 2 * DayMs, Frozen(("sig-z", "slow_sql", 1, 1)));
        // Ignored: another environment, older than 30 days, later, another service or kind.
        await SeedIncidentAsync(NewId(), other, AtMs - DayMs, frozen);
        await SeedIncidentAsync(NewId(), source, AtMs - 31 * DayMs, frozen);
        await SeedIncidentAsync(NewId(), source, AtMs + DayMs, frozen);
        await SeedIncidentAsync(NewId(), source, AtMs - DayMs, frozen, service: "reports-svc");
        await SeedIncidentAsync(NewId(), source, AtMs - DayMs, frozen, kind: "restart");

        var context = await factory.Database.LoadLinkContextAsync(
            new IncidentKey(id, source), [source, twin], Token);

        Assert.NotNull(context);
        Assert.Equal(new Recurrence(2, 1), context.Recurrences["sig-a"]);
    }

    [Fact]
    public async Task Replace_is_idempotent_and_listing_returns_the_new_rows()
    {
        var key = new IncidentKey(NewId(), NewId());
        await SeedIncidentAsync(key.Id, key.SourceId, AtMs, Frozen(("sig-a", "slow_sql", 1, 1)));
        var first = new StoredLink("sig-a", LinkLevel.Strong,
            [new LinkReason("recurrence", K: 4, N: 5, UsualShare: 0.2), new LinkReason("mechanism_compatible")],
            IncidentLinkRules.RulesVersion);
        var second = new StoredLink("sig-b", LinkLevel.Weak, [], IncidentLinkRules.RulesVersion);

        await factory.Database.ReplaceLinksAsync(key, "production", [first, second], Token);
        await factory.Database.ReplaceLinksAsync(key, "production", [first], Token);
        await factory.Database.ReplaceLinksAsync(key, "production", [first], Token);

        var links = await factory.Database.ListLinksAsync(key, Token);
        var link = Assert.Single(links);
        Assert.Equal("sig-a", link.Signature);
        Assert.Equal(LinkLevel.Strong, link.Level);
        Assert.Equal(IncidentLinkRules.RulesVersion, link.RulesVersion);
        Assert.Equal(["recurrence", "mechanism_compatible"], link.Reasons.Select(r => r.Code));
        Assert.Equal(new LinkReason("recurrence", K: 4, N: 5, UsualShare: 0.2), link.Reasons[0]);
    }

    [Fact]
    public async Task Incidents_needing_links_lists_missing_and_outdated_rows()
    {
        var missing = new IncidentKey(NewId(), NewId());
        var outdated = new IncidentKey(NewId(), NewId());
        var current = new IncidentKey(NewId(), NewId());
        foreach (var key in new[] { missing, outdated, current })
            await SeedIncidentAsync(key.Id, key.SourceId, AtMs, Frozen(("sig-a", "slow_sql", 1, 1)));
        await factory.Database.ReplaceLinksAsync(outdated, "production",
            [new StoredLink("sig-a", LinkLevel.Weak, [], 1)], Token);
        await factory.Database.ReplaceLinksAsync(current, "production",
            [new StoredLink("sig-a", LinkLevel.Weak, [], 2)], Token);

        var needing = await factory.Database.IncidentsNeedingLinksAsync(2, Token);

        Assert.Contains(missing, needing);
        Assert.Contains(outdated, needing);
        Assert.DoesNotContain(current, needing);
    }

    [Fact]
    public async Task Purge_removes_links_with_their_incident()
    {
        var gone = new IncidentKey(NewId(), NewId());
        var kept = new IncidentKey(NewId(), NewId());
        await SeedIncidentAsync(gone.Id, gone.SourceId, 1_000, Frozen(("sig-a", "slow_sql", 1, 1)));
        await SeedIncidentAsync(kept.Id, kept.SourceId, long.MaxValue / 4, Frozen(("sig-a", "slow_sql", 1, 1)));
        StoredLink[] links = [new("sig-a", LinkLevel.Weak, [], 1)];
        await factory.Database.ReplaceLinksAsync(gone, "production", links, Token);
        await factory.Database.ReplaceLinksAsync(kept, "production", links, Token);

        await factory.Database.PurgeAsync(10_000, 0, 0, 0, Token);

        Assert.Equal(0, await CountLinksAsync(gone.Id));
        Assert.Equal(1, await CountLinksAsync(kept.Id));
    }

    [Fact]
    public async Task Migration_from_v23_creates_the_links_table()
    {
        var path = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-{Guid.NewGuid():N}.db");
        try
        {
            var options = Options.Create(new HubOptions { DatabasePath = path });
            await new HubDatabase(options, TimeProvider.System).InitializeAsync(Token);
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync(Token);
                await using var downgrade = connection.CreateCommand();
                downgrade.CommandText = LegacySchema.WithoutLinks;
                await downgrade.ExecuteNonQueryAsync(Token);
            }

            await new HubDatabase(options, TimeProvider.System).InitializeAsync(Token);

            await using var check = new SqliteConnection($"Data Source={path};Pooling=False");
            await check.OpenAsync(Token);
            await using var query = check.CreateCommand();
            query.CommandText = """
                                SELECT (SELECT count(*) FROM sqlite_master WHERE name = 'incident_finding_links'),
                                       (SELECT count(*) FROM schema_migrations WHERE version = 24);
                                """;
            await using var reader = await query.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt64(1));
        }
        finally
        {
            TestPool.ClearFor(path);
            File.Delete(path);
            File.Delete($"{path}-shm");
            File.Delete($"{path}-wal");
        }
    }
}
