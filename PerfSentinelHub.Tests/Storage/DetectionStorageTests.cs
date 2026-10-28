using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Storage;

public sealed class DetectionStorageTests : IAsyncLifetime
{
    private const long ObservedAt = 1_800_000_500_000;
    private const int Day = 20_400;
    private const string Ledger = "6f1c2a8e-3b4d-4e5f-8a9b-0c1d2e3f4a5b";
    private const string Signature = "n_plus_one_sql:order-svc:SELECT * FROM orders WHERE id = ?";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-detections-{Guid.NewGuid():N}.db");

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

    private static DetectionLedgerPage Page(
        long detections = 5,
        long? extraIoOps = 40,
        bool sealedDay = false,
        int? sealedThrough = null,
        long shedTraces = 0,
        double samplingRate = 1.0)
    {
        return new DetectionLedgerPage(
            Ledger, 1_799_000_000_000, Day, Day, sealedThrough,
            [new DetectionCountRow(Day, Signature, detections, extraIoOps)],
            [new DetectionDayRow(Day, sealedDay, false, 0, 0, shedTraces, 0, samplingRate)],
            null);
    }

    private static DetectionLedgerPage Days(params int[] days)
    {
        return new DetectionLedgerPage(
            Ledger, 1_799_000_000_000, days[^1], days[0], null, [],
            [.. days.Select(day => new DetectionDayRow(day, false, false, 0, 0, 0, 0, 1.0))],
            null);
    }

    private Task PollAsync(DetectionLedgerPage page, long observedAt = ObservedAt)
    {
        return _database.MergeDetectionPageAsync(
            "daemon-a", page, false, observedAt, TestContext.Current.CancellationToken);
    }

    private async Task PushAsync(DetectionLedgerPage page, long observedAt = ObservedAt)
    {
        Assert.True(await _database.TryMergeDetectionPageAsync(
            "daemon-a", page, observedAt, TestContext.Current.CancellationToken));
    }

    // Every stored column of the three tables on one line, so a failure prints them all.
    private const string Rows = """
                                SELECT
                                  (SELECT group_concat(day || ' ' || detections || ' ' || IFNULL(extra_io_ops, '-'), ',')
                                   FROM detection_counts) || ' | ' ||
                                  (SELECT group_concat(day || ' ' || sealed || ' ' || gap || ' ' || overflow_detections
                                     || ' ' || IFNULL(overflow_extra_io_ops, '-') || ' ' || shed_traces || ' '
                                     || ingest_rejected || ' ' || sampling_rate || ' ' || last_read_ms, ',')
                                   FROM detection_days) || ' | ' ||
                                  (SELECT group_concat(started_ms || ' ' || first_read_ms || ' ' || last_read_ms || ' '
                                     || polled || ' ' || pushed || ' ' || IFNULL(read_from_day, '-'), ',')
                                   FROM detection_ledgers);
                                """;

