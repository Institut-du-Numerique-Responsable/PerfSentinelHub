using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Collection;

public sealed class PollingTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-poll-{Guid.NewGuid():N}.db");

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
    public async Task Poll_uses_the_daemon_contract_and_persists_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await File.ReadAllBytesAsync(FixturePath, cancellationToken);
        var incidents = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "daemon-incidents-0.20.0.json"),
            cancellationToken);
        var requests = new List<(string Path, string? Auth)>();
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            requests.Add(
                ($"{context.Request.Path}{context.Request.QueryString}", context.Request.Headers.Authorization));
            if (context.Request.Path == "/api/status")
                await context.Response.WriteAsJsonAsync(new { version = "0.11.2" }, cancellationToken);
            else if (context.Request.Path == "/api/findings")
                await context.Response.Body.WriteAsync(fixture, cancellationToken);
            else if (context.Request.Path == "/api/incidents")
                await context.Response.Body.WriteAsync(incidents, cancellationToken);
            else
                // The catch-all fake lost app.Map's free 404: without this, a
                // future client read would be fed the findings body and pass.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
        }, cancellationToken);

        var options = new HubOptions { DatabasePath = _databasePath, HttpTimeout = TimeSpan.FromSeconds(2) };
        var database = new HubDatabase(Options.Create(options), TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        var poller = new SourcePoller(
            new DaemonClient(new HttpClient(), Options.Create(options)),
            database,
            new IncidentReader(
                new DaemonClient(new HttpClient(), Options.Create(options)),
                database,
                NullLogger<IncidentReader>.Instance),
            new AckReader(
                new DaemonClient(new HttpClient(), Options.Create(options)),
                database,
                NullLogger<AckReader>.Instance),
            new RingWatch(),
            TimeProvider.System,
            NullLogger<SourcePoller>.Instance);
        var source = new SourceOptions
        {
            Id = "prod",
            Name = "Production",
            Environment = "production",
            BaseUrl = daemon.BaseUrl,
            AuthHeaderName = "Authorization",
            AuthHeaderValue = "Bearer test-secret"
        };

        var result = await poller.PollAsync(source, cancellationToken);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.RejectedCount);
        Assert.Equal("0.11.2", result.ProducerVersion);
        Assert.False(result.IsPossiblyTruncated);
        Assert.Equal(1, result.IncidentCount);
        Assert.Equal(
            [
                ("/api/status", "Bearer test-secret"),
                ("/api/findings?limit=1000&include_acked=true", "Bearer test-secret"),
                ("/api/incidents?limit=100&offset=0", "Bearer test-secret")
            ],
            requests);
        Assert.Single(await database.QueryFindingsAsync(
            new FindingQuery(null, null, null, 10),
            cancellationToken));

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT producer_version, unreachable_since_ms, last_error_code
                              FROM source_state WHERE source_id = 'prod';
                              """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal("0.11.2", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public async Task Poll_asks_a_daemon_that_lists_its_baseline_for_its_acks_with_the_read_credential()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await File.ReadAllBytesAsync(FixturePath, cancellationToken);
        var acks = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "daemon-acks-0.24.0.json"),
            cancellationToken);
        var requests = new List<(string Path, string? Auth)>();
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            requests.Add(
                ($"{context.Request.Path}{context.Request.QueryString}", context.Request.Headers.Authorization));
            if (context.Request.Path == "/api/status")
                await context.Response.WriteAsJsonAsync(new { version = "0.24.0" }, cancellationToken);
            else if (context.Request.Path == "/api/findings")
                await context.Response.Body.WriteAsync(fixture, cancellationToken);
            else if (context.Request.Path == "/api/incidents")
                await context.Response.WriteAsync("[]", cancellationToken);
            else if (context.Request.Path == "/api/acks")
                await context.Response.Body.WriteAsync(acks, cancellationToken);
            else
                context.Response.StatusCode = StatusCodes.Status404NotFound;
        }, cancellationToken);

        var options = new HubOptions { DatabasePath = _databasePath, HttpTimeout = TimeSpan.FromSeconds(2) };
        var database = new HubDatabase(Options.Create(options), TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        var poller = new SourcePoller(
            new DaemonClient(new HttpClient(), Options.Create(options)),
            database,
            new IncidentReader(
                new DaemonClient(new HttpClient(), Options.Create(options)),
                database,
                NullLogger<IncidentReader>.Instance),
            new AckReader(
                new DaemonClient(new HttpClient(), Options.Create(options)),
                database,
                NullLogger<AckReader>.Instance),
            new RingWatch(),
            TimeProvider.System,
            NullLogger<SourcePoller>.Instance);
        var source = new SourceOptions
        {
            Id = "prod",
            Name = "Production",
            Environment = "production",
            BaseUrl = daemon.BaseUrl,
            AuthHeaderName = "Authorization",
            AuthHeaderValue = "Bearer test-secret"
        };

        var result = await poller.PollAsync(source, cancellationToken);

        Assert.Equal("0.24.0", result.ProducerVersion);
        Assert.Equal(
            [
                ("/api/status", "Bearer test-secret"),
                ("/api/findings?limit=1000&include_acked=true", "Bearer test-secret"),
                ("/api/incidents?limit=100&offset=0", "Bearer test-secret"),
                ("/api/acks?include_toml=true", "Bearer test-secret")
            ],
            requests);
        var read = Assert.Contains("prod", await database.QueryAckReadsAsync(cancellationToken));
        Assert.Equal(AckReadStates.Ok, read.State);
    }

    [Theory]
    // A daemon before 0.31.2 reports no counter and is not asked for a config.
    [InlineData(false, "true", 0L, true, 0)]
    // The counter but no config: nothing confirms a push.
    [InlineData(true, "missing", null, true, 1)]
    [InlineData(true, "false", null, true, 1)]
    // The daemon pushes to the Hub, which received what the cap cut.
    [InlineData(true, "true", 0L, false, 1)]
    // The export is on but its pushes do not land here: never, or not lately.
    [InlineData(true, "true", null, true, 1)]
    [InlineData(true, "true", 3 * 3_600_000L, true, 1)]
    // A failed or cut config is unread, not a failed poll.
    [InlineData(true, "error", null, true, 1)]
    [InlineData(true, "cut", null, true, 1)]
    // Only incidents, or a batch with no finding, landed lately: none of the
    // findings the cap cut did.
    [InlineData(true, "true", 0L, true, 1, "incidents")]
    [InlineData(true, "true", 0L, true, 1, "empty")]
    // A failed freshness read decides only the warning, never the poll.
    [InlineData(true, "true", null, true, 1, "unreadable")]
    public async Task Poll_reports_a_cap_sized_snapshot_as_possibly_truncated(
        bool reportsTheCounter,
        string config,
        long? importAgeMs,
        bool expectedWarning,
        int expectedConfigReads,
        string push = "findings")
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(
            FixturePath,
            cancellationToken));
        var finding = fixture.RootElement[0].GetRawText();
        var payload = Encoding.UTF8.GetBytes(
            $"[{string.Join(',', Enumerable.Repeat(finding, 1000))}]");
        var configReads = 0;
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/status")
            {
                await context.Response.WriteAsync(
                    reportsTheCounter
                        ? "{\"version\":\"0.34.1\",\"uptime_seconds\":60,\"stored_findings\":1000,"
                          + "\"max_retained_findings\":1000,\"findings_stored_total\":1000}"
                        : "{\"version\":\"0.11.2\"}",
                    cancellationToken);
            }
            else if (context.Request.Path == "/api/findings")
            {
                await context.Response.Body.WriteAsync(payload, cancellationToken);
            }
            else if (context.Request.Path == "/api/config")
            {
                Interlocked.Increment(ref configReads);
                switch (config)
                {
                    case "true" or "false":
                        await context.Response.WriteAsync(
                            $"{{\"hub_export_enabled\":{config}}}",
                            cancellationToken);
                        break;
                    case "error":
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        break;
                    case "cut":
                        context.Response.ContentLength = 64;
                        await context.Response.WriteAsync("{\"hub", cancellationToken);
                        await context.Response.Body.FlushAsync(cancellationToken);
                        context.Abort();
                        break;
                    default:
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        break;
                }
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        }, cancellationToken);
        var options = new HubOptions { DatabasePath = _databasePath };
        var database = new HubDatabase(Options.Create(options), TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        if (importAgeMs is { } age)
        {
            var importedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - age;
            Assert.True(push == "incidents"
                ? await database.TryUpsertPushedIncidentsAsync("capped", [], importedAtMs, cancellationToken)
                : await database.TryUpsertBatchAsync(
                    new SourceSnapshot("capped", "Capped", "test", "0.34.1"),
                    FindingParser.Parse(Encoding.UTF8.GetBytes(push == "empty" ? "[]" : $"[{finding}]")),
                    importedAtMs,
                    cancellationToken));
        }

        if (push == "unreadable")
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE source_finding_imports;";
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        var logger = new ListLogger<SourcePoller>();
        var poller = new SourcePoller(
            new DaemonClient(new HttpClient(), Options.Create(options)),
            database,
            new IncidentReader(
                new DaemonClient(new HttpClient(), Options.Create(options)),
                database,
                NullLogger<IncidentReader>.Instance),
            new AckReader(
                new DaemonClient(new HttpClient(), Options.Create(options)),
                database,
                NullLogger<AckReader>.Instance),
            new RingWatch(),
            TimeProvider.System,
            logger);

        var result = await poller.PollAsync(new SourceOptions
        {
            Id = "capped",
            Name = "Capped",
            Environment = "test",
            BaseUrl = daemon.BaseUrl
        }, cancellationToken);

        Assert.True(result.IsPossiblyTruncated);
        Assert.Equal(
            expectedWarning,
            logger.Messages.Any(message => message.Contains("possibly truncated", StringComparison.Ordinal)));
        Assert.Equal(expectedConfigReads, configReads);
    }

    [Fact]
    public async Task Poll_reads_the_config_once_for_both_warnings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(
            FixturePath,
            cancellationToken));
        var finding = fixture.RootElement[0].GetRawText();
        var capped = Encoding.UTF8.GetBytes(
            $"[{string.Join(',', Enumerable.Repeat(finding, 1000))}]");
        // 400 findings went in between the polls and the ring holds 100: the second
        // poll finds 300 gone and, the fake answering the cap, hits it as well.
        var totals = new Queue<long>([1000, 1400]);
        var configReads = 0;
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/status")
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    version = "0.34.1",
                    uptime_seconds = 60,
                    stored_findings = 100,
                    max_retained_findings = 100,
                    findings_stored_total = totals.Dequeue()
                }, cancellationToken);
            }
            else if (context.Request.Path == "/api/config")
            {
                Interlocked.Increment(ref configReads);
                await context.Response.WriteAsJsonAsync(new { hub_export_enabled = false }, cancellationToken);
            }
            else if (context.Request.Path == "/api/findings")
            {
                await context.Response.Body.WriteAsync(capped, cancellationToken);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        }, cancellationToken);
        var options = new HubOptions { DatabasePath = _databasePath };
        var database = new HubDatabase(Options.Create(options), TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        var client = new DaemonClient(new HttpClient(), Options.Create(options));
        var logger = new ListLogger<SourcePoller>();
        var poller = new SourcePoller(
            client,
            database,
            new IncidentReader(client, database, NullLogger<IncidentReader>.Instance),
            new AckReader(client, database, NullLogger<AckReader>.Instance),
            new RingWatch(),
            TimeProvider.System,
            logger);
        var source = new SourceOptions
        {
            Id = "both",
            Name = "Both",
            Environment = "test",
            BaseUrl = daemon.BaseUrl
        };
        await poller.PollAsync(source, cancellationToken);
        var readsBefore = configReads;
        var logged = logger.Messages.Count;

        var second = await poller.PollAsync(source, cancellationToken);

        var messages = logger.Messages.Skip(logged).ToList();
        Assert.True(second.IsPossiblyTruncated);
        Assert.Equal(300L, second.MissedFindings);
        Assert.Contains(messages, message => message.Contains("possibly truncated", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("left the daemon's ring", StringComparison.Ordinal));
        Assert.Equal(1, configReads - readsBefore);
    }

    [Theory]
    // 300 findings went in between the polls and the ring holds 100.
    [InlineData(false, false, 200L)]
    // The daemon pushes to the Hub, which received what the poll missed.
    [InlineData(true, true, 0L)]
    // The export is on but no push landed here: nothing was delivered.
    [InlineData(true, false, 200L)]
    public async Task Poll_counts_the_findings_that_left_the_ring_between_two_polls(
        bool exportsToTheHub,
        bool pushLanded,
        long expectedMissed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // The same uptime twice: the polls are milliseconds apart, so one process.
        var totals = new Queue<(long Uptime, long Total)>([(60, 100), (60, 400)]);
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/status")
            {
                var (uptime, total) = totals.Dequeue();
                await context.Response.WriteAsJsonAsync(new
                {
                    version = "0.31.2",
                    uptime_seconds = uptime,
                    stored_findings = 100,
                    max_retained_findings = 100,
                    findings_stored_total = total
                }, cancellationToken);
            }
            else if (context.Request.Path == "/api/config")
            {
                await context.Response.WriteAsJsonAsync(
                    new { hub_export_enabled = exportsToTheHub },
                    cancellationToken);
            }
            else if (context.Request.Path == "/api/findings")
            {
                await context.Response.WriteAsync("[]", cancellationToken);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        }, cancellationToken);
        var options = new HubOptions { DatabasePath = _databasePath };
        var database = new HubDatabase(Options.Create(options), TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        if (pushLanded)
            Assert.True(await database.TryUpsertBatchAsync(
                new SourceSnapshot("ring", "Ring", "test", "0.31.2"),
                FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken)),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                cancellationToken));
        var client = new DaemonClient(new HttpClient(), Options.Create(options));
        var rings = new RingWatch();
        var logger = new ListLogger<SourcePoller>();
        var poller = new SourcePoller(
            client,
            database,
            new IncidentReader(client, database, NullLogger<IncidentReader>.Instance),
            new AckReader(client, database, NullLogger<AckReader>.Instance),
            rings,
            TimeProvider.System,
            logger);
        var source = new SourceOptions
        {
            Id = "ring",
            Name = "Ring",
            Environment = "test",
            BaseUrl = daemon.BaseUrl
        };

        var first = await poller.PollAsync(source, cancellationToken);
        var second = await poller.PollAsync(source, cancellationToken);

        Assert.Null(first.MissedFindings);
        Assert.Equal(expectedMissed, second.MissedFindings);
        Assert.Equal(expectedMissed, rings.Missed("ring"));
        Assert.Equal(
            expectedMissed > 0,
            logger.Messages.Any(message => message.Contains("left the daemon's ring", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("http", "http_error")]
    [InlineData("status", "invalid_status")]
    [InlineData("findings", "invalid_findings")]
    [InlineData("large", "response_too_large")]
    [InlineData("timeout", "timeout")]
    public async Task Failure_is_classified_recorded_and_does_not_log_credentials(
        string mode,
        string expectedCode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            switch (mode)
            {
                case "http":
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return;
                case "timeout":
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                    return;
            }

            if (context.Request.Path == "/api/status")
            {
                await context.Response.WriteAsync(
                    mode == "status" ? "{}" : "{\"version\":\"0.11.2\"}",
                    cancellationToken);
                return;
            }

            if (mode == "large")
            {
                var oversized = new byte[16 * 1024 * 1024 + 1];
                context.Response.ContentLength = oversized.Length;
                await context.Response.Body.WriteAsync(oversized, cancellationToken);
                return;
            }

            await context.Response.WriteAsync("{}", cancellationToken);
        }, cancellationToken);
        var options = new HubOptions
        {
            DatabasePath = _databasePath,
            HttpTimeout = mode == "timeout" ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(2)
        };
        var database = new HubDatabase(Options.Create(options), TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        var logger = new ListLogger<SourcePoller>();
        var poller = new SourcePoller(
            new DaemonClient(new HttpClient(), Options.Create(options)),
            database,
            new IncidentReader(
                new DaemonClient(new HttpClient(), Options.Create(options)),
                database,
                NullLogger<IncidentReader>.Instance),
            new AckReader(
                new DaemonClient(new HttpClient(), Options.Create(options)),
                database,
                NullLogger<AckReader>.Instance),
            new RingWatch(),
            TimeProvider.System,
            logger);
        var source = new SourceOptions
        {
            Id = mode,
            Name = mode,
            Environment = "test",
            BaseUrl = daemon.BaseUrl,
            AuthHeaderName = "Authorization",
            AuthHeaderValue = "Bearer must-not-leak"
        };

        var exception = await Assert.ThrowsAsync<SourcePollException>(() =>
            poller.PollAsync(source, cancellationToken));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.Contains(logger.Messages, message => message.Contains(mode, StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains(expectedCode, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("must-not-leak", StringComparison.Ordinal));
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT unreachable_since_ms, last_error_code FROM source_state WHERE source_id = $id;";
        command.Parameters.AddWithValue("$id", mode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.False(reader.IsDBNull(0));
        Assert.Equal(expectedCode, reader.GetString(1));
    }

    [Fact]
    public async Task Success_after_failure_clears_unreachable_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await File.ReadAllBytesAsync(FixturePath, cancellationToken);
        var requestCount = 0;
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            if (context.Request.Path == "/api/status")
                await context.Response.WriteAsync("{\"version\":\"0.11.2\"}", cancellationToken);
            else if (context.Request.Path == "/api/findings")
                await context.Response.Body.WriteAsync(fixture, cancellationToken);
            else
                context.Response.StatusCode = StatusCodes.Status404NotFound;
        }, cancellationToken);
        var options = new HubOptions { DatabasePath = _databasePath };
        var database = new HubDatabase(Options.Create(options), TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        var poller = new SourcePoller(
            new DaemonClient(new HttpClient(), Options.Create(options)),
            database,
            new IncidentReader(
                new DaemonClient(new HttpClient(), Options.Create(options)),
                database,
                NullLogger<IncidentReader>.Instance),
            new AckReader(
                new DaemonClient(new HttpClient(), Options.Create(options)),
                database,
                NullLogger<AckReader>.Instance),
            new RingWatch(),
            TimeProvider.System,
            NullLogger<SourcePoller>.Instance);
        var source = new SourceOptions
        {
            Id = "recovering",
            Name = "Recovering",
            Environment = "test",
            BaseUrl = daemon.BaseUrl
        };

        await Assert.ThrowsAsync<SourcePollException>(() => poller.PollAsync(source, cancellationToken));
        await poller.PollAsync(source, cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT unreachable_since_ms, last_error_code
                              FROM source_state WHERE source_id = 'recovering';
                              """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Theory]
    [InlineData(13, "database_full")]
    [InlineData(5, "storage_error")]
    public void A_storage_failure_names_a_full_database(int sqliteCode, string expected)
    {
        Assert.Equal(expected, SourcePoller.ErrorCode(new SqliteException("failed", sqliteCode)));
    }
}
