using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Storage;

/// <summary>Frozen recap reports and their deliveries, as the store keeps and lists them.</summary>
public sealed class RecapReportStorageTests : IAsyncLifetime
{
    private const long NowMs = 1_800_000_000_000;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-recap-{Guid.NewGuid():N}.db");
    private HubDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        _database = new HubDatabase(Options.Create(new HubOptions { DatabasePath = _path }), TimeProvider.System);
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestPool.ClearFor(_path);
        File.Delete(_path);
        File.Delete($"{_path}-shm");
        File.Delete($"{_path}-wal");
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_report_reads_back_verbatim()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // Spacing, key order and non-ASCII text a re-serialisation would lose.
        var snapshot = "{ \"z\" : 1,\n  \"a\": \"é\" }\n";
        var report = Report("r1", "weekly", snapshot, views: [RecapViewNames.Pilotage, RecapViewNames.Tech]);

        Assert.True(await _database.InsertRecapReportAsync(report, [], cancellationToken));

        var read = await _database.GetRecapReportAsync("r1", cancellationToken);
        Assert.NotNull(read);
        Assert.Equal(snapshot, read.SnapshotJson);
        Assert.Equal(report.Summary with { Views = read.Summary.Views }, read.Summary);
        Assert.Equal(report.Summary.Views, read.Summary.Views);
        Assert.Null(await _database.GetRecapReportAsync("absent", cancellationToken));
    }

    [Fact]
    public async Task A_second_report_for_the_same_subscription_and_period_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        (string, string)[] deliveries = [(RecapChannels.Webhook, "ops")];

        Assert.True(await _database.InsertRecapReportAsync(Report("r1", "weekly"), deliveries, cancellationToken));
        Assert.True(await _database.RecapReportExistsAsync("weekly", "week", "2026-10-19", cancellationToken));
        Assert.False(await _database.RecapReportExistsAsync("weekly", "week", "2026-10-26", cancellationToken));
        Assert.False(await _database.InsertRecapReportAsync(Report("r2", "weekly"), deliveries, cancellationToken));

        var reports = await _database.ListRecapReportsAsync(null, 10, cancellationToken);
        Assert.Equal(["r1"], reports.Select(report => report.Id));
        Assert.Single(await _database.ListRecapDeliveriesAsync(["r1", "r2"], cancellationToken));
    }

    [Fact]
    public async Task On_demand_reports_never_conflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await _database.InsertRecapReportAsync(Report("r1", null), [], cancellationToken));
        Assert.True(await _database.InsertRecapReportAsync(Report("r2", null), [], cancellationToken));

        Assert.Equal(2, (await _database.ListRecapReportsAsync(null, 10, cancellationToken)).Count);
    }

    [Fact]
    public async Task Deliveries_land_with_their_report_pending_and_due_at_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        (string, string)[] deliveries = [(RecapChannels.Webhook, "ops"), (RecapChannels.Email, "pm@example.test")];

        await _database.InsertRecapReportAsync(Report("r1", "weekly"), deliveries, cancellationToken);

        var stored = await _database.ListRecapDeliveriesAsync(["r1"], cancellationToken);
        Assert.Equal([("webhook", "ops"), ("email", "pm@example.test")], stored.Select(d => (d.Channel, d.Target)));
        Assert.All(stored, delivery =>
        {
            Assert.Matches("^[0-9a-f]{32}$", delivery.Id);
            Assert.Equal("r1", delivery.ReportId);
            Assert.Equal(DeliveryStates.Pending, delivery.State);
            Assert.Equal(0, delivery.Attempts);
            Assert.Equal(NowMs, delivery.NextAttemptMs);
            Assert.Null(delivery.LastAttemptMs);
            Assert.Null(delivery.LastStatus);
        });
        Assert.Equal(2, (await _database.DueRecapDeliveriesAsync(NowMs, cancellationToken)).Count);
    }

    [Fact]
    public async Task The_list_is_newest_first_and_filters_on_the_creator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _database.InsertRecapReportAsync(Report("old", null, createdBy: "ana", generatedAtMs: NowMs - 10), [],
            cancellationToken);
        await _database.InsertRecapReportAsync(Report("new", null, createdBy: "bob", generatedAtMs: NowMs), [],
            cancellationToken);
        await _database.InsertRecapReportAsync(Report("mid", "weekly", generatedAtMs: NowMs - 5), [],
            cancellationToken);

        var all = await _database.ListRecapReportsAsync(null, 10, cancellationToken);
        var mine = await _database.ListRecapReportsAsync("sub-ana", 10, cancellationToken);
        var limited = await _database.ListRecapReportsAsync(null, 2, cancellationToken);

        Assert.Equal(["new", "mid", "old"], all.Select(report => report.Id));
        Assert.Equal(["old"], mine.Select(report => report.Id));
        Assert.Equal(["new", "mid"], limited.Select(report => report.Id));
        Assert.Equal(["pilotage", "tech"], all[0].Views);
    }

    [Fact]
    public async Task Due_deliveries_skip_the_future_and_the_settled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        (string, string)[] deliveries =
        [
            (RecapChannels.Webhook, "a"), (RecapChannels.Webhook, "b"),
            (RecapChannels.Webhook, "c")
        ];
        await _database.InsertRecapReportAsync(Report("r1", "weekly"), deliveries, cancellationToken);
        var ids = (await _database.ListRecapDeliveriesAsync(["r1"], cancellationToken)).Select(d => d.Id).ToArray();

        await _database.RecordRecapDeliveryAttemptAsync(ids[0], 200, DeliveryStates.Delivered, NowMs, null,
            cancellationToken);
        await _database.RecordRecapDeliveryAttemptAsync(ids[1], 503, DeliveryStates.Pending, NowMs, NowMs + 30_000,
            cancellationToken);

        Assert.Equal([ids[2]], (await _database.DueRecapDeliveriesAsync(NowMs, cancellationToken)).Select(d => d.Id));
        var retried = (await _database.DueRecapDeliveriesAsync(NowMs + 30_000, cancellationToken)).Select(d => d.Id);
        Assert.Equal(ids[1..].Order(), retried.Order());
        var settled = (await _database.ListRecapDeliveriesAsync(["r1"], cancellationToken))[0];
        Assert.Equal((DeliveryStates.Delivered, 1, 200, NowMs, null),
            (settled.State, settled.Attempts, settled.LastStatus, settled.LastAttemptMs, settled.NextAttemptMs));
    }

    [Fact]
    public async Task Purging_drops_old_reports_and_their_deliveries_only()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        (string, string)[] deliveries = [(RecapChannels.Webhook, "ops")];
        await _database.InsertRecapReportAsync(Report("old", "a", generatedAtMs: 100), deliveries, cancellationToken);
        await _database.InsertRecapReportAsync(Report("new", "b", generatedAtMs: 900), deliveries, cancellationToken);

        await _database.PurgeRecapReportsAsync(500, cancellationToken);

        Assert.Equal(["new"], (await _database.ListRecapReportsAsync(null, 10, cancellationToken)).Select(r => r.Id));
        Assert.Equal(["new"],
            (await _database.ListRecapDeliveriesAsync(["old", "new"], cancellationToken)).Select(d => d.ReportId));
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM recap_deliveries;";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(cancellationToken))!);
    }

    private static RecapReportRecord Report(
        string id,
        string? subscription,
        string snapshotJson = "{}",
        string? createdBy = null,
        long generatedAtMs = NowMs,
        IReadOnlyList<string>? views = null)
    {
        return new RecapReportRecord(
            new RecapReportSummary(id, subscription, "week", "2026-10-19", "2026-10-25",
                views ?? RecapViewNames.All, true, false, createdBy, generatedAtMs),
            snapshotJson, createdBy is null ? null : "sub-" + createdBy);
    }
}