    private async Task<string> ScalarAsync(string sql)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture)!;
    }

    [Fact]
    public async Task A_page_merged_twice_is_unchanged()
    {
        await PollAsync(Page());
        var once = await ScalarAsync(Rows);
        await PollAsync(Page());

        Assert.Equal("20400 5 40 | 20400 0 0 0 0 0 0 1.0 1800000500000 | 1799000000000 1800000500000 1800000500000 1 0 -",
            once);
        Assert.Equal(once, await ScalarAsync(Rows));
    }

    [Fact]
    public async Task A_lower_count_never_lowers_a_stored_one()
    {
        await PollAsync(Page(9, 70, shedTraces: 3, samplingRate: 0.5));
        await PollAsync(Page(4, 30), ObservedAt - 1000);
        await PollAsync(Page(6, 90, shedTraces: 1, samplingRate: 0.8), ObservedAt + 1000);

        Assert.Equal("20400 9 90", await ScalarAsync("SELECT day || ' ' || detections || ' ' || extra_io_ops FROM detection_counts;"));
        Assert.Equal("3 0.5 1800000501000",
            await ScalarAsync("SELECT shed_traces || ' ' || sampling_rate || ' ' || last_read_ms FROM detection_days;"));
        Assert.Equal("1800000499000 1800000501000",
            await ScalarAsync("SELECT first_read_ms || ' ' || last_read_ms FROM detection_ledgers;"));
    }

    [Fact]
    public async Task Io_stays_null_once_a_null_merged()
    {
        await PollAsync(Page(extraIoOps: 40));
        await PollAsync(Page(extraIoOps: null));
        await PollAsync(Page(extraIoOps: 80));

        Assert.Equal("-", await ScalarAsync("SELECT IFNULL(extra_io_ops, '-') FROM detection_counts;"));
    }

    [Fact]
    public async Task A_poll_page_never_seals()
    {
        await PollAsync(Page(sealedDay: true, sealedThrough: Day));

        Assert.Equal("0", await ScalarAsync("SELECT sealed FROM detection_days;"));
        Assert.Equal("-", await ScalarAsync("SELECT IFNULL(read_from_day, '-') FROM detection_ledgers;"));
    }

    [Fact]
    public async Task Sealing_a_pass_seals_up_to_the_first_page_and_moves_the_cursor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await PollAsync(Days(20_398, 20_399, 20_400));
        Assert.Null(await _database.ReadDetectionCursorAsync("daemon-a", Ledger, cancellationToken));

        // A pass whose first page had no sealed day leaves everything as it was.
        await _database.SealDetectionPassAsync("daemon-a", Ledger, null, cancellationToken);
        Assert.Equal("0,0,0", await ScalarAsync("SELECT group_concat(sealed, ',') FROM detection_days;"));

        await _database.SealDetectionPassAsync("daemon-a", Ledger, 20_399, cancellationToken);

        Assert.Equal("20398 1,20399 1,20400 0",
            await ScalarAsync("SELECT group_concat(day || ' ' || sealed, ',') FROM (SELECT * FROM detection_days ORDER BY day);"));
        Assert.Equal(20_400, await _database.ReadDetectionCursorAsync("daemon-a", Ledger, cancellationToken));
        Assert.Null(await _database.ReadDetectionCursorAsync(
            "daemon-a", "00000000-0000-4000-8000-000000000000", cancellationToken));
        Assert.Null(await _database.ReadDetectionCursorAsync("daemon-b", Ledger, cancellationToken));
    }

    [Fact]
    public async Task A_pushed_sealed_day_is_sealed()
    {
        await PushAsync(Page(sealedDay: true));
        // A later poll of the same day, which always writes 0, never unseals it.
        await PollAsync(Page());

        Assert.Equal("1", await ScalarAsync("SELECT sealed FROM detection_days;"));
    }

    [Fact]
    public async Task A_push_marks_the_ledger_pushed_and_a_poll_polled()
    {
        await PushAsync(Page());
        Assert.Equal("0 1", await ScalarAsync("SELECT polled || ' ' || pushed FROM detection_ledgers;"));

        await PollAsync(Page());
        Assert.Equal("1 1", await ScalarAsync("SELECT polled || ' ' || pushed FROM detection_ledgers;"));
    }

    [Fact]
    public async Task A_push_files_source_imports_and_not_source_state()
    {
        await PushAsync(Page());

        Assert.Equal("1800000500000",
            await ScalarAsync("SELECT last_import_ms FROM source_imports WHERE source_id = 'daemon-a';"));
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM source_state;"));
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM source_finding_imports;"));

        await PollAsync(Page(), ObservedAt + 1000);
        Assert.Equal("1800000500000",
            await ScalarAsync("SELECT last_import_ms FROM source_imports WHERE source_id = 'daemon-a';"));
    }

    [Fact]
    public async Task A_windowed_read_counts_from_the_retention_cutoff_to_the_hub_day()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const long dayMs = 86_400_000;
        // An hour into the day after Day, keeping thirty days.
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds((Day + 1) * dayMs + 3_600_000));
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _path, Retention = TimeSpan.FromDays(30) }), clock);
        var parsed = FindingParser.Parse(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "daemon-findings-0.11.2.json"),
            cancellationToken));
        var finding = parsed.Findings[0] with { Signature = Signature };
        await database.UpsertBatchAsync(
            new SourceSnapshot("daemon-a", "Daemon A", "lab", "0.37.0"), new ParsedBatch([finding], 0),
            Day * dayMs, cancellationToken);
        await database.MergeDetectionPageAsync("daemon-a", Page(), true, Day * dayMs, cancellationToken);

        var row = Assert.Single(await database.QueryFindingsAsync(
            new FindingQuery(null, null, null, 10, FromMs: 0), cancellationToken));

        Assert.Equal(new VolumeWindow(Day - 29, Day + 1), row.Window);
        Assert.Equal(5, row.Volume!.Detections);
        Assert.Equal(["open_day", "uncovered_days"], row.Volume.Gaps);
    }

    [Fact]
    public async Task An_unwindowed_read_answers_whatever_the_retention()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _path, Retention = TimeSpan.MaxValue }),
            TimeProvider.System);

        Assert.Empty(await database.QueryFindingsAsync(new FindingQuery(null, null, null, 10), cancellationToken));
    }
}
