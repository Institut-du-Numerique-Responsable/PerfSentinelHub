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

    private const string CarbonRow = """
                                     SELECT IFNULL(avoidable_gco2, '-') || ' ' || IFNULL(avoidable_gco2_low, '-') || ' '
                                       || IFNULL(avoidable_io_ops, '-') || ' ' || IFNULL(total_io_ops, '-')
                                     FROM detection_days;
                                     """;

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

    private static DetectionLedgerPage CarbonPage(
        double? avoidableGco2 = null,
        long? avoidableIoOps = null,
        double? avoidableGco2Low = null,
        long? totalIoOps = null)
    {
        return new DetectionLedgerPage(
            Ledger, 1_799_000_000_000, Day, Day, null, [],
            [
                new DetectionDayRow(Day, false, false, 0, 0, 0, 0, 1.0, avoidableGco2, avoidableIoOps, avoidableGco2Low,
                    totalIoOps)
            ],
            null);
    }

    [Fact]
    public async Task Carbon_fields_absent_then_present_keep_the_present_values()
    {
        await PollAsync(CarbonPage());
        Assert.Equal("- - - -", await ScalarAsync(CarbonRow));

        await PollAsync(CarbonPage(3.0, 120, 1.5, 900));
        await PollAsync(CarbonPage(2.0, 100, 1.0, 800));

        Assert.Equal("3.0 1.5 120 900", await ScalarAsync(CarbonRow));
    }

    [Fact]
    public async Task Carbon_fields_never_go_back_to_null()
    {
        await PushAsync(CarbonPage(3.0, 120, 1.5, 900));
        await PushAsync(CarbonPage());

        Assert.Equal("3.0 1.5 120 900", await ScalarAsync(CarbonRow));
    }

    private static DetectionLedgerPage SignalPage(long? belowThresholdPasses, DetectionThresholds? thresholds = null)
    {
        return new DetectionLedgerPage(
            Ledger, 1_799_000_000_000, Day, Day, null,
            [new DetectionCountRow(Day, Signature, 0, null, belowThresholdPasses)],
            [new DetectionDayRow(Day, false, false, 0, 0, 0, 0, 1.0, Thresholds: thresholds)],
            null);
    }

    [Fact]
    public async Task Below_threshold_passes_merge_by_maximum_and_survive_an_absent_copy()
    {
        await PushAsync(SignalPage(4));
        await PollAsync(SignalPage(null));
        await PushAsync(SignalPage(3));

        Assert.Equal("4", await ScalarAsync("SELECT IFNULL(below_threshold_passes, '-') FROM detection_counts;"));
    }

    [Fact]
    public async Task Thresholds_merge_by_maximum_per_key()
    {
        const string thresholds = """
                                  SELECT IFNULL(slow_query_threshold_ms, '-') || ' ' || IFNULL(slow_http_threshold_ms, '-')
                                    || ' ' || IFNULL(max_fanout, '-') || ' ' || IFNULL(chatty_threshold, '-') || ' '
                                    || IFNULL(pool_saturation_threshold, '-') || ' ' || IFNULL(serialized_min_calls, '-')
                                  FROM detection_days;
                                  """;
        await PushAsync(SignalPage(null, new DetectionThresholds(500, 1000, 20, 15, 10, 3)));
        await PushAsync(SignalPage(null, new DetectionThresholds(800, null, 10, null, null, null)));
        await PushAsync(SignalPage(null));

        Assert.Equal("800 1000 20 15 10 3", await ScalarAsync(thresholds));
    }

    private static DetectionLedgerPage Days(params int[] days)
    {
        return new DetectionLedgerPage(
            Ledger, 1_799_000_000_000, days[^1], days[0], null, [],
            [.. days.Select(day => new DetectionDayRow(day, false, false, 0, 0, 0, 0, 1.0))],
            null);
    }

    private Task<DetectionMergeOutcome> PollAsync(DetectionLedgerPage page, long observedAt = ObservedAt)
    {
        return _database.MergeDetectionPageAsync(
            "daemon-a", page, false, observedAt, TestContext.Current.CancellationToken);
    }

    private async Task PushAsync(DetectionLedgerPage page, long observedAt = ObservedAt)
    {
        Assert.Equal(DetectionMergeOutcome.Merged, await _database.TryMergeDetectionPageAsync(
            "daemon-a", page, observedAt, TestContext.Current.CancellationToken));
    }

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

        Assert.Equal(
            "20400 5 40 | 20400 0 0 0 0 0 0 1.0 1800000500000 | 1799000000000 1800000500000 1800000500000 1 0 -",
            once);
        Assert.Equal(once, await ScalarAsync(Rows));
    }

    [Fact]
    public async Task A_lower_count_never_lowers_a_stored_one()
    {
        await PollAsync(Page(9, 70, shedTraces: 3, samplingRate: 0.5));
        await PollAsync(Page(4, 30), ObservedAt - 1000);
        await PollAsync(Page(6, 90, shedTraces: 1, samplingRate: 0.8), ObservedAt + 1000);

        Assert.Equal("20400 9 90",
            await ScalarAsync("SELECT day || ' ' || detections || ' ' || extra_io_ops FROM detection_counts;"));
        Assert.Equal("3 0.5 1800000501000",
            await ScalarAsync(
                "SELECT shed_traces || ' ' || sampling_rate || ' ' || last_read_ms FROM detection_days;"));
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
        await _database.SealDetectionPassAsync("daemon-a", Ledger, 20_398, 20_398, null, cancellationToken);
        Assert.Equal("0,0,0", await ScalarAsync("SELECT group_concat(sealed, ',') FROM detection_days;"));

        await _database.SealDetectionPassAsync("daemon-a", Ledger, 20_398, 20_398, 20_399, cancellationToken);

        Assert.Equal("20398 1,20399 1,20400 0",
            await ScalarAsync(
                "SELECT group_concat(day || ' ' || sealed, ',') FROM (SELECT * FROM detection_days ORDER BY day);"));
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

    private static DetectionLedgerPage Split(params DetectionCountRow[] counts)
    {
        return Page() with { Counts = counts, Split = true };
    }

    private static DetectionCountRow Grouped(string grouping, long detections, long? extraIoOps = 10)
    {
        return new DetectionCountRow(Day, Signature, detections, extraIoOps, Grouping: grouping);
    }

    private const string GroupingRows = """
                                        SELECT group_concat(grouping || '=' || detections || '/' || IFNULL(extra_io_ops, '-'), ',')
                                        FROM (SELECT * FROM detection_counts ORDER BY grouping);
                                        """;

    // The finding's volume over Day, read as a windowed listing an hour into the next day.
    private async Task<DetectionVolume> VolumeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const long dayMs = 86_400_000;
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds((Day + 1) * dayMs + 3_600_000));
        var database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _path, Retention = TimeSpan.FromDays(30) }), clock);
        var parsed = FindingParser.Parse(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "daemon-findings-0.11.2.json"),
            cancellationToken));
        await database.UpsertBatchAsync(
            new SourceSnapshot("daemon-a", "Daemon A", "lab", "0.37.0"),
            new ParsedBatch([parsed.Findings[0] with { Signature = Signature }], 0), Day * dayMs, cancellationToken);
        return Assert.Single(await database.QueryFindingsAsync(
            new FindingQuery(null, null, null, 10, FromMs: 0), cancellationToken)).Volume!;
    }

    [Fact]
    public async Task Migration_from_v24_keeps_every_count_with_an_empty_grouping()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var connection = await _database.OpenConnectionAsync(cancellationToken))
        {
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = LegacySchema.WithoutGroupings + $"""
                INSERT INTO detection_days(source_id, ledger_id, day, sealed, gap, overflow_detections,
                  shed_traces, ingest_rejected, sampling_rate, last_read_ms)
                VALUES ('daemon-a', '{Ledger}', {Day}, 1, 0, 0, 0, 0, 1.0, 1);
                INSERT INTO detection_counts(signature, source_id, ledger_id, day, detections, extra_io_ops,
                  below_threshold_passes)
                VALUES ('{Signature}', 'daemon-a', '{Ledger}', {Day}, 7, 70, NULL),
                       ('slow_sql:a', 'daemon-a', '{Ledger}', {Day}, 0, NULL, 4),
                       ('{Signature}', 'daemon-b', '{Ledger}', {Day - 1}, 2, 20, 1);
                """;
            await downgrade.ExecuteNonQueryAsync(cancellationToken);
        }

        TestPool.ClearFor(_path);
        await new HubDatabase(Options.Create(new HubOptions { DatabasePath = _path }), TimeProvider.System)
            .InitializeAsync(cancellationToken);

        Assert.Equal($"{Day - 1} daemon-b 2 20 1 ,{Day} daemon-a 0 - 4 ,{Day} daemon-a 7 70 - ",
            await ScalarAsync("""
                              SELECT group_concat(day || ' ' || source_id || ' ' || detections || ' '
                                || IFNULL(extra_io_ops, '-') || ' ' || IFNULL(below_threshold_passes, '-') || ' '
                                || grouping, ',')
                              FROM (SELECT * FROM detection_counts ORDER BY day, signature DESC);
                              """));
        Assert.Equal("0 1 1 signature,source_id,ledger_id,day,grouping", await ScalarAsync("""
            SELECT (SELECT split FROM detection_days)
              || ' ' || (SELECT count(*) FROM schema_migrations WHERE version = 25)
              || ' ' || (SELECT count(*) FROM sqlite_master WHERE name = 'ix_detection_counts_day')
              || ' ' || (SELECT group_concat(name, ',') FROM (
                   SELECT name FROM pragma_table_info('detection_counts') WHERE pk > 0 ORDER BY pk));
            """));
        // Booting again rebuilds nothing.
        await new HubDatabase(Options.Create(new HubOptions { DatabasePath = _path }), TimeProvider.System)
            .InitializeAsync(cancellationToken);
        Assert.Equal("3", await ScalarAsync("SELECT count(*) FROM detection_counts;"));
    }

    [Fact]
    public async Task Two_groupings_of_one_signature_are_two_rows_and_merge_by_max_each()
    {
        await PushAsync(Split(Grouped("a", 6), Grouped("b", 4, 40)));
        await PushAsync(Split(Grouped("a", 5, 30), Grouped("b", 7)));

        Assert.Equal("a=6/30,b=7/40", await ScalarAsync(GroupingRows));
        Assert.Equal(13, (await VolumeAsync()).Detections);
    }

    [Fact]
    public async Task A_split_page_marks_its_days_split()
    {
        await PollAsync(Days(Day - 1));
        await PushAsync(Split(Grouped("a", 6)));
        Assert.Equal($"{Day - 1} 0,{Day} 1",
            await ScalarAsync("SELECT group_concat(day || ' ' || split, ',') FROM (SELECT * FROM detection_days ORDER BY day);"));

        // An unsplit page onto that day is refused whole, by both paths, and the day stays split.
        Assert.Equal(DetectionMergeOutcome.DaySplit, await PollAsync(Page(50)));
        Assert.Equal(DetectionMergeOutcome.DaySplit, await _database.TryMergeDetectionPageAsync(
            "daemon-a", Page(50) with { Days = [] }, ObservedAt, TestContext.Current.CancellationToken));
        Assert.Equal(DetectionMergeOutcome.Merged, await PollAsync(Days(Day - 1)));
        Assert.Equal("a=6/10", await ScalarAsync(GroupingRows));
        Assert.Equal("1", await ScalarAsync($"SELECT split FROM detection_days WHERE day = {Day};"));
    }

    [Fact]
    public async Task A_first_split_page_replaces_the_unsplit_totals_of_its_day_and_a_later_one_adds()
    {
        await PushAsync(Page(10, 100));
        Assert.Equal(10, (await VolumeAsync()).Detections);

        await PushAsync(Split(Grouped("a", 6), Grouped("b", 4)));
        Assert.Equal("a=6/10,b=4/10", await ScalarAsync(GroupingRows));
        Assert.Equal(10, (await VolumeAsync()).Detections);

        // The pass's next page of the same day: the first page's rows stay.
        await PushAsync(Split(Grouped("", 1, 5)));
        Assert.Equal("=1/5,a=6/10,b=4/10", await ScalarAsync(GroupingRows));
        Assert.Equal(11, (await VolumeAsync()).Detections);
        Assert.Equal(25, (await VolumeAsync()).AvoidableIoOps);
    }

    [Fact]
    public async Task A_day_split_over_pages_with_its_row_on_the_last_keeps_every_page()
    {
        // A large day goes out in pages, its day row on the last one only.
        await PushAsync(Split(Grouped("a", 6)) with { Days = [] });
        await PushAsync(Split(Grouped("b", 4)));
        Assert.Equal("a=6/10,b=4/10", await ScalarAsync(GroupingRows));

        // The same over a day first stored unsplit: the first page replaces the totals, the last adds.
        await PushAsync(Page(20) with
        {
            Days = [new DetectionDayRow(Day + 1, false, false, 0, 0, 0, 0, 1.0)],
            Counts = [new DetectionCountRow(Day + 1, Signature, 20, 10)]
        });
        await PushAsync(Split(new DetectionCountRow(Day + 1, Signature, 15, 10, Grouping: "a")) with { Days = [] });
        await PushAsync(Split(new DetectionCountRow(Day + 1, Signature, 5, 10, Grouping: "b")) with
        {
            Days = [new DetectionDayRow(Day + 1, false, false, 0, 0, 0, 0, 1.0)]
        });

        Assert.Equal("15 5", await ScalarAsync(
            $"SELECT group_concat(detections, ' ') FROM (SELECT * FROM detection_counts WHERE day = {Day + 1} ORDER BY grouping);"));
        Assert.Equal("1 1", await ScalarAsync("SELECT group_concat(split, ' ') FROM detection_days;"));
    }
}
