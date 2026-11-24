using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Links;
using PerfSentinelHub.Storage;
using PerfSentinelHub.Tests.Support;

namespace PerfSentinelHub.Tests.Links;

public sealed class IncidentLinkWorkerTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private const string ApiKey = "0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential
    private const string Source = "test";
    private const long AtMs = 100 * 86_400_000L;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private IncidentLinkWorker NewWorker() => new(
        factory.Services.GetRequiredService<IncidentLinkQueue>(),
        factory.Database,
        factory.Services.GetRequiredService<IOptions<HubOptions>>(),
        TimeProvider.System,
        NullLogger<IncidentLinkWorker>.Instance);

    private static string Frozen(int count, long atMs) =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(index =>
            $$"""{"finding":{"type":"n_plus_one_sql","signature":"sig-{{index}}"},"stored_at_ms":1,"first_seen_ms":{{atMs + 5}},"seen_count":3}""")) + "]";

    private async Task SeedIncidentAsync(string id, int signatures, long atMs = AtMs, string? findings = null)
    {
        await using var connection = await factory.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO incidents(id, source_id, service, kind, at_ms, window_from_ms, window_to_ms,
                                finding_count, incident_json, first_seen_ms, last_seen_ms, findings_json)
                              VALUES ($id, $source, 'checkout-svc', 'oom_kill', $at, $from, $at, 1, '{}', $at, $at, $findings);
                              """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$source", Source);
        command.Parameters.AddWithValue("$at", atMs);
        command.Parameters.AddWithValue("$from", atMs - 300_000);
        command.Parameters.AddWithValue("$findings", findings ?? Frozen(signatures, atMs));
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (!await condition())
        {
            try
            {
                await Task.Delay(20, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }

    private static string Id(int index) => "c" + index.ToString("x31", CultureInfo.InvariantCulture);

    // A grown copy freezes one more finding, as the daemon's settle pass can.
    private async Task PushIncidentAsync(string id, bool grown = false)
    {
        var array = JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "daemon-incidents-0.20.0.json")))!.AsArray();
        var incident = array[0]!.AsObject();
        incident["id"] = id;
        if (grown)
            incident["findings"]!.AsArray().Add(incident["findings"]![0]!.DeepClone());
        var body = new JsonObject
        {
            ["producer_version"] = "0.29.0",
            ["incidents"] = new JsonArray(incident.DeepClone())
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/import/incidents?source_id=test")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-API-Key", ApiKey);
        using var response = await factory.CreateClient().SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Intensity_weighs_each_finding_against_its_usual_share_of_the_incident()
    {
        var id = NewId();
        // 40 days past the other tests' incidents: its ledger rows never reach their baselines, nor they its recurrence.
        const long at = AtMs + 40 * 86_400_000L;
        await SeedIncidentAsync(id, 3, at);
        await using (var connection = await factory.Database.OpenConnectionAsync(Token))
        {
            // Seven covered days before the incident: sig-0 is usually 5 % of these findings, sig-1 95 %, sig-2 new.
            for (var day = at / 86_400_000L - 7; day < at / 86_400_000L; day++)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                                      INSERT INTO detection_days(source_id, ledger_id, day, sealed, gap, overflow_detections,
                                        shed_traces, ingest_rejected, sampling_rate, last_read_ms)
                                      VALUES ($source, 'l1', $day, 1, 0, 0, 0, 0, 1.0, 0);
                                      INSERT INTO detection_counts(signature, source_id, ledger_id, day, detections)
                                      VALUES ('sig-0', $source, 'l1', $day, 5), ('sig-1', $source, 'l1', $day, 95);
                                      """;
                command.Parameters.AddWithValue("$source", Source);
                command.Parameters.AddWithValue("$day", day);
                await command.ExecuteNonQueryAsync(Token);
            }
        }

        using var worker = NewWorker();
        Assert.True(await worker.ProcessAsync(new IncidentKey(id, Source), Token));

        // Each was seen 3 times. The new sig-2 has no usual share, so it stays out of both totals: sig-0 and sig-1
        // weigh half each, sig-0 ten times its usual share, sig-1 about half.
        var links = (await factory.Database.ListLinksAsync(new IncidentKey(id, Source), Token))
            .ToDictionary(link => link.Signature);
        Assert.Equal(LinkLevel.Strong, links["sig-0"].Level);
        Assert.Equal(10.0, Assert.Single(links["sig-0"].Reasons, r => r.Code == "intensity").Ratio!.Value, 0.01);
        Assert.Equal(LinkLevel.Weak, links["sig-1"].Level);
        Assert.Contains(links["sig-2"].Reasons, r => r.Code == "intensity_new");
    }

    [Fact]
    public async Task Worker_shares_compare_the_same_grouping_population()
    {
        var id = NewId();
        // 80 days past the other tests' incidents, and 40 past the intensity test's.
        const long at = AtMs + 80 * 86_400_000L;
        // Frozen in ns-a at ns-a's usual shares: sig-a 1 in 10, sig-b 9 in 10.
        var findings = "[" + string.Join(",", new[] { ("sig-a", 1), ("sig-b", 9) }.Select(row =>
            $$"""{"finding":{"type":"n_plus_one_sql","signature":"{{row.Item1}}","grouping":[{"key":"k8s.namespace","value":"ns-a"}]},"first_seen_ms":1,"seen_count":{{row.Item2}}}""")) + "]";
        await SeedIncidentAsync(id, 0, at, findings);
        await using (var connection = await factory.Database.OpenConnectionAsync(Token))
        {
            // Source-wide sig-a is 90 % of these findings and sig-b 10 %, but in ns-a sig-a is only 10 %.
            for (var day = at / 86_400_000L - 7; day < at / 86_400_000L; day++)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                                      INSERT INTO detection_days(source_id, ledger_id, day, sealed, gap, overflow_detections,
                                        shed_traces, ingest_rejected, sampling_rate, last_read_ms, split)
                                      VALUES ($source, 'l1', $day, 1, 0, 0, 0, 0, 1.0, 0, 1);
                                      INSERT INTO detection_split_days(source_id, ledger_id, day) VALUES ($source, 'l1', $day);
                                      INSERT INTO detection_counts(signature, source_id, ledger_id, day, detections, grouping)
                                      VALUES ('sig-a', $source, 'l1', $day, 10, 'ns-a'), ('sig-b', $source, 'l1', $day, 90, 'ns-a'),
                                             ('sig-a', $source, 'l1', $day, 890, 'ns-b'), ('sig-b', $source, 'l1', $day, 10, 'ns-b');
                                      """;
                command.Parameters.AddWithValue("$source", Source);
                command.Parameters.AddWithValue("$day", day);
                await command.ExecuteNonQueryAsync(Token);
            }
        }

        using var worker = NewWorker();
        Assert.True(await worker.ProcessAsync(new IncidentKey(id, Source), Token));

        // Read source-wide, sig-b would be nine times its usual share; in its own grouping it is at its usual share.
        var links = (await factory.Database.ListLinksAsync(new IncidentKey(id, Source), Token))
            .ToDictionary(link => link.Signature);
        Assert.Equal(LinkLevel.Weak, links["sig-b"].Level);
        Assert.DoesNotContain(links["sig-b"].Reasons, r => r.Code.StartsWith("intensity", StringComparison.Ordinal));
        Assert.Equal(LinkLevel.Weak, links["sig-a"].Level);
    }

    // Frozen n_plus_one_sql rows, filed under a grouping unless it is empty.
    private static string FrozenIn(params (string Signature, string Grouping, long Seen)[] rows) =>
        "[" + string.Join(",", rows.Select(row =>
            $$"""{"finding":{"type":"n_plus_one_sql","signature":"{{row.Signature}}"{{(row.Grouping.Length > 0 ? $$""","grouping":[{"key":"k8s.namespace","value":"{{row.Grouping}}"}]""" : "")}}},"first_seen_ms":1,"seen_count":{{row.Seen}}}""")) + "]";

    // One clean ledger day of the test source, marked split when asked, with its count rows.
    private async Task SeedLedgerDayAsync(long day, bool split, params (string Signature, string Grouping, long Detections)[] counts)
    {
        await using var connection = await factory.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO detection_days(source_id, ledger_id, day, sealed, gap, overflow_detections,
                                shed_traces, ingest_rejected, sampling_rate, last_read_ms, split)
                              VALUES ($source, 'l1', $day, 1, 0, 0, 0, 0, 1.0, 0, $split);
                              """ + (split
                                  ? "INSERT INTO detection_split_days(source_id, ledger_id, day) VALUES ($source, 'l1', $day);"
                                  : "")
                              + string.Concat(counts.Select((_, i) => $"""
                                  INSERT INTO detection_counts(signature, source_id, ledger_id, day, detections, grouping)
                                  VALUES ($s{i}, $source, 'l1', $day, $d{i}, $g{i});
                                  """));
        command.Parameters.AddWithValue("$source", Source);
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$split", split ? 1 : 0);
        for (var i = 0; i < counts.Length; i++)
        {
            command.Parameters.AddWithValue($"$s{i}", counts[i].Signature);
            command.Parameters.AddWithValue($"$g{i}", counts[i].Grouping);
            command.Parameters.AddWithValue($"$d{i}", counts[i].Detections);
        }

        await command.ExecuteNonQueryAsync(Token);
    }

    [Fact]
    public async Task Mixed_signatures_read_one_day_set_during_the_transition()
    {
        var id = NewId();
        // 120 days past the other tests' incidents, 40 past the grouping population test's.
        const long at = AtMs + 120 * 86_400_000L;
        await SeedIncidentAsync(id, 0, at, FrozenIn(("sig-x", "", 10), ("sig-y", "ns-a", 10)));
        // Seven covered days, the last three split: both run steadily at 100 a day.
        for (var day = at / 86_400_000L - 7; day < at / 86_400_000L; day++)
        {
            var split = day >= at / 86_400_000L - 3;
            await SeedLedgerDayAsync(day, split, ("sig-x", "", 100), ("sig-y", split ? "ns-a" : "", 100));
        }

        using var worker = NewWorker();
        Assert.True(await worker.ProcessAsync(new IncidentKey(id, Source), Token));

        // Over the same three split days both are half of the incident, as usual; seven days against three
        // would read sig-y at 1.67 times its usual share.
        var links = (await factory.Database.ListLinksAsync(new IncidentKey(id, Source), Token))
            .ToDictionary(link => link.Signature);
        Assert.Equal(LinkLevel.Weak, links["sig-y"].Level);
        Assert.DoesNotContain(links["sig-y"].Reasons, r => r.Code == "intensity");
        Assert.Equal(LinkLevel.Weak, links["sig-x"].Level);
    }

    [Fact]
    public async Task A_grouped_row_unseen_in_its_grouping_but_seen_in_another_is_unknown_not_new()
    {
        var id = NewId();
        // 160 days past the other tests' incidents, 40 past the transition test's.
        const long at = AtMs + 160 * 86_400_000L;
        await SeedIncidentAsync(id, 0, at, FrozenIn(("sig-a", "ns-a", 5), ("sig-b", "ns-a", 5)));
        await SeedLedgerDayAsync(at / 86_400_000L - 1, true, ("sig-a", "ns-b", 40), ("sig-b", "ns-a", 60));

        using var worker = NewWorker();
        Assert.True(await worker.ProcessAsync(new IncidentKey(id, Source), Token));

        var links = (await factory.Database.ListLinksAsync(new IncidentKey(id, Source), Token))
            .ToDictionary(link => link.Signature);
        Assert.Contains(links["sig-a"].Reasons, r => r.Code == "intensity_unknown");
        Assert.DoesNotContain(links["sig-a"].Reasons, r => r.Code == "intensity_new");
    }

    [Fact]
    public async Task Imported_incident_gets_links_once_the_worker_runs()
    {
        var id = Id(1);
        var key = new IncidentKey(id, Source);
        using var worker = NewWorker();
        await worker.StartAsync(Token);
        try
        {
            await PushIncidentAsync(id);

            Assert.True(await WaitAsync(async () => (await factory.Database.ListLinksAsync(key, Token)).Count > 0));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Merge_requeues_and_recomputes_the_incident()
    {
        var id = Id(2);
        var key = new IncidentKey(id, Source);
        using var worker = NewWorker();
        await worker.StartAsync(Token);
        try
        {
            await PushIncidentAsync(id);
            Assert.True(await WaitAsync(async () => (await factory.Database.ListLinksAsync(key, Token)).Count > 0));
            await factory.Database.ReplaceLinksAsync(key, [], Token);
            Assert.Empty(await factory.Database.ListLinksAsync(key, Token));

            await PushIncidentAsync(id, grown: true);

            Assert.True(await WaitAsync(async () => (await factory.Database.ListLinksAsync(key, Token)).Count > 0));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_push_queues_only_new_or_grown_incidents()
    {
        var id = Id(3);
        var key = new IncidentKey(id, Source);
        var queue = factory.Services.GetRequiredService<IncidentLinkQueue>();
        await DrainAsync(queue);

        await PushIncidentAsync(id);
        Assert.Contains(key, await DrainAsync(queue));

        await PushIncidentAsync(id);
        Assert.DoesNotContain(key, await DrainAsync(queue));

        await PushIncidentAsync(id, grown: true);
        Assert.Contains(key, await DrainAsync(queue));
    }

    // What the queue holds now: an import queues its keys before it answers.
    private static async Task<List<IncidentKey>> DrainAsync(IncidentLinkQueue queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var keys = new List<IncidentKey>();
        try
        {
            await foreach (var key in queue.ReadAllAsync(timeout.Token))
                keys.Add(key);
        }
        catch (OperationCanceledException)
        {
        }

        return keys;
    }

    [Fact]
    public async Task A_gone_incident_is_skipped_without_error()
    {
        using var worker = NewWorker();

        Assert.False(await worker.ProcessAsync(new IncidentKey(NewId(), Source), Token));
        Assert.False(await worker.ProcessAsync(new IncidentKey(NewId(), "no-such-source"), Token));
    }

    [Fact]
    public async Task Startup_queues_incidents_without_links_or_on_an_older_rules_version()
    {
        var fresh = NewId();
        var stale = NewId();
        await SeedIncidentAsync(fresh, 2);
        await SeedIncidentAsync(stale, 2);
        var staleKey = new IncidentKey(stale, Source);
        await factory.Database.ReplaceLinksAsync(staleKey, [new StoredLink("sig-0", LinkLevel.Weak, [], IncidentLinkRules.RulesVersion - 1)], Token);
        using var worker = NewWorker();
        await worker.StartAsync(Token);
        try
        {
            Assert.True(await WaitAsync(async () =>
                (await factory.Database.ListLinksAsync(new IncidentKey(fresh, Source), Token)).Count == 2
                && (await factory.Database.ListLinksAsync(staleKey, Token)).Count == 2));
            Assert.All(await factory.Database.ListLinksAsync(staleKey, Token),
                link => Assert.Equal(IncidentLinkRules.RulesVersion, link.RulesVersion));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_failed_catch_up_still_drains_the_queue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-links-{NewId()}.db");
        var options = factory.Services.GetRequiredService<IOptions<HubOptions>>();
        // Never initialized: the catch-up query finds no table and throws.
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = path, Sources = options.Value.Sources }), TimeProvider.System);
        var queue = new IncidentLinkQueue();
        queue.Enqueue(new IncidentKey("drained", Source));
        var logger = new ListLogger<IncidentLinkWorker>();
        using var worker = new IncidentLinkWorker(queue, database, options, TimeProvider.System, logger);
        await worker.StartAsync(Token);
        try
        {
            Assert.True(await WaitAsync(() => Task.FromResult(
                logger.Messages.Any(m => m.Contains("catch-up", StringComparison.Ordinal))
                && logger.Messages.Any(m => m.Contains("drained", StringComparison.Ordinal)))));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            TestPool.ClearFor(path);
            File.Delete(path);
            File.Delete($"{path}-shm");
            File.Delete($"{path}-wal");
        }
    }

    [Fact]
    public async Task A_findings_push_succeeds_while_the_worker_assesses_a_thousand_signatures()
    {
        var id = NewId();
        await SeedIncidentAsync(id, 1000);
        var key = new IncidentKey(id, Source);
        using var worker = NewWorker();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/import/findings?source_id=test")
        {
            Content = new StringContent(
                $$"""{"producer_version":"0.11.2","findings":{{FixtureFindings()}}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-API-Key", ApiKey);

        // The assessment loops on a thread of its own for the whole push, so the two overlap for
        // certain. The counters prove a pass was running at the send or began during the request.
        using var stop = new CancellationTokenSource();
        var begun = 0;
        var ended = 0;
        var started = new TaskCompletionSource();
        var stress = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                Interlocked.Increment(ref begun);
                await worker.ProcessAsync(key, Token);
                Interlocked.Increment(ref ended);
                started.TrySetResult();
            }
        }, Token);
        await Task.WhenAny(started.Task, stress).WaitAsync(TimeSpan.FromSeconds(20), Token);
        if (stress.IsFaulted)
            await stress;

        var endedBefore = Volatile.Read(ref ended);
        var begunBefore = Volatile.Read(ref begun);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var response = await factory.CreateClient().SendAsync(request, Token);
        clock.Stop();
        var begunAfter = Volatile.Read(ref begun);
        await stop.CancelAsync();
        await stress;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(begunAfter > begunBefore || begunBefore > endedBefore,
            "no assessment ran while the push was in flight");
        // The write gate is waited on for 5 s before a push answers 503; far below that is no wait.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"push took {clock.Elapsed}");
        Assert.Equal(1000, (await factory.Database.ListLinksAsync(key, Token)).Count);
    }

    [Fact]
    public async Task A_failing_incident_is_logged_and_the_next_key_still_gets_links()
    {
        var bad = NewId();
        var good = NewId();
        await SeedIncidentAsync(bad, 1);
        await SeedIncidentAsync(good, 2);
        var queue = new IncidentLinkQueue();
        queue.Enqueue(new IncidentKey(bad, Source));
        queue.Enqueue(new IncidentKey(good, Source));
        var logger = new ListLogger<IncidentLinkWorker>();
        using var worker = new IncidentLinkWorker(queue, factory.Database,
            new FailsOnce(factory.Services.GetRequiredService<IOptions<HubOptions>>().Value), TimeProvider.System, logger);
        await worker.StartAsync(Token);
        try
        {
            Assert.True(await WaitAsync(async () =>
                (await factory.Database.ListLinksAsync(new IncidentKey(good, Source), Token)).Count == 2));
            Assert.Contains(logger.Messages, m => m.Contains(bad, StringComparison.Ordinal));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_catch_up_runs_again_every_hour_to_retry_a_failed_assessment()
    {
        var sentinel = NewId();
        var late = NewId();
        await SeedIncidentAsync(sentinel, 1);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var worker = new IncidentLinkWorker(new IncidentLinkQueue(), factory.Database,
            factory.Services.GetRequiredService<IOptions<HubOptions>>(), clock,
            NullLogger<IncidentLinkWorker>.Instance);
        await worker.StartAsync(Token);
        try
        {
            // Once the startup pass has done the sentinel, an incident seeded now is only found by a later pass.
            Assert.True(await WaitAsync(async () =>
                (await factory.Database.ListLinksAsync(new IncidentKey(sentinel, Source), Token)).Count == 1));
            await SeedIncidentAsync(late, 1);
            var lateKey = new IncidentKey(late, Source);

            Assert.True(await WaitAsync(async () =>
            {
                clock.Advance(IncidentLinkWorker.CatchUpInterval);
                return (await factory.Database.ListLinksAsync(lateKey, Token)).Count == 1;
            }));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // The first read of the options throws: the first incident assessed fails, the next one is fine.
    // The first read is the start-up catch-up's, the second the first queued key's.
    private sealed class FailsOnce(HubOptions inner) : IOptions<HubOptions>
    {
        private int _reads;

        public HubOptions Value => Interlocked.Increment(ref _reads) == 2
            ? throw new InvalidOperationException("simulated failure")
            : inner;
    }

    private static string FixtureFindings() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "daemon-findings-0.11.2.json"));
}
