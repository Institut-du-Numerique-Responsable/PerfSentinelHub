using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Collection;
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
        string service = "checkout-svc", string kind = "oom_kill", string incidentJson = "{}", long findingCount = 1) =>
        ExecuteAsync("""
                     INSERT INTO incidents(id, source_id, service, kind, at_ms, window_from_ms, window_to_ms,
                       finding_count, incident_json, first_seen_ms, last_seen_ms, findings_json)
                     VALUES ($id, $source, $service, $kind, $at, $from, $to, $count, $json, $at, $at, $findings);
                     """,
            ("$id", id), ("$source", source), ("$service", service), ("$kind", kind), ("$at", atMs),
            ("$from", atMs - 300_000), ("$to", atMs), ("$json", incidentJson), ("$findings", findings),
            ("$count", findingCount));

    private Task SeedDayAsync(string source, long day, string ledger = "l1", int gap = 0, long overflow = 0) =>
        ExecuteAsync("""
                     INSERT INTO detection_days(source_id, ledger_id, day, sealed, gap, overflow_detections,
                       shed_traces, ingest_rejected, sampling_rate, last_read_ms)
                     VALUES ($source, $ledger, $day, 1, $gap, $overflow, 0, 0, 1.0, 0);
                     """,
            ("$source", source), ("$ledger", ledger), ("$day", day), ("$gap", gap), ("$overflow", overflow));

    private Task SeedCountAsync(string source, string signature, long day, long detections, string ledger = "l1",
        string grouping = "") =>
        ExecuteAsync("""
                     INSERT INTO detection_counts(signature, source_id, ledger_id, day, detections, grouping)
                     VALUES ($signature, $source, $ledger, $day, $detections, $grouping);
                     """,
            ("$signature", signature), ("$source", source), ("$ledger", ledger), ("$day", day),
            ("$detections", detections), ("$grouping", grouping));

    private Task SeedSplitAsync(string source, long day, string ledger = "l1") =>
        ExecuteAsync("INSERT INTO detection_split_days(source_id, ledger_id, day) VALUES ($source, $ledger, $day);",
            ("$source", source), ("$ledger", ledger), ("$day", day));

    // Frozen rows filed under a grouping, as the daemon writes them; an empty grouping writes none.
    private static string FrozenIn(params (string Signature, string Grouping, long Seen)[] rows) =>
        "[" + string.Join(",", rows.Select(row =>
            $$"""{"finding":{"type":"n_plus_one_sql","signature":"{{row.Signature}}"{{(row.Grouping.Length > 0 ? $$""","grouping":[{"key":"k8s.namespace","value":"{{row.Grouping}}"}]""" : "")}}},"first_seen_ms":1,"seen_count":{{row.Seen}}}""")) + "]";

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
        Assert.Equal(new FrozenSignature("sig-a", "n_plus_one_sql", 7, 700, 0),
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

    // The push is a trust boundary: a value outside [0, 1] reads as no saturation, so it never lifts a level.
    [Theory]
    [InlineData("98")]
    [InlineData("-0.1")]
    public async Task Saturation_outside_zero_to_one_reads_as_absent(string value)
    {
        var id = NewId();
        var source = NewId();
        await SeedIncidentAsync(id, source, AtMs, Frozen(("sig-a", "slow_sql", 1, 1)),
            incidentJson: $$"""{"saturation":{{value}}}""");

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
        Assert.Equal(["sig-a", "sig-b"], context.SeenInBaselineDays.Order());
    }

    [Fact]
    public async Task Baseline_ignores_rows_below_the_detection_threshold()
    {
        var id = NewId();
        var source = NewId();
        await SeedIncidentAsync(id, source, AtMs, Frozen(("sig-a", "n_plus_one_sql", 1, AtMs)));
        await SeedDayAsync(source, Day - 1);
        // A pass below the threshold stores a zero row: the signature was not detected that day.
        await SeedCountAsync(source, "sig-a", Day - 1, 0);

        var context = await factory.Database.LoadLinkContextAsync(new IncidentKey(id, source), [source], Token);

        Assert.NotNull(context);
        Assert.Equal(new Baseline(1, 0, 0), context.Baselines["sig-a"]);
        Assert.Empty(context.SeenInBaselineDays);
    }

    [Fact]
    public async Task Baseline_spans_the_seven_days_before_the_incident_day()
    {
        var id = NewId();
        var source = NewId();
        await SeedIncidentAsync(id, source, AtMs, Frozen(("sig-a", "n_plus_one_sql", 1, AtMs)));
        await SeedDayAsync(source, Day - 7);
        await SeedDayAsync(source, Day - 8);
        await SeedCountAsync(source, "sig-a", Day - 7, 3);
        await SeedCountAsync(source, "sig-a", Day - 8, 50);

        var context = await factory.Database.LoadLinkContextAsync(new IncidentKey(id, source), [source], Token);

        Assert.NotNull(context);
        Assert.Equal(new Baseline(1, 1, 3), context.Baselines["sig-a"]);
    }

    [Fact]
    public async Task A_narrowed_row_reads_its_own_grouping_history()
    {
        var id = NewId();
        var source = NewId();
        await SeedIncidentAsync(id, source, AtMs, FrozenIn(("sig-a", "ns-a", 4), ("sig-b", "ns-a", 2), ("sig-b", "ns-c", 3)));
        // Days -1..-3 split: sig-a 5 a day in ns-a, 95 in ns-b; sig-b in ns-a on days -1 and -2, in ns-b on day -3.
        for (var day = Day - 3; day < Day; day++)
        {
            await SeedDayAsync(source, day);
            await SeedSplitAsync(source, day);
            await SeedCountAsync(source, "sig-a", day, 5, grouping: "ns-a");
            await SeedCountAsync(source, "sig-a", day, 95, grouping: "ns-b");
        }

        await SeedCountAsync(source, "sig-b", Day - 1, 7, grouping: "ns-a");
        await SeedCountAsync(source, "sig-b", Day - 2, 7, grouping: "ns-a");
        await SeedCountAsync(source, "sig-b", Day - 3, 50, grouping: "ns-b");
        // Day -4 came unsplit: its one row holds every grouping, so a narrowed row cannot read it.
        await SeedDayAsync(source, Day - 4);
        await SeedCountAsync(source, "sig-a", Day - 4, 1000);
        // Day -5 has a second ledger that sent it unsplit: not a split day for the source either.
        await SeedDayAsync(source, Day - 5);
        await SeedDayAsync(source, Day - 5, ledger: "l2");
        await SeedSplitAsync(source, Day - 5);
        await SeedCountAsync(source, "sig-a", Day - 5, 5, grouping: "ns-a");
        await SeedCountAsync(source, "sig-a", Day - 5, 50, ledger: "l2");

        var context = await factory.Database.LoadLinkContextAsync(new IncidentKey(id, source), [source], Token);

        Assert.NotNull(context);
        Assert.Equal(new Baseline(3, 3, 15), context.Baselines["sig-a"]);
        // Two groupings sum their histories over the split days; ns-c was never seen, so its 3 stay unknown.
        Assert.Equal(new Baseline(3, 2, 14), context.Baselines["sig-b"]);
        Assert.Equal(new FrozenSignature("sig-b", "n_plus_one_sql", 5, 1, 2), context.Signatures.Single(s => s.Signature == "sig-b"));
        Assert.Equal(new FrozenSignature("sig-a", "n_plus_one_sql", 4, 1, 4), context.Signatures.Single(s => s.Signature == "sig-a"));
    }

    [Fact]
    public async Task A_grouped_row_without_split_days_is_unknown_not_the_whole_source()
    {
        var id = NewId();
        var source = NewId();
        await SeedIncidentAsync(id, source, AtMs, FrozenIn(("sig-a", "ns-a", 4)));
        for (var day = Day - 7; day < Day; day++)
        {
            await SeedDayAsync(source, day);
            await SeedCountAsync(source, "sig-a", day, 100);
        }

        var context = await factory.Database.LoadLinkContextAsync(new IncidentKey(id, source), [source], Token);

        Assert.NotNull(context);
        Assert.Equal(new Baseline(0, 0, 0), context.Baselines["sig-a"]);
        Assert.Equal(["sig-a"], context.SeenInBaselineDays);
        Assert.Equal(0, context.Signatures.Single().KnownSeenCount);
    }

    [Fact]
    public async Task An_ungrouped_row_reads_the_signature_sum_as_before()
    {
        var id = NewId();
        var source = NewId();
        await SeedIncidentAsync(id, source, AtMs, FrozenIn(("sig-a", "", 4), ("sig-c", "", 1), ("sig-c", "ns-a", 2)));
        // Day -1 split: its groupings and its row without one all count; day -2 unsplit.
        await SeedDayAsync(source, Day - 1);
        await SeedSplitAsync(source, Day - 1);
        await SeedDayAsync(source, Day - 2);
        foreach (var signature in new[] { "sig-a", "sig-c" })
        {
            await SeedCountAsync(source, signature, Day - 1, 5, grouping: "ns-a");
            await SeedCountAsync(source, signature, Day - 1, 95, grouping: "ns-b");
            await SeedCountAsync(source, signature, Day - 1, 2);
            await SeedCountAsync(source, signature, Day - 2, 40);
        }

        var context = await factory.Database.LoadLinkContextAsync(new IncidentKey(id, source), [source], Token);

        Assert.NotNull(context);
        Assert.Equal(new Baseline(2, 2, 142), context.Baselines["sig-a"]);
        Assert.Equal(4, context.Signatures.Single(s => s.Signature == "sig-a").KnownSeenCount);
        // A signature also frozen without a grouping reads as a whole: its ns-a history is already in the sum.
        Assert.Equal(new Baseline(2, 2, 142), context.Baselines["sig-c"]);
        Assert.Equal(3, context.Signatures.Single(s => s.Signature == "sig-c").KnownSeenCount);
    }

    [Fact]
    public async Task Frozen_row_without_seen_count_counts_once()
    {
        var id = NewId();
        var source = NewId();
        await SeedIncidentAsync(id, source, AtMs,
            """[{"finding":{"type":"slow_sql","signature":"sig-a"},"first_seen_ms":5}]""");

        var context = await factory.Database.LoadLinkContextAsync(new IncidentKey(id, source), [source], Token);

        Assert.NotNull(context);
        Assert.Equal(new FrozenSignature("sig-a", "slow_sql", 1, 5, 0), Assert.Single(context.Signatures));
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
        // Malformed findings count as an earlier incident without the signature, and never fail the read.
        await SeedIncidentAsync(NewId(), source, AtMs - 3 * DayMs, "not json");
        await SeedIncidentAsync(NewId(), source, AtMs - 3 * DayMs, """["row",{"finding":{"signature":7}},{"finding":"sig-a"}]""");
        // Exactly 30 days earlier is still inside the lookback.
        await SeedIncidentAsync(NewId(), source, AtMs - 30 * DayMs, frozen);
        // Ignored: another environment, older than 30 days, later, another service or kind.
        await SeedIncidentAsync(NewId(), other, AtMs - DayMs, frozen);
        await SeedIncidentAsync(NewId(), source, AtMs - 31 * DayMs, frozen);
        await SeedIncidentAsync(NewId(), source, AtMs + DayMs, frozen);
        await SeedIncidentAsync(NewId(), source, AtMs - DayMs, frozen, service: "reports-svc");
        await SeedIncidentAsync(NewId(), source, AtMs - DayMs, frozen, kind: "restart");

        var context = await factory.Database.LoadLinkContextAsync(
            new IncidentKey(id, source), [source, twin], Token);

        Assert.NotNull(context);
        Assert.Equal(new Recurrence(5, 2), context.Recurrences["sig-a"]);
    }

    [Fact]
    public async Task Upsert_that_replaces_the_findings_drops_the_links_and_one_that_keeps_them_does_not()
    {
        var key = new IncidentKey(NewId(), NewId());
        var poor = new ParsedIncident(key.Id, "checkout-svc", "oom_kill", AtMs, null, AtMs - 300_000, AtMs, null, 1,
            "{}", Frozen(("sig-a", "slow_sql", 1, 1)));
        var rich = poor with { FindingCount = 2, FindingsJson = Frozen(("sig-a", "slow_sql", 1, 1), ("sig-b", "slow_sql", 1, 1)) };
        var link = new StoredLink("sig-a", LinkLevel.Weak, [], IncidentLinkRules.RulesVersion);

        Assert.Equal([key.Id], await factory.Database.UpsertIncidentsAsync(key.SourceId, [poor], AtMs, Token));
        await factory.Database.ReplaceLinksAsync(key, [link], Token);
        // The same findings again, then a poorer re-capture: the copy keeps its findings and its links.
        Assert.Empty(await factory.Database.UpsertIncidentsAsync(key.SourceId, [poor], AtMs + 1, Token));
        Assert.Equal(1, await CountLinksAsync(key.Id));

        Assert.Equal([key.Id], await factory.Database.UpsertIncidentsAsync(key.SourceId, [rich], AtMs + 2, Token));
        Assert.Equal(0, await CountLinksAsync(key.Id));
        Assert.Contains(key, await factory.Database.IncidentsNeedingLinksAsync(IncidentLinkRules.RulesVersion, [key.SourceId], Token));
    }

    // The worker reads outside the write gate: links read from findings an upsert replaced meanwhile must
    // not land, or the catch-up would count the copy as assessed once the re-queued key is lost.
    [Fact]
    public async Task Replace_writes_nothing_when_the_findings_changed_since_the_context_was_read()
    {
        var key = new IncidentKey(NewId(), NewId());
        var poor = new ParsedIncident(key.Id, "checkout-svc", "oom_kill", AtMs, null, AtMs - 300_000, AtMs, null, 1,
            "{}", Frozen(("sig-a", "slow_sql", 1, 1)));
        var rich = poor with { FindingCount = 2, FindingsJson = Frozen(("sig-a", "slow_sql", 1, 1), ("sig-b", "slow_sql", 1, 1)) };
        var link = new StoredLink("sig-a", LinkLevel.Weak, [], IncidentLinkRules.RulesVersion);
        await factory.Database.UpsertIncidentsAsync(key.SourceId, [poor], AtMs, Token);
        var context = await factory.Database.LoadLinkContextAsync(key, [key.SourceId], Token);
        Assert.NotNull(context);

        await factory.Database.UpsertIncidentsAsync(key.SourceId, [rich], AtMs + 1, Token);
        await factory.Database.ReplaceLinksAsync(key, [link], context.FindingsJson, Token);

        Assert.Equal(0, await CountLinksAsync(key.Id));
        Assert.Contains(key, await factory.Database.IncidentsNeedingLinksAsync(IncidentLinkRules.RulesVersion, [key.SourceId], Token));

        var fresh = await factory.Database.LoadLinkContextAsync(key, [key.SourceId], Token);
        await factory.Database.ReplaceLinksAsync(key, [link], fresh!.FindingsJson, Token);
        Assert.Equal(1, await CountLinksAsync(key.Id));
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

        await factory.Database.ReplaceLinksAsync(key, [first, second], Token);
        await factory.Database.ReplaceLinksAsync(key, [first], Token);
        await factory.Database.ReplaceLinksAsync(key, [first], Token);

        var links = await factory.Database.ListLinksAsync(key, Token);
        var link = Assert.Single(links);
        Assert.Equal("sig-a", link.Signature);
        Assert.Equal(LinkLevel.Strong, link.Level);
        Assert.Equal(IncidentLinkRules.RulesVersion, link.RulesVersion);
        Assert.Equal(["recurrence", "mechanism_compatible"], link.Reasons.Select(r => r.Code));
        Assert.Equal(new LinkReason("recurrence", K: 4, N: 5, UsualShare: 0.2), link.Reasons[0]);
    }

    [Fact]
    public async Task A_v4_incident_is_requeued_only_when_its_baseline_window_holds_a_split_day()
    {
        var before = new IncidentKey(NewId(), NewId());
        var within = new IncidentKey(NewId(), NewId());
        foreach (var key in new[] { before, within })
        {
            await SeedIncidentAsync(key.Id, key.SourceId, AtMs, FrozenIn(("sig-a", "ns-a", 3)));
            await factory.Database.ReplaceLinksAsync(key, [new StoredLink("sig-a", LinkLevel.Weak, [], 4)], Token);
        }

        // Split days on either side of the seven covered days keep the v4 links.
        await SeedSplitAsync(before.SourceId, Day - 8);
        await SeedSplitAsync(before.SourceId, Day);
        await SeedSplitAsync(within.SourceId, Day - 1);

        var needing = await factory.Database.IncidentsNeedingLinksAsync(IncidentLinkRules.RulesVersion,
            [before.SourceId, within.SourceId], Token);

        Assert.DoesNotContain(before, needing);
        Assert.Contains(within, needing);
    }

    [Fact]
    public async Task Incidents_needing_links_lists_missing_and_outdated_rows()
    {
        var missing = new IncidentKey(NewId(), NewId());
        var outdated = new IncidentKey(NewId(), NewId());
        var current = new IncidentKey(NewId(), NewId());
        var empty = new IncidentKey(NewId(), NewId());
        var unsigned = new IncidentKey(NewId(), NewId());
        var unconfigured = new IncidentKey(NewId(), NewId());
        foreach (var key in new[] { missing, outdated, current, unconfigured })
            await SeedIncidentAsync(key.Id, key.SourceId, AtMs, Frozen(("sig-a", "slow_sql", 1, 1)));
        await SeedIncidentAsync(empty.Id, empty.SourceId, AtMs, "[]", findingCount: 0);
        await SeedIncidentAsync(unsigned.Id, unsigned.SourceId, AtMs,
            """[{"finding":{"type":"slow_sql"}},{"finding":{"type":"slow_sql","signature":""}},"row"]""");
        await factory.Database.ReplaceLinksAsync(outdated, [new StoredLink("sig-a", LinkLevel.Weak, [], 1)], Token);
        await factory.Database.ReplaceLinksAsync(current, [new StoredLink("sig-a", LinkLevel.Weak, [], 2)], Token);

        var needing = await factory.Database.IncidentsNeedingLinksAsync(2,
            [missing.SourceId, outdated.SourceId, current.SourceId, empty.SourceId, unsigned.SourceId], Token);

        Assert.Contains(missing, needing);
        Assert.Contains(outdated, needing);
        Assert.DoesNotContain(current, needing);
        // No frozen finding, so no link is ever written: queueing it would repeat at every start.
        Assert.DoesNotContain(empty, needing);
        // No row has a signature, so no link is ever written either; a dropped source is never assessed.
        Assert.DoesNotContain(unsigned, needing);
        Assert.DoesNotContain(unconfigured, needing);
    }

    [Fact]
    public async Task Signatures_follow_the_incident_findings()
    {
        var id = NewId();
        var source = NewId();
        await SeedIncidentAsync(id, source, AtMs,
            """[{"finding":{"type":"slow_sql","signature":"sig-a"}},{"finding":{"type":"slow_sql","signature":"sig-a"}},""" +
            """{"finding":{"signature":""}},{"finding":{"signature":7}},"row",{"finding":{"type":"slow_sql","signature":"sig-b"}}]""");
        Assert.Equal(["sig-a", "sig-b"], await SignaturesAsync(id, source));

        await ExecuteAsync("UPDATE incidents SET findings_json = $f WHERE id = $id AND source_id = $source",
            ("$f", Frozen(("sig-c", "slow_sql", 1, 1))), ("$id", id), ("$source", source));
        Assert.Equal(["sig-c"], await SignaturesAsync(id, source));

        await ExecuteAsync("UPDATE incidents SET findings_json = 'not json' WHERE id = $id AND source_id = $source",
            ("$id", id), ("$source", source));
        Assert.Empty(await SignaturesAsync(id, source));

        await ExecuteAsync("UPDATE incidents SET findings_json = $f WHERE id = $id AND source_id = $source",
            ("$f", Frozen(("sig-d", "slow_sql", 1, 1))), ("$id", id), ("$source", source));
        await ExecuteAsync("DELETE FROM incidents WHERE id = $id AND source_id = $source", ("$id", id), ("$source", source));
        Assert.Empty(await SignaturesAsync(id, source));
    }

    private async Task<List<string>> SignaturesAsync(string id, string source)
    {
        await using var connection = await factory.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT signature FROM incident_signatures
                              WHERE incident_id = $id AND source_id = $source ORDER BY signature;
                              """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$source", source);
        var signatures = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
            signatures.Add(reader.GetString(0));
        return signatures;
    }

    [Fact]
    public async Task Purge_removes_links_with_their_incident()
    {
        var gone = new IncidentKey(NewId(), NewId());
        var kept = new IncidentKey(NewId(), NewId());
        await SeedIncidentAsync(gone.Id, gone.SourceId, 1_000, Frozen(("sig-a", "slow_sql", 1, 1)));
        await SeedIncidentAsync(kept.Id, kept.SourceId, long.MaxValue / 4, Frozen(("sig-a", "slow_sql", 1, 1)));
        StoredLink[] links = [new("sig-a", LinkLevel.Weak, [], 1)];
        await factory.Database.ReplaceLinksAsync(gone, links, Token);
        await factory.Database.ReplaceLinksAsync(kept, links, Token);

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
                // An incident a version 23 build stored: the migration derives its signatures.
                await using var seed = connection.CreateCommand();
                seed.CommandText = $"""
                                    INSERT INTO incidents(id, source_id, service, kind, at_ms, window_from_ms, window_to_ms,
                                      finding_count, incident_json, first_seen_ms, last_seen_ms, findings_json)
                                    VALUES ('old', 'src', 'svc', 'oom_kill', 1, 0, 1, 2, '{"{}"}', 1, 1,
                                            '{Frozen(("sig-a", "slow_sql", 1, 1), ("sig-b", "slow_sql", 1, 1))}');
                                    """;
                await seed.ExecuteNonQueryAsync(Token);
            }

            await new HubDatabase(options, TimeProvider.System).InitializeAsync(Token);

            await using var check = new SqliteConnection($"Data Source={path};Pooling=False");
            await check.OpenAsync(Token);
            await using var query = check.CreateCommand();
            query.CommandText = """
                                SELECT (SELECT count(*) FROM sqlite_master WHERE name = 'incident_finding_links'),
                                       (SELECT count(*) FROM schema_migrations WHERE version = 24),
                                       (SELECT count(*) FROM incident_signatures WHERE incident_id = 'old');
                                """;
            await using var reader = await query.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt64(1));
            Assert.Equal(2, reader.GetInt64(2));
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
