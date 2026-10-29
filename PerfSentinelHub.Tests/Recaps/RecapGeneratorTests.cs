using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Recaps;

/// <summary>
///     The scheduled pass against its own database. The week under report is 2026-10-19..2026-10-25
///     (days 20745..20751) unless a test says otherwise; the clock only moves forward.
/// </summary>
public sealed partial class RecapGeneratorTests : IAsyncLifetime
{
    private const long DayMs = 86_400_000;
    private const int LastMon = 20745;
    private const int LastSun = 20751;
    private const string Ledger = "00000000-0000-4000-8000-000000000001";
    private const string S = "n_plus_one_sql:cart:0000000000000000000000000000000000000001";

    // HubJsonContext is internal to the Hub; its options are snake_case_lower, which these mirror.
    private static readonly JsonSerializerOptions Wire = new()
    { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static readonly SourceOptions[] Sources =
    [
        new() { Id = "a", Name = "Shop", Environment = "prod", BaseUrl = new Uri("http://127.0.0.1:1") },
        new() { Id = "b", Name = "Lab", Environment = "prod", BaseUrl = new Uri("http://127.0.0.1:2") }
    ];

    private readonly FakeTimeProvider _clock =
        new(DateTimeOffset.Parse("2026-10-26T00:30:00Z", CultureInfo.InvariantCulture));

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-gen-{Guid.NewGuid():N}.db");
    private HubDatabase _database = null!;
    private RecapGenerator _generator = null!;
    private ListLogger<RecapGenerator> _logger = null!;
    private IOptions<HubOptions> _options = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async ValueTask InitializeAsync()
    {
        await StartAsync(Weekly());
    }

    public ValueTask DisposeAsync()
    {
        TestPool.ClearFor(_path);
        File.Delete(_path);
        File.Delete($"{_path}-shm");
        File.Delete($"{_path}-wal");
        return ValueTask.CompletedTask;
    }

    private static RecapSubscriptionOptions Weekly(
        string name = "weekly",
        string cadence = "week",
        string[]? webhooks = null,
        string[]? emails = null)
    {
        return new RecapSubscriptionOptions
        {
            Name = name,
            Cadences = [cadence],
            Views = [RecapViewNames.Pilotage],
            Webhooks = webhooks ?? [],
            Emails = emails ?? []
        };
    }

    private async Task StartAsync(params RecapSubscriptionOptions[] subscriptions)
    {
        _options = Options.Create(new HubOptions
        {
            DatabasePath = _path,
            Sources = [.. Sources],
            Recaps = new RecapsOptions { Subscriptions = subscriptions }
        });
        _database = new HubDatabase(_options, _clock);
        await _database.InitializeAsync(Token);
        _generator = NewGenerator(_database);
    }

    private RecapGenerator NewGenerator(HubDatabase database)
    {
        _logger = new ListLogger<RecapGenerator>();
        return new RecapGenerator(
            database, new RecapService(database, _options, _clock), _options, _clock, _logger);
    }

    // ------------------------------------------------------------ when

    [Fact]
    public async Task A_weekly_report_waits_for_every_ledger_to_seal_sunday()
    {
        await SeedAsync("a", S, LastMon + 1);
        await LedgerAsync("a", [.. Days(LastMon, 7)]);
        await LedgerAsync("b", [.. Days(LastMon, 6), Open(LastSun)]);

        await _generator.RunOnceAsync(Token);
        Assert.Empty(await ReportsAsync());

        await LedgerAsync("b", [Sealed(LastSun)]);
        await _generator.RunOnceAsync(Token);

        var report = Assert.Single(await ReportsAsync());
        Assert.True(report.Complete);
        Assert.Equal("week", report.PeriodKind);
        Assert.Equal("2026-10-19", report.PeriodFrom);
        Assert.Equal("2026-10-25", report.PeriodTo);
        Assert.Equal("weekly", report.Subscription);
        Assert.Null(report.CreatedBy);
    }

    [Fact]
    public async Task Past_the_max_delay_the_report_is_generated_incomplete()
    {
        await LedgerAsync("a", [.. Days(LastMon, 7)]);
        await LedgerAsync("b", [.. Days(LastMon, 6), Open(LastSun)]);
        _clock.SetUtcNow(DateTimeOffset.Parse("2026-10-26T05:59:00Z", CultureInfo.InvariantCulture));
        await _generator.RunOnceAsync(Token);
        Assert.Empty(await ReportsAsync());

        _clock.SetUtcNow(DateTimeOffset.Parse("2026-10-26T06:00:00Z", CultureInfo.InvariantCulture));
        await _generator.RunOnceAsync(Token);

        var report = Assert.Single(await ReportsAsync());
        Assert.False(report.Complete);
        Assert.Contains("open_day", (await SnapshotAsync(report)).Period.Incomplete);
    }

    [Fact]
    public async Task A_day_report_covers_the_utc_day()
    {
        _clock.SetUtcNow(DateTimeOffset.Parse("2026-10-29T00:10:00Z", CultureInfo.InvariantCulture));
        await StartAsync(Weekly(cadence: "day"));

        await _generator.RunOnceAsync(Token);

        var report = Assert.Single(await ReportsAsync());
        Assert.Equal(("day", "2026-10-28", "2026-10-28"), (report.PeriodKind, report.PeriodFrom, report.PeriodTo));
    }

    [Fact]
    public async Task A_month_report_waits_for_the_month_to_close()
    {
        _clock.SetUtcNow(DateTimeOffset.Parse("2026-10-31T23:00:00Z", CultureInfo.InvariantCulture));
        await StartAsync(Weekly(cadence: "month"));
        await _generator.RunOnceAsync(Token);
        var september = Assert.Single(await ReportsAsync());
        Assert.Equal("2026-09-01", september.PeriodFrom);

        // October's last day, 20757, is sealed once the Hub has read the daemon after midnight.
        _clock.SetUtcNow(DateTimeOffset.Parse("2026-11-01T00:30:00Z", CultureInfo.InvariantCulture));
        await LedgerAsync("a", [Open(LastSun + 6)]);
        await _generator.RunOnceAsync(Token);
        Assert.Single(await ReportsAsync());

        await LedgerAsync("a", [Sealed(LastSun + 6)]);
        await _generator.RunOnceAsync(Token);

        var reports = await ReportsAsync();
        Assert.Equal(["2026-10-01", "2026-09-01"], reports.Select(item => item.PeriodFrom));
    }

    [Fact]
    public async Task A_new_subscription_reports_the_last_closed_period_only()
    {
        _clock.SetUtcNow(DateTimeOffset.Parse("2026-10-29T10:00:00Z", CultureInfo.InvariantCulture));

        await _generator.RunOnceAsync(Token);

        Assert.Equal("2026-10-19", Assert.Single(await ReportsAsync()).PeriodFrom);
    }

    // ------------------------------------------------------------ once

    [Fact]
    public async Task A_report_is_generated_once_across_restarts()
    {
        await StartAsync(Weekly(webhooks: ["ops"], emails: ["lead@acme.test"]));
        await SeedAsync("a", S, LastMon + 1);

        await _generator.RunOnceAsync(Token);
        await _generator.RunOnceAsync(Token);
        var restarted = new HubDatabase(_options, _clock);
        await restarted.InitializeAsync(Token);
        await NewGenerator(restarted).RunOnceAsync(Token);

        var report = Assert.Single(await ReportsAsync());
        Assert.Equal(2, (await _database.ListRecapDeliveriesAsync([report.Id], Token)).Count);
    }

    [Fact]
    public async Task Nothing_moved_archives_without_delivery()
    {
        await StartAsync(Weekly(webhooks: ["ops"], emails: ["lead@acme.test"]));
        // The same finding both weeks, 100 then 105 avoidable operations: a flat trend.
        await SeedAsync("a", S, LastMon - 6);
        await SeedAsync("a", S, LastMon + 1);
        await LedgerAsync("a", [.. Days(LastMon - 7, 14)], [Count(LastMon - 7, 100), Count(LastMon, 105)]);

        await _generator.RunOnceAsync(Token);

        var report = Assert.Single(await ReportsAsync());
        Assert.True(report.NothingMoved);
        Assert.Empty(await _database.ListRecapDeliveriesAsync([report.Id], Token));
    }

    [Fact]
    public async Task A_moved_period_queues_one_delivery_per_destination()
    {
        await StartAsync(Weekly(
            webhooks: ["ops", "chat", "ops"], emails: ["lead@acme.test", "cto@acme.test", "LEAD@acme.test"]));
        await SeedAsync("a", S, LastMon + 1);

        await _generator.RunOnceAsync(Token);

        var report = Assert.Single(await ReportsAsync());
        Assert.False(report.NothingMoved);
        var deliveries = await _database.ListRecapDeliveriesAsync([report.Id], Token);
        Assert.Equal(
            [("webhook", "ops"), ("webhook", "chat"), ("email", "lead@acme.test"), ("email", "cto@acme.test")],
            deliveries.Select(delivery => (delivery.Channel, delivery.Target)));
        Assert.All(deliveries, delivery => Assert.Equal(DeliveryStates.Pending, delivery.State));
        Assert.All(deliveries, delivery => Assert.Matches(Hex32(), delivery.Id));
    }

    [Fact]
    public async Task The_report_stores_the_tech_snapshot_and_the_subscription_views()
    {
        await SeedAsync("a", S, LastMon + 1);

        await _generator.RunOnceAsync(Token);

        var report = Assert.Single(await ReportsAsync());
        Assert.Matches(Hex32(), report.Id);
        Assert.Equal([RecapViewNames.Pilotage], report.Views);
        Assert.Equal(NowMs, report.GeneratedAtMs);
        var stored = await _database.GetRecapReportAsync(report.Id, Token);
        Assert.Contains("\"view\":\"tech\"", stored!.SnapshotJson);
        Assert.Equal(S, Assert.Single((await SnapshotAsync(report)).Priorities).Signature);
    }

    // ------------------------------------------------------------ failure

    [Fact]
    public async Task One_failing_subscription_does_not_stop_the_others()
    {
        var broken = Weekly("broken");
        broken.Environment = "nowhere";
        await StartAsync(broken, Weekly("fine"));

        await _generator.RunOnceAsync(Token);

        Assert.Equal("fine", Assert.Single(await ReportsAsync()).Subscription);
        var line = Assert.Single(_logger.Messages, message => message.Contains("broken", StringComparison.Ordinal));
        Assert.Contains("week", line, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ helpers

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex Hex32();

    private Task<IReadOnlyList<RecapReportSummary>> ReportsAsync()
    {
        return _database.ListRecapReportsAsync(null, 50, Token);
    }

    private async Task<RecapSnapshot> SnapshotAsync(RecapReportSummary report)
    {
        var stored = await _database.GetRecapReportAsync(report.Id, Token);
        return JsonSerializer.Deserialize<RecapSnapshot>(stored!.SnapshotJson, Wire)!;
    }

    private Task SeedAsync(string source, string signature, int day)
    {
        var configured = Sources.Single(item => item.Id == source);
        return _database.UpsertBatchAsync(
            new SourceSnapshot(source, configured.Name, configured.Environment, "0.37.1"),
            FindingJson.Batch(FindingJson.Envelope(signature)),
            day * DayMs + 3_600_000, Token);
    }

    private static DetectionDayRow Sealed(int day)
    {
        return new DetectionDayRow(day, true, false, 0, null, 0, 0, 1.0);
    }

    private static DetectionDayRow Open(int day)
    {
        return new DetectionDayRow(day, false, false, 0, null, 0, 0, 1.0);
    }

    private static IEnumerable<DetectionDayRow> Days(int from, int count)
    {
        return Enumerable.Range(from, count).Select(Sealed);
    }

    private static DetectionCountRow Count(int day, long io)
    {
        return new DetectionCountRow(day, S, 1, io);
    }

    private Task LedgerAsync(string source, DetectionDayRow[] days, DetectionCountRow[]? counts = null)
    {
        var page = new DetectionLedgerPage(
            Ledger, (LastMon - 40L) * DayMs, LastSun + 1, LastMon - 30, null, counts ?? [], days, null);
        return _database.MergeDetectionPageAsync(source, page, true, NowMs, Token);
    }
}
