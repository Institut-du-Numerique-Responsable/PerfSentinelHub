using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Recaps;

/// <summary>
///     The recap against its own database, on Thursday 2026-10-29 10:00 UTC unless a test moves the
///     clock. Ledgers are pushed, so a day sealed in a page is sealed in the store.
/// </summary>
public sealed class RecapServiceTests : IAsyncLifetime
{
    private const long DayMs = 86_400_000;

    // Monday 2026-10-26 and the Monday before; today is the Thursday of the first.
    private const int Mon = 20752;
    private const int Thu = 20755;
    private const int LastMon = 20745;
    private const string Ledger = "00000000-0000-4000-8000-000000000001";
    private const string S = "n_plus_one_sql:cart:0000000000000000000000000000000000000001";
    private const string T = "slow_sql:cart:0000000000000000000000000000000000000002";
    private const string U = "slow_sql:cart:0000000000000000000000000000000000000003";
    private const string V = "slow_sql:cart:0000000000000000000000000000000000000004";

    private static readonly SourceOptions[] Sources =
    [
        new() { Id = "a", Name = "Shop", Environment = "prod", BaseUrl = new Uri("http://127.0.0.1:1") },
        new() { Id = "b", Name = "Lab", Environment = "lab", BaseUrl = new Uri("http://127.0.0.1:2") },
        new()
        {
            Id = "t", Name = "Traces", Environment = "prod", Kind = SourceKinds.Tempo,
            BaseUrl = new Uri("http://127.0.0.1:3")
        }
    ];

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-recap-{Guid.NewGuid():N}.db");

    private FakeTimeProvider _clock = Clock("2026-10-29T10:00:00Z");

