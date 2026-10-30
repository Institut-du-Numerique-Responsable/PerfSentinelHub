using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Storage;

// Each test counts at a source of its own, so the other tests' done actions stay off its watch list.
public sealed class CardStorageTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private const int Today = 20741;
    private const long DoneMs = Today * 86_400_000L + 36_000_000;
    private const string LedgerA = "00000000-0000-4000-8000-00000000000a";
    private const string LedgerB = "00000000-0000-4000-8000-00000000000b";

    private HubDatabase Database => factory.Database;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DetectionThresholds Slow500 = new(500, null, null, null, null, null);

    // One pushed page of one ledger: a single count on a single day.
    private Task CountAsync(
        string sourceId, string ledgerId, int day, string signature, long detections, long? passes = null,
        bool sealedDay = false, long? extraIoOps = null)
    {
        return PageAsync(sourceId, ledgerId,
            new DetectionDayRow(day, sealedDay, false, 0, null, 0, 0, 1.0, Thresholds: Slow500),
            new DetectionCountRow(day, signature, detections, extraIoOps, passes));
    }

    private Task PageAsync(string sourceId, string ledgerId, DetectionDayRow day, params DetectionCountRow[] counts)
    {
        var page = new DetectionLedgerPage(ledgerId, 0, day.Day, day.Day, null, counts, [day], null);
        return Database.MergeDetectionPageAsync(sourceId, page, true, DoneMs, Token);
    }

    private Task DoneAsync(string signature, long atMs)
    {
        return Database.CreateActionAsync(signature, ActionStatuses.Done, null, null, "alice", atMs, null, Token);
    }

    [Fact]
    public async Task The_watch_list_is_capped_newest_first()
    {
        string[] signatures = ["slow_sql:capped:a", "slow_http:capped:b", "excessive_fanout:capped:c"];
        for (var index = 0; index < signatures.Length; index++)
        {
            await CountAsync("capped", LedgerA, Today - 1, signatures[index], 1);
            await DoneAsync(signatures[index], DoneMs + index + 1);
        }

        Assert.Equal(["excessive_fanout:capped:c", "slow_http:capped:b"],
            await Database.ReadWatchAsync("capped", 2, Token));
    }

    [Fact]
    public async Task A_day_with_a_detection_in_another_ledger_does_not_confirm()
    {
        // The daemon restarted that day: the new ledger saw only passes below the threshold, the old one a detection.
        const string signature = "slow_sql:restarted:a";
        await CountAsync("restarted", LedgerA, Today - 1, signature, 2);
        await DoneAsync(signature, DoneMs);
        await CountAsync("restarted", LedgerA, Today + 1, signature, 0, 5, true);
        await CountAsync("restarted", LedgerB, Today + 1, signature, 1, null, true);

        Assert.Equal([signature], await Database.ReadWatchAsync("restarted", 10, Token));
    }

    [Fact]
    public async Task Signal_days_are_sealed_zero_detection_rows_with_passes()
    {
        const string signature = "slow_sql:signal:a";
        await CountAsync("signal", LedgerA, Today, signature, 0, 5, true);
        await CountAsync("signal", LedgerA, Today + 1, signature, 0, 5, true);
        await CountAsync("signal", LedgerA, Today + 2, signature, 0, 5);
        await CountAsync("signal", LedgerA, Today + 3, signature, 0, null, true);
        await CountAsync("signal", LedgerA, Today + 4, signature, 2, 5, true);
        await CountAsync("signal-other", LedgerA, Today + 1, signature, 0, 5, true);

        var day = Assert.Single(await Database.ReadSignalDaysAsync([signature], ["signal"], Today, Token));

        Assert.Equal(new SignalDay(signature, "signal", Today + 1, Slow500), day);
    }

    [Fact]
    public async Task A_day_with_a_detection_in_another_ledger_gives_no_signal()
    {
        const string signature = "slow_sql:signal-restart:a";
        await CountAsync("signal-restart", LedgerA, Today + 1, signature, 0, 5, true);
        await CountAsync("signal-restart", LedgerB, Today + 1, signature, 1, null, true);

        Assert.Empty(await Database.ReadSignalDaysAsync([signature], ["signal-restart"], Today, Token));
    }

    [Fact]
    public async Task Day_totals_read_the_three_columns()
    {
        await PageAsync("totals", LedgerA, new DetectionDayRow(Today - 2, true, false, 0, null, 0, 0, 1.0));
        await PageAsync("totals", LedgerA,
            new DetectionDayRow(Today - 1, true, false, 0, null, 0, 0, 1.0, null, 100, 3.0, 1000));
        await PageAsync("totals", LedgerA,
            new DetectionDayRow(Today + 1, true, false, 0, null, 0, 0, 1.0, null, 7, 1.0, 70));
        await PageAsync("totals-other", LedgerA,
            new DetectionDayRow(Today - 1, true, false, 0, null, 0, 0, 1.0, null, 9, 1.0, 90));

        var totals = await Database.ReadDayTotalsAsync(["totals"], Today - 2, Today, Token);

        Assert.Equal(
            [new DayTotals("totals", Today - 2, null, null, null), new DayTotals("totals", Today - 1, 100, 1000, 3.0)],
            totals.OrderBy(row => row.Day));
    }

    [Fact]
    public async Task The_schema_applied_date_is_read()
    {
        await using var connection = await Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT applied_at_ms FROM schema_migrations WHERE version = 20;";
        var applied = (long)(await command.ExecuteScalarAsync(Token))!;

        Assert.Equal(applied, await Database.ReadSchemaAppliedAtAsync(20, Token));
        Assert.Null(await Database.ReadSchemaAppliedAtAsync(9_999, Token));
    }

    [Fact]
    public async Task Carbon_days_read_the_low_coefficient()
    {
        const string signature = "n_plus_one_sql:carbon-low:a";
        await PageAsync("carbon-low", LedgerA,
            new DetectionDayRow(Today - 1, true, false, 0, null, 0, 0, 1.0, 9.0, 1000, 3.0, 5000),
            new DetectionCountRow(Today - 1, signature, 2, 100));

        var (_, day) = Assert.Single(
            await Database.ReadCarbonDaysAsync([signature], ["carbon-low"], Today - 1, Today - 1, Token));

        Assert.Equal(3.0, day.AvoidableGco2Low);
    }
}
