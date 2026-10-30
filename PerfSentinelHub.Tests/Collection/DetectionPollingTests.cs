using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Collection;

public sealed class DetectionPollingTests : IDisposable
{
    // Today on the poller's clock, so every day below sits inside the parser's window.
    private const int Today = 20_400;

    private const string OtherLedgerId = "00000000-0000-4000-8000-000000000002";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-detection-polls-{Guid.NewGuid():N}.db");

    private readonly ConcurrentQueue<string> _requests = new();

    public void Dispose()
    {
        TestPool.ClearFor(_databasePath);
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(
        """{"ledger_id":"not-a-guid","started_ms":1,"retained_days":31,"oldest_day":20398,"current_day":20400,"sealed_through":20399}""")]
    [InlineData(
        """{"ledger_id":"00000000-0000-4000-8000-000000000001","started_ms":"1","oldest_day":20398,"current_day":20400}""")]
    [InlineData(
        """{"ledger_id":"00000000-0000-4000-8000-000000000001","started_ms":1,"oldest_day":20398,"current_day":20400,"sealed_through":"x"}""")]
    [InlineData("[]")]
    public async Task A_daemon_without_a_ledger_is_never_asked(string? ledger)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var status = ledger is null
            ? new JsonObject()
            : new JsonObject { ["detection_ledger"] = JsonNode.Parse(ledger) };
        await using var daemon = await DaemonAsync(status, _ => Task.CompletedTask, cancellationToken);
        var (poller, database, watch) = await BuildAsync(cancellationToken);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(1, result.ImportedCount);
        Assert.Empty(_requests);
        Assert.Empty(await DaysAsync(database, cancellationToken));
        Assert.All(Enum.GetValues<DetectionReadOutcome>(), outcome => Assert.Equal(0, watch.Reads("prod", outcome)));
    }

    [Fact]
    public async Task A_complete_pass_merges_seals_and_moves_the_cursor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pages = new Queue<JsonObject>([
            Page([Today - 2], "a"),
            Page([Today - 1], "b"),
            Page([Today], null),
            Page([Today], null)
        ]);
        await using var daemon = await DaemonAsync(
            Status(Ledger()), context => Serve(context, pages.Dequeue()), cancellationToken);
        var (poller, database, watch) = await BuildAsync(cancellationToken);
        var source = Source(daemon);

        await poller.PollAsync(source, cancellationToken);

        Assert.Equal(
            [
                $"?since_day={Today - 2}&limit=5000",
                $"?since_day={Today - 2}&limit=5000&after=a",
                $"?since_day={Today - 2}&limit=5000&after=b"
            ],
            _requests);
        Assert.Equal(
            [(Today - 2, true), (Today - 1, true), (Today, false)],
            await DaysAsync(database, cancellationToken));
        Assert.Equal(Today, await database.ReadDetectionCursorAsync("prod", DetectionLedgerJson.LedgerId,
            cancellationToken));
        Assert.Equal(3, await CountRowsAsync(database, cancellationToken));

        _requests.Clear();
        await poller.PollAsync(source, cancellationToken);

        Assert.Equal([$"?since_day={Today}&limit=5000"], _requests);
        Assert.Equal(2, watch.Reads("prod", DetectionReadOutcome.Ok));
        Assert.Equal(4, watch.Rows("prod"));
    }

    [Fact]
    public async Task A_failed_second_page_seals_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var served = 0;
        await using var daemon = await DaemonAsync(
            Status(Ledger()),
            context =>
            {
                if (served++ == 0)
                    return Serve(context, Page([Today - 2], "a"));
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.CompletedTask;
            },
            cancellationToken);
        var (poller, database, watch) = await BuildAsync(cancellationToken);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal([(Today - 2, false)], await DaysAsync(database, cancellationToken));
        Assert.Null(await database.ReadDetectionCursorAsync("prod", DetectionLedgerJson.LedgerId, cancellationToken));
        Assert.Equal(1, watch.Reads("prod", DetectionReadOutcome.Failed));
        Assert.Equal(1, watch.Rows("prod"));
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    public async Task A_ledger_gone_mid_pass_seals_nothing(int statusCode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var served = 0;
        await using var daemon = await DaemonAsync(
            Status(Ledger()),
            context =>
            {
                if (served++ == 0)
                    return Serve(context, Page([Today - 2], "a"));
                context.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            },
            cancellationToken);
        var (poller, database, watch) = await BuildAsync(cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal([(Today - 2, false)], await DaysAsync(database, cancellationToken));
        Assert.Null(await database.ReadDetectionCursorAsync("prod", DetectionLedgerJson.LedgerId, cancellationToken));
        Assert.Equal(1, watch.Reads("prod", DetectionReadOutcome.Aborted));
    }

    [Fact]
    public async Task A_ledger_change_mid_pass_aborts_without_sealing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var second = Page([Today - 1], null);
        second["ledger_id"] = OtherLedgerId;
        var pages = new Queue<JsonObject>([Page([Today - 2], "a"), second]);
        await using var daemon = await DaemonAsync(
            Status(Ledger()), context => Serve(context, pages.Dequeue()), cancellationToken);
        var (poller, database, watch) = await BuildAsync(cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        // The page of the new ledger is not merged: its pass starts from its own oldest day.
        Assert.Equal([(Today - 2, false)], await DaysAsync(database, cancellationToken));
        Assert.Null(await database.ReadDetectionCursorAsync("prod", DetectionLedgerJson.LedgerId, cancellationToken));
        Assert.Null(await database.ReadDetectionCursorAsync("prod", OtherLedgerId, cancellationToken));
        Assert.Equal(1, watch.Reads("prod", DetectionReadOutcome.Aborted));
    }

    [Fact]
    public async Task A_first_page_of_another_ledger_than_the_status_named_aborts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var page = Page([Today], null);
        page["ledger_id"] = OtherLedgerId;
        await using var daemon = await DaemonAsync(
            Status(Ledger()), context => Serve(context, page), cancellationToken);
        var (poller, database, watch) = await BuildAsync(cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Empty(await DaysAsync(database, cancellationToken));
        Assert.Equal(1, watch.Reads("prod", DetectionReadOutcome.Aborted));
    }

    [Fact]
    public async Task A_day_sealed_during_a_pass_is_reread_next_pass()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = Page([Today - 2], "a");
        first["sealed_through"] = Today - 2;
        // The daemon sealed yesterday between the two pages, and says so on the second.
        var second = Page([Today - 1], null, true);
        var pages = new Queue<JsonObject>([first, second, Page([Today - 1], null, true)]);
        await using var daemon = await DaemonAsync(
            Status(Ledger(Today - 2)), context => Serve(context, pages.Dequeue()), cancellationToken);
        var (poller, database, _) = await BuildAsync(cancellationToken);
        var source = Source(daemon);

        await poller.PollAsync(source, cancellationToken);

        Assert.Equal([(Today - 2, true), (Today - 1, false)], await DaysAsync(database, cancellationToken));
        Assert.Equal(Today - 1, await database.ReadDetectionCursorAsync("prod", DetectionLedgerJson.LedgerId,
            cancellationToken));

        _requests.Clear();
        await poller.PollAsync(source, cancellationToken);

        Assert.Equal([$"?since_day={Today - 1}&limit=5000"], _requests);
        Assert.Equal([(Today - 2, true), (Today - 1, true)], await DaysAsync(database, cancellationToken));
    }

    [Fact]
    public async Task A_polled_day_keeps_its_carbon_fields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (poller, database, _) = await BuildAsync(cancellationToken);
        var page = DetectionLedgerJson.Page(
            [DetectionLedgerJson.Count(Today)],
            [
                DetectionLedgerJson.Day(
                    Today, avoidableGco2: 12.5, avoidableIoOps: 400, avoidableGco2Low: 6.25, totalIoOps: 5200)
            ],
            Today);
        await using var daemon = await DaemonAsync(
            Status(Ledger()), context => Serve(context, page), cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT avoidable_gco2 || ' ' || avoidable_gco2_low || ' ' || avoidable_io_ops || ' '
                                || total_io_ops FROM detection_days WHERE source_id = 'prod' AND day = $day;
                              """;
        command.Parameters.AddWithValue("$day", Today);
        Assert.Equal("12.5 6.25 400 5200", (string)(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task A_polled_page_keeps_the_signal_fields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (poller, database, _) = await BuildAsync(cancellationToken);
        var page = DetectionLedgerJson.Page(
            [DetectionLedgerJson.Count(Today, detections: 0, extraIoOps: null, belowThresholdPasses: 12)],
            [DetectionLedgerJson.Day(Today, thresholds: new JsonObject { ["max_fanout"] = 20 })],
            Today);
        await using var daemon = await DaemonAsync(
            Status(Ledger()), context => Serve(context, page), cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT (SELECT detections || ' ' || below_threshold_passes FROM detection_counts
                                      WHERE source_id = 'prod' AND day = $day)
                                || ' | ' || (SELECT max_fanout FROM detection_days WHERE source_id = 'prod' AND day = $day);
                              """;
        command.Parameters.AddWithValue("$day", Today);
        Assert.Equal("0 12 | 20", (string)(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task A_complete_pass_never_seals_a_day_below_what_it_read()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (poller, database, _) = await BuildAsync(cancellationToken);
        // A day read partially, its cursor still on it, then pruned by the daemon: its
        // ledger now starts two days later.
        var partial = DetectionLedgerJson.Page(
            [DetectionLedgerJson.Count(Today - 4)], [DetectionLedgerJson.Day(Today - 4)], Today);
        var nowMs = DetectionLedgerJson.Midnight(Today);
        await database.MergeDetectionPageAsync(
            "prod", DetectionLedgerJson.Parse(partial, nowMs), false, nowMs, cancellationToken);
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"UPDATE detection_ledgers SET read_from_day = {Today - 4};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var daemon = await DaemonAsync(
            Status(Ledger()), context => Serve(context, Page([Today - 2, Today - 1], null)), cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal([$"?since_day={Today - 4}&limit=5000"], _requests);
        Assert.Equal(
            [(Today - 4, false), (Today - 2, true), (Today - 1, true)],
            await DaysAsync(database, cancellationToken));
        Assert.Equal(Today, await database.ReadDetectionCursorAsync("prod", DetectionLedgerJson.LedgerId,
            cancellationToken));
    }

    [Fact]
    public async Task An_upper_case_ledger_id_is_the_same_ledger_on_the_status_and_the_pages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string upper = "AAAAAAAA-0000-4000-8000-00000000000A";
        var ledger = Ledger();
        ledger["ledger_id"] = upper;
        var page = Page([Today - 1], null);
        page["ledger_id"] = upper;
        await using var daemon = await DaemonAsync(Status(ledger), context => Serve(context, page), cancellationToken);
        var (poller, database, watch) = await BuildAsync(cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(1, watch.Reads("prod", DetectionReadOutcome.Ok));
        Assert.Equal(Today, await database.ReadDetectionCursorAsync("prod", upper.ToLowerInvariant(),
            cancellationToken));
    }

    [Fact]
    public async Task The_cursor_is_escaped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // A query separator, a plus that would decode as a space, and spaces.
        var cursor = $"{Today - 2}:slow_http:search-svc:GET /search?q=a+b&page=1 #top";
        var afters = new ConcurrentQueue<string?>();
        var pages = new Queue<JsonObject>([Page([Today - 2], cursor), Page([Today], null)]);
        await using var daemon = await DaemonAsync(
            Status(Ledger()),
            context =>
            {
                afters.Enqueue(context.Request.Query.TryGetValue("after", out var after) ? after.ToString() : null);
                return Serve(context, pages.Dequeue());
            },
            cancellationToken);
        var (poller, _, watch) = await BuildAsync(cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal([null, cursor], afters);
        Assert.EndsWith($"&after={Uri.EscapeDataString(cursor)}", _requests.Last(), StringComparison.Ordinal);
        Assert.Equal(1, watch.Reads("prod", DetectionReadOutcome.Ok));
    }

    [Theory]
    [InlineData(StatusCodes.Status200OK, "{not json", DetectionReadOutcome.Failed)]
    [InlineData(StatusCodes.Status200OK, "{\"counts\":[],\"days\":[]}", DetectionReadOutcome.Failed)]
    [InlineData(StatusCodes.Status401Unauthorized, "{\"error\":\"missing or invalid X-API-Key\"}",
        DetectionReadOutcome.Unauthorized)]
    public async Task A_detections_failure_keeps_the_findings_poll_green(
        int statusCode,
        string body,
        DetectionReadOutcome expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonAsync(
            Status(Ledger()),
            async context =>
            {
                context.Response.StatusCode = statusCode;
                await context.Response.WriteAsync(body, cancellationToken);
            },
            cancellationToken);
        var (poller, database, watch) = await BuildAsync(cancellationToken);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(1, result.ImportedCount);
        await AssertReachableAsync(database, cancellationToken);
        Assert.Equal(1, watch.Reads("prod", expected));
    }

    // Unlike an incident read, a storage failure on the ledger stays the ledger's: the
    // findings it would otherwise fail are already committed.
    [Fact]
    public async Task A_storage_failure_on_the_ledger_keeps_the_findings_poll_green()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonAsync(
            Status(Ledger()), context => Serve(context, Page([Today], null)), cancellationToken);
        var (poller, database, watch) = await BuildAsync(cancellationToken);
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE detection_counts;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(1, result.ImportedCount);
        await AssertReachableAsync(database, cancellationToken);
        Assert.Equal(1, watch.Reads("prod", DetectionReadOutcome.Failed));
    }

    [Fact]
    public async Task A_pass_stops_at_the_page_cap_without_sealing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonAsync(
            Status(Ledger()), context => Serve(context, Page([Today - 2], "a")), cancellationToken);
        var (poller, database, watch) = await BuildAsync(cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(DetectionReader.PagesCap, _requests.Count);
        Assert.Equal([(Today - 2, false)], await DaysAsync(database, cancellationToken));
        Assert.Null(await database.ReadDetectionCursorAsync("prod", DetectionLedgerJson.LedgerId, cancellationToken));
        Assert.Equal(1, watch.Reads("prod", DetectionReadOutcome.Capped));
    }

    private static JsonObject Ledger(int sealedThrough = Today - 1)
    {
        return new JsonObject
        {
            ["ledger_id"] = DetectionLedgerJson.LedgerId,
            ["started_ms"] = DetectionLedgerJson.Midnight(Today - 2) + 1_000,
            ["retained_days"] = 31,
            ["oldest_day"] = Today - 2,
            ["current_day"] = Today,
            ["sealed_through"] = sealedThrough
        };
    }

    private static JsonObject Status(JsonObject ledger)
    {
        return new JsonObject { ["detection_ledger"] = ledger };
    }

    // One count and one day object per day, sealed_through yesterday as the ledger says.
    private static JsonObject Page(int[] days, string? nextAfter, bool sealedDay = false)
    {
        var page = DetectionLedgerJson.Page(
            [.. days.Select(day => DetectionLedgerJson.Count(day))],
            [.. days.Select(day => DetectionLedgerJson.Day(day, sealedDay))],
            Today);
        page["next_after"] = nextAfter;
        return page;
    }

    private static Task Serve(HttpContext context, JsonObject page)
    {
        return context.Response.WriteAsync(page.ToJsonString());
    }

    // A daemon whose ledger route is the test's, whose status carries the given
    // fields beside a 0.37.0 version, and which serves one finding and nothing else.
    private Task<FakeDaemon> DaemonAsync(
        JsonObject status,
        Func<HttpContext, Task> detections,
        CancellationToken cancellationToken)
    {
        status["version"] = "0.37.0";
        var statusBody = status.ToJsonString();
        return FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/detections")
            {
                _requests.Enqueue(context.Request.QueryString.Value!);
                await detections(context);
            }
            else if (context.Request.Path == "/api/status")
            {
                await context.Response.WriteAsync(statusBody, cancellationToken);
            }
            else if (context.Request.Path == "/api/findings")
            {
                var findings = await File.ReadAllBytesAsync(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "daemon-findings-0.11.2.json"),
                    cancellationToken);
                await context.Response.Body.WriteAsync(findings, cancellationToken);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        }, cancellationToken);
    }

    private async Task<(SourcePoller Poller, HubDatabase Database, DetectionWatch Watch)> BuildAsync(
        CancellationToken cancellationToken)
    {
        var clock = new FakeTimeProvider(
            DateTimeOffset.FromUnixTimeMilliseconds(DetectionLedgerJson.Midnight(Today) + 3_600_000));
        var options = new HubOptions { DatabasePath = _databasePath, HttpTimeout = TimeSpan.FromSeconds(2) };
        var database = new HubDatabase(Options.Create(options), clock);
        await database.InitializeAsync(cancellationToken);
        var client = new DaemonClient(new HttpClient(), Options.Create(options));
        var watch = new DetectionWatch();
        var poller = new SourcePoller(
            client,
            database,
            new IncidentReader(client, database, NullLogger<IncidentReader>.Instance),
            new AckReader(client, database, NullLogger<AckReader>.Instance),
            new DetectionReader(client, database, watch, NullLogger<DetectionReader>.Instance),
            new RingWatch(),
            clock,
            NullLogger<SourcePoller>.Instance);
        return (poller, database, watch);
    }

    private static SourceOptions Source(FakeDaemon daemon)
    {
        return new SourceOptions
        {
            Id = "prod",
            Name = "Production",
            Environment = "production",
            BaseUrl = daemon.BaseUrl,
            AuthHeaderName = "X-API-Key",
            AuthHeaderValue = "must-not-leak"
        };
    }

    private static async Task<List<(int Day, bool Sealed)>> DaysAsync(
        HubDatabase database,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT day, sealed FROM detection_days WHERE source_id = 'prod' ORDER BY day;";
        var days = new List<(int, bool)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            days.Add((reader.GetInt32(0), reader.GetInt64(1) == 1));
        return days;
    }

    private static async Task<long> CountRowsAsync(HubDatabase database, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM detection_counts WHERE source_id = 'prod';";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task AssertReachableAsync(HubDatabase database, CancellationToken cancellationToken)
    {
        var state = Assert.Contains("prod", await database.QuerySourceStatesAsync(cancellationToken));
        Assert.Null(state.UnreachableSinceMs);
        Assert.Null(state.LastErrorCode);
    }
}