    private HubDatabase _database = null!;
    private RecapService _service = null!;

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await StartAtAsync(_clock);
    }

    public ValueTask DisposeAsync()
    {
        TestPool.ClearFor(_path);
        File.Delete(_path);
        File.Delete($"{_path}-shm");
        File.Delete($"{_path}-wal");
        return ValueTask.CompletedTask;
    }

    private static FakeTimeProvider Clock(string utc)
    {
        return new FakeTimeProvider(DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture));
    }

    // A fake clock only moves forward, so a test that needs an earlier day starts over on an empty store.
    private async Task StartAtAsync(FakeTimeProvider clock)
    {
        _clock = clock;
        _database = new HubDatabase(Configured(0.2), _clock);
        await _database.InitializeAsync(Token);
        _service = new RecapService(_database, Configured(0.2), _clock);
    }

    private IOptions<HubOptions> Configured(double threshold)
    {
        return Options.Create(new HubOptions
        {
            DatabasePath = _path,
            Sources = [.. Sources],
            Recaps = new RecapsOptions { MovementThreshold = threshold }
        });
    }

    // ------------------------------------------------------------ ranking and tiles

    [Fact]
    public async Task Findings_with_io_rank_first_then_by_severity()
    {
        await SeedAsync("a", "w", Thu);
        await SeedAsync("a", "i", Thu, "info");
        await SeedAsync("a", "c", Thu, "critical");
        await SeedAsync("a", "n", Thu, "info");
        await LedgerAsync("a", [Sealed(Mon), Open(Thu)], [Count(Mon, "w", 50), Count(Mon, "i", 10)]);

        var snapshot = await RecapAsync();

        Assert.Equal(["w", "i", "c", "n"], snapshot.Priorities.Select(priority => priority.Signature));
        Assert.Equal([1, 2, 3, 4], snapshot.Priorities.Select(priority => priority.Rank));
    }

    [Fact]
    public async Task An_acked_finding_is_counted_not_ranked()
    {
        await SeedAsync("a", "big", Thu);
        await SeedAsync("a", "small", Thu);
        await SeedAsync("b", "alone", Thu);
        await LedgerAsync("a", [Sealed(Mon), Open(Thu)], [Count(Mon, "big", 300), Count(Mon, "small", 100)]);
        await _database.ReplaceSourceAcksAsync(
            "a", [new ParsedAck("big", "daemon", "robin", "known", "2026-09-20T10:00:00Z", null, null)],
            AckReadStates.Ok, NowMs, Token);

        var snapshot = await RecapAsync("prod");

        Assert.Equal(1, snapshot.Tiles.AckedFindings);
        Assert.Equal(1, snapshot.Tiles.ActiveFindings);
        Assert.Equal(100, snapshot.Tiles.AvoidableIoOps);
        Assert.Equal(0, snapshot.MorePriorities);
        // The Lab source has no ledger: its finding has no known I/O, which is not zero.
        var lab = await RecapAsync("lab");
        Assert.Null(lab.Tiles.AvoidableIoOps);
        Assert.Equal(0, lab.MorePriorities);
    }

    [Fact]
    public async Task Action_counts_cover_the_current_signatures()
    {
        await SeedAsync("a", S, Thu);
        await SeedAsync("a", T, Thu);
        await SeedAsync("a", U, Mon);
        await SeedAsync("a", V, Mon);
        await SeedAsync("a", "gone", LastMon);
        await LedgerAsync("a", [Sealed(Mon + 1), Sealed(Mon + 2), Open(Thu)], [Count(Mon + 1, V, 5)]);
        await ActionAsync(S, ActionStatuses.Todo);
        await ActionAsync(T, ActionStatuses.InProgress);
        await ActionAsync(U, ActionStatuses.Done);
        await ActionAsync(V, ActionStatuses.Done);
        // Done last week on a finding absent from the current read: not counted.
        await ActionAsync("gone", ActionStatuses.Done, LastMon * DayMs + 43_200_000);

        var actions = (await RecapAsync()).Tiles.Actions;

        Assert.Equal(new RecapActionCounts(1, 1, 2, 1, 1, 0), actions);
    }

    [Fact]
    public async Task A_source_without_a_ledger_ranks_by_severity_and_says_so()
    {
        await SeedAsync("a", "with-io", Thu);
        await SeedAsync("b", "bare", Thu, "critical");
        await LedgerAsync("a", [Sealed(Mon), Open(Thu)], [Count(Mon, "with-io", 50)]);

        var snapshot = await RecapAsync();

        Assert.Equal(["with-io", "bare"], snapshot.Priorities.Select(priority => priority.Signature));
        Assert.True(snapshot.Priorities[0].VolumeKnown);
        Assert.False(snapshot.Priorities[1].VolumeKnown);
        // The Tempo source holds no finding, so it is not listed.
        Assert.Equal(["Lab"], snapshot.SourcesWithoutVolume);
    }

    // ------------------------------------------------------------ periods and coverage

    [Fact]
    public async Task A_week_in_progress_compares_with_the_same_days_of_the_previous_week()
    {
        await SeedAsync("a", S, Thu);
        await LedgerAsync(
            "a",
            [Sealed(Mon), Sealed(Mon + 1), Sealed(Mon + 2), Open(Thu), .. Days(LastMon, 7)],
            [
                Count(Mon, S, 40), Count(Mon + 1, S, 30), Count(Mon + 2, S, 30), Count(Thu, S, 10),
                Count(LastMon, S, 40), Count(LastMon + 1, S, 30), Count(LastMon + 2, S, 30),
                Count(LastMon + 3, S, 100), Count(LastMon + 4, S, 50)
            ]);

        var snapshot = await RecapAsync();

        Assert.Equal("flat", Assert.Single(snapshot.Priorities).Trend);
        Assert.Equal(3, snapshot.Previous.TruncatedToDays);
        Assert.Empty(snapshot.Period.Incomplete);
        Assert.True(snapshot.Period.InProgress);
        Assert.Equal(110, snapshot.Tiles.AvoidableIoOps);
        Assert.Equal(100, snapshot.Tiles.PreviousAvoidableIoOps);
    }

    [Fact]
    public async Task A_partial_today_does_not_read_as_a_drop()
    {
        await StartAtAsync(Clock("2026-10-26T10:00:00Z"));
        await SeedAsync("a", S, Mon);
        await LedgerAsync("a", [Open(Mon), .. Days(LastMon, 7)],
            [Count(Mon, S, 40), Count(LastMon, S, 100)]);

        var snapshot = await RecapAsync();

        Assert.Equal("unknown", Assert.Single(snapshot.Priorities).Trend);
        Assert.Empty(snapshot.Movements.Down);
        Assert.Empty(snapshot.Movements.Up);
        Assert.Equal(0, snapshot.Previous.TruncatedToDays);
        Assert.Empty(snapshot.Period.Incomplete);
    }

    [Fact]
    public async Task A_gap_day_marks_the_period_incomplete_and_the_comparison_inexact()
    {
        await SeedAsync("a", S, Thu);
        await LedgerAsync("a", [Sealed(Mon), Sealed(Mon + 1, gap: true), Sealed(Mon + 2), Open(Thu)],
            [Count(Mon, S, 10)]);

        var snapshot = await RecapAsync();

        Assert.Contains("restart", snapshot.Period.Incomplete);
        Assert.False(snapshot.ComparisonExact);
    }

    [Fact]
    public async Task Sampling_is_a_note_not_an_incompleteness()
    {
        await SeedAsync("a", S, Thu);
        await LedgerAsync("a", [Sealed(Mon), Sealed(Mon + 1, 0.5), Sealed(Mon + 2), Open(Thu), .. Days(LastMon, 7)],
            [Count(Mon, S, 10)]);

        var snapshot = await RecapAsync();

        Assert.Contains("sampling", snapshot.Period.Notes);
        Assert.Empty(snapshot.Period.Incomplete);
        Assert.Equal(0.5, snapshot.Period.SamplingRate);
        Assert.True(snapshot.ComparisonExact);
    }

    [Fact]
    public async Task The_days_before_the_ledger_are_uncovered()
    {
        await SeedAsync("a", S, Thu);
        await LedgerAsync("a", [Sealed(Mon + 2), Open(Thu)], [Count(Mon + 2, S, 10)]);

        var snapshot = await RecapAsync();

        Assert.Contains("uncovered_days", snapshot.Period.Incomplete);
    }

    [Fact]
    public async Task A_retired_ledger_is_not_judged()
    {
        await _database.MergeDetectionPageAsync(
            "b", Page([Sealed(Mon - 20)], []), true, (Mon - 19) * DayMs, Token);
        await LedgerAsync("a", [Sealed(Mon)], []);

        var live = await _database.ReadRecapCoverageAsync(["a", "b"], Mon, Mon + 6, Token);
        var wide = await _database.ReadRecapCoverageAsync(["a", "b"], Mon - 20, Mon + 6, Token);

        Assert.Equal(["a"], live.Select(source => source.SourceId));
        Assert.Equal(["a", "b"], wide.Select(source => source.SourceId));
    }

    [Fact]
    public async Task A_finding_observed_last_week_and_not_this_week_is_gone()
    {
        await SeedAsync("a", S, LastMon);
        await SeedAsync("a", T, Thu);

        var snapshot = await RecapAsync();

        var gone = Assert.Single(snapshot.Movements.Gone);
        Assert.Equal(S, gone.Signature);
        Assert.Equal("gone", gone.Trend);
        Assert.Null(gone.Change);
        Assert.Equal([T], snapshot.Priorities.Select(priority => priority.Signature));
        Assert.False(snapshot.NothingMoved);
    }

    [Fact]
    public async Task Up_and_down_follow_the_configured_threshold()
    {
        await SeedAsync("a", S, Thu);
        await LedgerAsync(
            "a",
            [.. Days(Mon, 3), Open(Thu), .. Days(LastMon, 7)],
            [Count(Mon, S, 60), Count(Mon + 1, S, 40), Count(Mon + 2, S, 40), Count(LastMon, S, 100)]);
        var tolerant = new RecapService(_database, Configured(0.5), _clock);

        var strict = await RecapAsync();
        var lenient = await tolerant.ComputeAsync(Request(), Token);

        Assert.Equal("up", Assert.Single(strict.Priorities).Trend);
        Assert.Equal("flat", Assert.Single(lenient.Priorities).Trend);
    }

    [Fact]
    public async Task Nothing_moved_when_only_small_changes()
    {
        await SeedAsync("a", S, Thu);
        await LedgerAsync(
            "a",
            [.. Days(Mon, 3), Open(Thu), .. Days(LastMon, 7)],
            [Count(Mon, S, 105), Count(LastMon, S, 100)]);

        var snapshot = await RecapAsync();

        Assert.Equal("flat", Assert.Single(snapshot.Priorities).Trend);
        Assert.True(snapshot.NothingMoved);
    }

    // ------------------------------------------------------------ carbon

    [Fact]
    public async Task Co2_is_a_partial_minimum_when_a_source_lacks_a_coefficient()
    {
        await SeedAsync("a", S, Thu);
        await SeedAsync("b", S, Thu);
        await LedgerAsync("a", [Sealed(Mon, gco2: 3.0, avoidable: 120), Open(Thu)], [Count(Mon, S, 40)]);
        await LedgerAsync("b", [Sealed(Mon), Open(Thu)], [Count(Mon, S, 50)]);

        var snapshot = await RecapAsync();

        Assert.Equal(1, snapshot.Tiles.Co2Grams);
        Assert.Equal(1, snapshot.Tiles.Co2SourcesWithoutCoefficient);
        Assert.Null(snapshot.Tiles.Co2MissingReason);
        Assert.True(Assert.Single(snapshot.Priorities).Co2Partial);
    }

    [Fact]
    public async Task Co2_is_null_on_an_old_engine()
    {
        await SeedAsync("a", S, Thu);
        await LedgerAsync("a", [Sealed(Mon), Open(Thu)], [Count(Mon, S, 40)]);

        var snapshot = await RecapAsync();

        Assert.Null(snapshot.Tiles.Co2Grams);
        Assert.Equal("no_coefficient", snapshot.Tiles.Co2MissingReason);
    }

    // ------------------------------------------------------------ actions

    [Fact]
    public async Task A_done_action_is_confirmed_by_a_clean_sealed_day()
    {
        await SeedAsync("a", S, Mon);
        await LedgerAsync("a", [Sealed(Mon + 1), Sealed(Mon + 2), Open(Thu)], []);
        await ActionAsync(S, ActionStatuses.Done);

        var snapshot = await RecapAsync();

        Assert.Equal("confirmed", Assert.Single(snapshot.Priorities).Action!.Confirmation);
        Assert.Equal(1, snapshot.Tiles.Actions.Confirmed);
    }

    [Fact]
    public async Task The_snapshot_carries_the_owner_display()
    {
        await SeedAsync("a", S, Mon);
        await ActionAsync(S, ActionStatuses.InProgress, owner: "alice@acme.com", ownerDisplay: "Alice Martin");

        var snapshot = await RecapAsync();

        var action = Assert.Single(snapshot.Priorities).Action!;
        Assert.Equal("alice@acme.com", action.Owner);
        Assert.Equal("Alice Martin", action.OwnerDisplay);
        var change = Assert.Single(action.Events!);
        Assert.Equal("Alice Martin", change.OwnerDisplay);
        // Names are not technical: the Management view keeps both owner fields.
        var shown = Assert.Single(RecapViews.Pilotage(snapshot).Priorities).Action!;
        Assert.Equal("alice@acme.com", shown.Owner);
        Assert.Equal("Alice Martin", shown.OwnerDisplay);
        Assert.Null(shown.Events);
    }

    [Fact]
    public async Task A_done_action_still_detected_is_listed_as_done_but_still_detected()
    {
        await SeedAsync("a", S, Mon);
        await LedgerAsync("a", [Sealed(Mon + 1), Sealed(Mon + 2), Open(Thu)], [Count(Mon + 1, S, 5)]);
        await ActionAsync(S, ActionStatuses.Done);

        var snapshot = await RecapAsync();

        Assert.Equal(S, Assert.Single(snapshot.Movements.DoneStillDetected).Signature);
        Assert.Empty(snapshot.Movements.CameBack);
        Assert.False(snapshot.NothingMoved);
    }

    [Fact]
    public async Task An_unreachable_source_never_confirms_an_action()
    {
        await SeedAsync("a", S, Mon);
        await LedgerAsync("a", [Sealed(Mon + 1), Sealed(Mon + 2), Open(Thu)], []);
        await ActionAsync(S, ActionStatuses.Done);
        await _database.MarkSourceFailureAsync("a", NowMs, "timeout", Token);

        var snapshot = await RecapAsync();

        Assert.Equal("awaiting", Assert.Single(snapshot.Priorities).Action!.Confirmation);
    }

    [Fact]
    public async Task An_action_is_judged_within_the_filter()
    {
        await SeedAsync("a", S, Mon);
        await SeedAsync("b", S, Mon);
        await LedgerAsync("a", [Sealed(Mon + 1), Sealed(Mon + 2), Open(Thu)], []);
        await LedgerAsync("b", [Sealed(Mon + 1), Sealed(Mon + 2), Open(Thu)], [Count(Mon + 1, S, 5)]);
        await ActionAsync(S, ActionStatuses.Done);

        var shop = await RecapAsync("prod");
        var lab = await RecapAsync("lab");

        Assert.Equal("confirmed", Assert.Single(shop.Priorities).Action!.Confirmation);
        Assert.Equal("contradicted", Assert.Single(lab.Priorities).Action!.Confirmation);
    }

    // ------------------------------------------------------------ helpers

    private Task<RecapSnapshot> RecapAsync(string? environment = null)
    {
        return _service.ComputeAsync(Request(environment), Token);
    }

    private static RecapRequest Request(string? environment = null)
    {
        var ids = environment is null
            ? null
            : (IReadOnlyList<string>)[.. Sources.Where(source => source.Environment == environment).Select(s => s.Id)];
        return new RecapRequest(
            RecapPeriodKind.Week, new DateOnly(2026, 10, 29), ids, null, null, null, Sources,
            new RecapFilters(environment, null, null, null));
    }

    private Task SeedAsync(string source, string signature, int day, string severity = "warning")
    {
        var configured = Sources.Single(item => item.Id == source);
        return _database.UpsertBatchAsync(
            new SourceSnapshot(source, configured.Name, configured.Environment, "0.37.1"),
            FindingJson.Batch(FindingJson.Envelope(signature, severity: severity)),
            day * DayMs + 3_600_000, Token);
    }

    // Done on Monday noon unless said otherwise: the actions here are written by the test, not the route.
    private async Task ActionAsync(
        string signature,
        string status,
        long? atMs = null,
        string? owner = null,
        string? ownerDisplay = null)
    {
        await _database.CreateActionAsync(signature, status, owner, ownerDisplay, "alice",
            atMs ?? Mon * DayMs + 43_200_000, Token);
    }

    private static DetectionDayRow Sealed(
        int day,
        double samplingRate = 1.0,
        bool gap = false,
        double? gco2 = null,
        long? avoidable = null)
    {
        return new DetectionDayRow(day, true, gap, 0, null, 0, 0, samplingRate, gco2, avoidable);
    }

    private static IEnumerable<DetectionDayRow> Days(int from, int count)
    {
        return Enumerable.Range(from, count).Select(day => Sealed(day));
    }

    private static DetectionDayRow Open(int day)
    {
        return new DetectionDayRow(day, false, false, 0, null, 0, 0, 1.0);
    }

    private static DetectionCountRow Count(int day, string signature, long io)
    {
        return new DetectionCountRow(day, signature, 1, io);
    }

    private static DetectionLedgerPage Page(DetectionDayRow[] days, DetectionCountRow[] counts)
    {
        return new DetectionLedgerPage(
            Ledger, (Mon - 40L) * DayMs, Thu, Mon - 30, null, counts, days, null);
    }

    private Task LedgerAsync(string source, DetectionDayRow[] days, DetectionCountRow[] counts)
    {
        return _database.MergeDetectionPageAsync(source, Page(days, counts), true, NowMs, Token);
    }
}
