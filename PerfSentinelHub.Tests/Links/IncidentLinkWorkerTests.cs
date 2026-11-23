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

    private static string Frozen(int count) =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(index =>
            $$"""{"finding":{"type":"n_plus_one_sql","signature":"sig-{{index}}"},"stored_at_ms":1,"first_seen_ms":{{AtMs + 5}},"seen_count":3}""")) + "]";

    private async Task SeedIncidentAsync(string id, int signatures)
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
        command.Parameters.AddWithValue("$at", AtMs);
        command.Parameters.AddWithValue("$from", AtMs - 300_000);
        command.Parameters.AddWithValue("$findings", Frozen(signatures));
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
