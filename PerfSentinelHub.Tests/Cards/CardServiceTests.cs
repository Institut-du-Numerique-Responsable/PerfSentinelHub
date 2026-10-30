using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Access;
using PerfSentinelHub.Cards;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;
using static PerfSentinelHub.Tests.Support.CardSeed;

namespace PerfSentinelHub.Tests.Cards;

/// <summary>
///     The figures of a share card against its own database, on Wednesday 2026-11-04 10:00 UTC: a month
///     card for October 2026, September before it. Source a (prod-eu) holds the ledger, b (prod-us) none.
/// </summary>
public sealed class CardServiceTests : IAsyncLifetime
{
    private const string N1 = "n_plus_one_sql:order-svc:0000000000000000000000000000000000000001";
    private const string N2 = "n_plus_one_sql:order-svc:0000000000000000000000000000000000000002";
    private const string R1 = "redundant_sql:order-svc:0000000000000000000000000000000000000003";
    private const string S1 = "slow_sql:pay-svc:0000000000000000000000000000000000000004";
    private const string S2 = "slow_sql:pay-svc:0000000000000000000000000000000000000005";
    private const int Done = 20741; // 2026-10-15
    private const long P99Us = 2_345_678;

    private static readonly DetectionThresholds Slow500 = new(500, null, null, null, null, null);

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse(Now, CultureInfo.InvariantCulture));
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-card-{Guid.NewGuid():N}.db");

    private HubDatabase _database = null!;
    private CardSeed _seed = null!;
    private CardService _service = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new HubDatabase(Configured(), _clock);
        await _database.InitializeAsync(Token);
        _seed = new CardSeed(_database);
        _service = new CardService(_database, Configured(), _clock);
    }

    public ValueTask DisposeAsync()
    {
        TestPool.ClearFor(_path);
        File.Delete(_path);
        File.Delete($"{_path}-shm");
        File.Delete($"{_path}-wal");
        return ValueTask.CompletedTask;
    }

    private IOptions<HubOptions> Configured(string? domain = null)
    {
        return Options.Create(new HubOptions
        {
            DatabasePath = _path,
            Sources = [.. Sources],
            Cards = new CardsOptions { Organisation = "Acme", Domain = domain }
        });
    }

    // ------------------------------------------------------------ period and scope

    [Fact]
    public async Task A_closed_complete_month_offers_its_confirmed_fixes()
    {
        await _seed.LedgerAsync("a", Sealed(October));
        await _seed.FixAsync("a", N1, Done);
        await _database.SetEndpointLabelAsync("order-svc", "GET /orders", "Order list", "alice", Token);

        var options = await _service.OptionsAsync(Scope(), Token);

        Assert.Null(options.Refusal);
        Assert.Equal(
            new CardFix(N1, "n_plus_one_sql", "order-svc", "GET /orders", RecapSentences.For("n_plus_one_sql"),
                Done * DayMs + 36_000_000, null, null, "Order list"),
            Assert.Single(options.Fixes));
        Assert.Equal(N1, options.DefaultSignature);
        Assert.Equal(new CardOrg("Acme", null), options.Org);
    }

    [Fact]
    public async Task A_period_still_running_is_refused()
    {
        await _seed.LedgerAsync("a", Sealed(October));
        var scope = Scope(at: new DateOnly(2026, 11, 2));

        Assert.Equal("open_period", await RefusedAsync(Ask("collective", scope)));
        var options = await _service.OptionsAsync(scope, Token);
        Assert.Equal("open_period", options.Refusal);
        Assert.Empty(options.Fixes);
    }

    [Fact]
    public async Task A_period_with_only_restart_days_is_allowed_and_counts_them()
    {
        await _seed.LedgerAsync("a", Sealed(October, day =>
            day.Day is 20730 or 20731 ? day with { Gap = true } : day));
        await _seed.FixAsync("a", N1, Done);

        var input = await ReadyAsync(Ask("collective"));

        Assert.Equal(2, input.Scope.RestartDays);
    }

    [Theory]
    [InlineData("uncovered_days")]
    [InlineData("open_day")]
    [InlineData("overflow")]
    public async Task An_uncovered_open_or_overflowing_day_refuses_the_card(string reason)
    {
        await _seed.LedgerAsync("a", Sealed(October, day => day.Day != 20730
            ? day
            : reason switch
            {
                "uncovered_days" => null,
                "open_day" => day with { Sealed = false },
                _ => day with { OverflowDetections = 3 }
            }));
        await _seed.FixAsync("a", N1, Done);

        Assert.Equal(reason, await RefusedAsync(Ask("collective")));
    }

    [Fact]
    public async Task Sampling_is_carried_in_permille()
    {
        await _seed.LedgerAsync("a", Sealed(October, day => day.Day == 20730 ? day with { SamplingRate = 0.25 } : day));
        await _seed.FixAsync("a", N1, Done);

        Assert.Equal(250, (await ReadyAsync(Ask("collective"))).Scope.SampledPermille);
        Assert.Equal(250, (await _service.OptionsAsync(Scope(), Token)).SampledPermille);
    }

    [Fact]
    public async Task A_source_without_a_ledger_is_left_out_of_the_signed_scope()
    {
        await _seed.LedgerAsync("a", Sealed(October));
        await _seed.FixAsync("a", N1, Done);

        Assert.Equal(["prod-eu"], (await ReadyAsync(Ask("collective"))).Scope.Sources);
        Assert.Equal("uncovered_days", await RefusedAsync(Ask("collective", Scope(ids: ["b"]))));
    }

    [Fact]
    public async Task A_source_whose_ledger_began_after_the_period_is_left_out_of_the_signed_scope()
    {
        await _seed.LedgerAsync("a", Sealed(October));
        await _seed.FixAsync("a", N1, Done);
        // A daemon added in November: its ledger is read today and holds no October day.
        await _database.MergeDetectionPageAsync("b",
            new DetectionLedgerPage("00000000-0000-4000-8000-0000000000c2", (Today - 1) * DayMs, Today, Today - 1,
                null, [], [new DetectionDayRow(Today - 1, true, false, 0, null, 0, 0, 1.0)], null),
            true, NowMs, Token);

        var input = await ReadyAsync(Ask("collective"));

        Assert.Equal(["prod-eu"], input.Scope.Sources);
        Assert.Null((await _service.OptionsAsync(Scope(), Token)).Refusal);
    }

    [Fact]
    public async Task A_source_name_the_card_cannot_print_refuses_it()
    {
        await _seed.LedgerAsync("a", Sealed(October));
        await _seed.FixAsync("a", N1, Done);
        SourceOptions[] renamed = [Sources[0] with { Name = "prod\u202Eeu" }, Sources[1]];

        Assert.Equal("unprintable_scope", await RefusedAsync(Ask("collective", Scope(sources: renamed))));
        Assert.Equal("unprintable_scope",
            await RefusedAsync(Ask("collective", Scope(environment: new string('e', 121)))));
    }

    [Fact]
    public async Task A_scope_line_the_card_cannot_hold_refuses_it()
    {
        await _seed.LedgerAsync("a", Sealed(October));
        await _seed.LedgerAsync("b", Sealed(October));
        await _seed.FixAsync("a", N1, Done);
        // Each name fits alone; the card prints environment and sources on one line, joined with ", ", in 120.
        var fits = Scope(environment: null, sources: Named(59));
        var over = Scope(environment: null, sources: Named(60));

        Assert.Null((await _service.OptionsAsync(fits, Token)).Refusal);
        Assert.Equal("scope_too_long", (await _service.OptionsAsync(over, Token)).Refusal);
        Assert.Equal("scope_too_long", await RefusedAsync(Ask("story", over)));
        Assert.Equal("scope_too_long", await RefusedAsync(Ask("collective", fits with { Environment = "prod" })));
        return;

        static SourceOptions[] Named(int length)
        {
            return [Sources[0] with { Name = new string('e', length) }, Sources[1] with { Name = new string('u', length) }];
        }
    }

    // ------------------------------------------------------------ fixes

    [Fact]
    public async Task A_fix_without_a_frozen_envelope_is_not_offered()
    {
        await _seed.LedgerAsync("a", Sealed(October), new DetectionCountRow(Done, N1, 3, null));
        await _seed.DoneAsync(N1, Done);

        Assert.Empty((await _service.OptionsAsync(Scope(), Token)).Fixes);
        Assert.Equal("no_confirmed_fix", await RefusedAsync(Ask("collective")));
    }

    [Fact]
    public async Task A_fix_that_came_back_is_not_offered()
    {
        await _seed.LedgerAsync("a", Sealed(October), new DetectionCountRow(Done + 4, N1, 2, null));
        await _seed.FixAsync("a", N1, Done);

        var options = await _service.OptionsAsync(Scope(), Token);

        Assert.Empty(options.Fixes);
        Assert.Null(options.DefaultSignature);
    }

    [Fact]
    public async Task A_fix_on_a_service_the_caller_cannot_read_is_left_out()
    {
        await _seed.LedgerAsync("a", Sealed(October));
        await _seed.FixAsync("a", N1, Done);
        await _seed.FixAsync("a", S1, Done, "slow_sql", "pay-svc", p99Us: P99Us);
        var reads = AccessScope.Of([AccessPattern.Compile(new PatternOptions { Service = "pay-svc" })]);

        var options = await _service.OptionsAsync(Scope(reads: reads, whole: []), Token);

        Assert.Equal([S1], options.Fixes.Select(fix => fix.Signature));
        var input = await ReadyAsync(Ask("collective", Scope(reads: reads, whole: [])));
        Assert.Equal(1, input.Figures.ConfirmedFixesCount);
        var nobody = AccessScope.Of([AccessPattern.Compile(new PatternOptions { Service = "cart-svc" })]);
        Assert.Empty((await _service.OptionsAsync(Scope(reads: nobody, whole: []), Token)).Fixes);
    }

    [Fact]
    public async Task A_fix_of_an_unknown_type_is_left_out()
    {
        await _seed.LedgerAsync("a", Sealed(October));
        await _seed.FixAsync("a", "brand_new:order-svc:1", Done, "brand_new");

        Assert.Empty((await _service.OptionsAsync(Scope(), Token)).Fixes);
        Assert.Equal("no_confirmed_fix", await RefusedAsync(Ask("collective")));
    }

    // ------------------------------------------------------------ story

    [Fact]
    public async Task The_default_story_is_the_fix_with_the_largest_volume_before()
    {
        await _seed.LedgerAsync("a", [.. Sealed(September), .. Sealed(October)],
            new DetectionCountRow(20700, N1, 2, 100), new DetectionCountRow(20700, N2, 2, 300));
        await _seed.FixAsync("a", N1, Done + 4);
        await _seed.FixAsync("a", N2, Done);

        Assert.Equal(N2, (await _service.OptionsAsync(Scope(), Token)).DefaultSignature);
        var input = await ReadyAsync(Ask("story"));
        Assert.Equal(300, input.Figures.BeforeFixOps);
    }

    [Fact]
    public async Task Without_volume_the_default_is_the_latest_fix()
    {
        await _seed.LedgerAsync("a", Sealed(October));
        await _seed.FixAsync("a", S1, Done + 4, "slow_sql", "pay-svc", p99Us: P99Us);
        await _seed.FixAsync("a", S2, Done, "slow_sql", "pay-svc", p99Us: P99Us);

        Assert.Equal(S1, (await _service.OptionsAsync(Scope(), Token)).DefaultSignature);
    }

    [Fact]
    public async Task A_story_of_an_unknown_fix_is_refused()
    {
        await _seed.LedgerAsync("a", Sealed(October));
        await _seed.FixAsync("a", N1, Done);

        Assert.Equal("unknown_fix", await RefusedAsync(Ask("story", signature: N2)));
        Assert.Equal("n_plus_one_sql", (await ReadyAsync(Ask("story", signature: N1))).Story!.FindingType);
    }

    [Fact]
    public async Task A_story_of_a_fix_whose_service_the_card_cannot_print_says_so()
    {
        await _seed.LedgerAsync("a", Sealed(October));
        await _seed.FixAsync("a", N1, Done, service: new string('s', 201));

        Assert.Empty((await _service.OptionsAsync(Scope(), Token)).Fixes);
        Assert.Equal("unprintable_fix", await RefusedAsync(Ask("story")));
        Assert.Equal("unprintable_fix", await RefusedAsync(Ask("story", signature: N1)));
        Assert.Equal("no_confirmed_fix", await RefusedAsync(Ask("story", signature: N2)));
        await _seed.FixAsync("a", S1, Done, "slow_sql", "pay-svc", p99Us: P99Us);
        Assert.Equal("unprintable_fix", await RefusedAsync(Ask("story", signature: N1)));
        Assert.Equal("pay-svc", (await ReadyAsync(Ask("story"))).Story!.Service);
    }

    [Fact]
    public async Task The_volume_before_is_the_last_whole_period_before_done()
    {
        // 19 days of 100 extra I/O, each priced at 3.0 g per 1,000 avoidable I/O: 5.7 g.
        await _seed.LedgerAsync("a",
            [
                .. Sealed(September, day => day with { AvoidableGco2Low = 3.0, AvoidableIoOps = 1000 }),
                .. Sealed(October)
            ],
            [.. Enumerable.Range(20700, 19).Select(day => new DetectionCountRow(day, N1, 2, 100))]);
        await _seed.FixAsync("a", N1, Done);

        var input = await ReadyAsync(Ask("story"));

        Assert.Equal(1900, input.Figures.BeforeFixOps);
        Assert.Equal(5_700_000, input.Figures.Co2LowUg);
        Assert.Equal(41, input.Figures.CitedOps);
        Assert.Equal(new CardDayRange("2026-09-01", "2026-09-30"), input.BeforeFixPeriod);
        Assert.Equal(new CardStory("n_plus_one_sql", "order-svc", "GET /orders", "2026-10-12", "2026-10-15"),
            input.Story);
        var fix = Assert.Single((await _service.OptionsAsync(Scope(), Token)).Fixes);
        Assert.Equal((1900, "September 2026"), (fix.BeforeFixOps, fix.BeforePeriodName));
    }

    [Fact]
    public async Task The_volume_before_is_left_out_when_that_period_is_uncovered()
    {
        await _seed.LedgerAsync("a",
            [.. Sealed(September, day => day.Day == 20710 ? null : day), .. Sealed(October)],
            new DetectionCountRow(20700, N1, 2, 100));
        await _seed.FixAsync("a", N1, Done);

        var input = await ReadyAsync(Ask("story"));

        Assert.Equal(new CardFiguresBlock(CitedOps: 41), input.Figures);
        Assert.Null(input.BeforeFixPeriod);
    }

    [Fact]
    public async Task A_slow_story_compares_with_the_threshold_once_a_clean_day_shows_passes_below()
    {
        await SlowStoryAsync(passes: 5);

        var input = await ReadyAsync(Ask("story", signature: S1));

        Assert.Equal(new CardFiguresBlock(CitedMs: 2345, ThresholdMs: 500), input.Figures);
        Assert.Equal("2026-10-12", input.Story!.ObservedAt);
        Assert.Equal(new CardDayRange("2026-10-01", "2026-10-31"), input.BeforeFixPeriod);
    }

    [Fact]
    public async Task Without_passes_below_the_story_cites_no_threshold()
    {
        await SlowStoryAsync(passes: null);

        var input = await ReadyAsync(Ask("story", signature: S1));

        Assert.Equal(new CardFiguresBlock(CitedMs: 2345), input.Figures);
        Assert.Null(input.BeforeFixPeriod);
    }

    [Fact]
    public async Task Without_a_trace_date_the_story_cites_no_threshold()
    {
        await SlowStoryAsync(passes: 5, firstTimestamp: "not a date");

        var input = await ReadyAsync(Ask("story", signature: S1));

        Assert.Equal(new CardFiguresBlock(CitedMs: 2345), input.Figures);
        Assert.Null(input.Story!.ObservedAt);
        Assert.Null(input.BeforeFixPeriod);
    }

    // ------------------------------------------------------------ curve

    [Fact]
    public async Task The_curve_traces_the_waste_ratio_oldest_first()
    {
        await CurveAsync();

        var input = await ReadyAsync(Ask("curve"));

        Assert.Equal([100L, 50L, 10L], input.Figures.WastePermille!);
        Assert.Equal(20, input.Figures.AvoidableOps);
        Assert.Equal(6_000, input.Figures.Co2LowUg);
        Assert.Null(input.Story);
        Assert.Equal(["prod-eu"], input.Scope.Sources);
    }

    [Fact]
    public async Task The_curve_stops_at_the_first_uncovered_period()
    {
        await CurveAsync(uncoveredAugust: true);

        Assert.Equal([50L, 10L], (await ReadyAsync(Ask("curve"))).Figures.WastePermille!);
    }

    [Fact]
    public async Task The_curve_holds_six_periods_at_most()
    {
        var months = Enumerable.Range(4, 7).Select(month => RecapPeriod.Of(RecapPeriodKind.Month, new DateOnly(2026, month, 1)));
        await _seed.LedgerAsync("a",
            [.. months.SelectMany(month => Sealed(month, day => day with { AvoidableIoOps = 1, TotalIoOps = 10 }))]);

        Assert.Equal(Enumerable.Repeat(100L, 6), (await ReadyAsync(Ask("curve"))).Figures.WastePermille!);
    }

    [Fact]
    public async Task A_curve_without_total_io_is_refused()
    {
        await _seed.LedgerAsync("a", [.. Sealed(September), .. Sealed(October)]);

        Assert.Equal("no_total_io", await RefusedAsync(Ask("curve")));
    }

    [Fact]
    public async Task The_curve_needs_whole_sources()
    {
        await CurveAsync();

        Assert.Equal("curve_needs_whole_sources", await RefusedAsync(Ask("curve", Scope(whole: []))));
    }

    [Fact]
    public async Task The_curve_needs_a_complete_previous_period()
    {
        await _seed.LedgerAsync("a", Sealed(October, day => day with { AvoidableIoOps = 1, TotalIoOps = 10 }));

        Assert.Equal("previous_uncovered_days", await RefusedAsync(Ask("curve")));
    }

    // ------------------------------------------------------------ collective

    [Fact]
    public async Task The_collective_counts_fixes_services_and_families_with_the_previous_period()
    {
        await CollectiveAsync(new DateOnly(2026, 8, 1));

        var figures = (await ReadyAsync(Ask("collective"))).Figures;

        Assert.Equal(3, figures.ConfirmedFixesCount);
        Assert.Equal(1, figures.PreviousFixesCount);
        Assert.Equal(2, figures.ServicesCount);
        Assert.Equal(new CardFamilies(2, 1, 0), figures.FixesByFamilyCount);
        Assert.True((await _service.OptionsAsync(Scope(), Token)).PreviousComplete);
    }

    [Fact]
    public async Task The_previous_fixes_are_left_out_before_the_upgrade()
    {
        await CollectiveAsync(new DateOnly(2026, 10, 10));

        Assert.Null((await ReadyAsync(Ask("collective"))).Figures.PreviousFixesCount);
        Assert.False((await _service.OptionsAsync(Scope(), Token)).PreviousComplete);
    }

    [Fact]
    public async Task A_collective_with_an_incomplete_previous_period_drops_its_comparison()
    {
        await _seed.SchemaAppliedAsync(20, new DateOnly(2026, 8, 1));
        await _seed.LedgerAsync("a", Sealed(October), new DetectionCountRow(20700, N1, 2, 100));
        await _seed.FixAsync("a", N1, Done);

        var input = await ReadyAsync(Ask("collective"));

        Assert.Equal(new CardFiguresBlock(ConfirmedFixesCount: 1, ServicesCount: 1,
            FixesByFamilyCount: new CardFamilies(1, 0, 0)), input.Figures);
        Assert.Null(input.BeforeFixPeriod);
        var options = await _service.OptionsAsync(Scope(), Token);
        Assert.False(options.PreviousComplete);
        Assert.Null(options.Refusal);
    }

    [Fact]
    public async Task The_collective_names_the_period_before_its_fixes()
    {
        await _seed.LedgerAsync("a",
            [.. Sealed(September, day => day with { AvoidableGco2Low = 3.0, AvoidableIoOps = 1000 }), .. Sealed(October)],
            new DetectionCountRow(20700, N1, 2, 100), new DetectionCountRow(20701, N2, 2, 200));
        await _seed.FixAsync("a", N1, Done);
        await _seed.FixAsync("a", N2, Done + 1);

        var input = await ReadyAsync(Ask("collective"));

        Assert.Equal(300, input.Figures.BeforeFixOps);
        Assert.Equal(900_000, input.Figures.Co2LowUg);
        Assert.Equal(2, input.Figures.FixesByFamilyCount!.ExtraCalls);
        Assert.Equal(new CardDayRange("2026-09-01", "2026-09-30"), input.BeforeFixPeriod);
    }

    [Fact]
    public async Task A_collective_without_fixes_is_refused()
    {
        await _seed.LedgerAsync("a", Sealed(October));

        Assert.Equal("no_confirmed_fix", await RefusedAsync(Ask("collective")));
        Assert.Equal("no_confirmed_fix", await RefusedAsync(Ask("story")));
    }

    // ------------------------------------------------------------ input and options

    [Fact]
    public async Task The_card_input_names_the_scope_and_the_organisation()
    {
        await _seed.LedgerAsync("a", Sealed(October));
        await _seed.FixAsync("a", N1, Done);
        var free = new CardFree("Our month", "Checkout", null);

        var input = await ReadyAsync(new CardAsk(Scope(), "collective", "fr", null, free));

        Assert.Equal("collective", input.Angle);
        Assert.Equal("fr", input.Lang);
        Assert.Equal(new CardPeriod("month", "2026-10-01", "2026-10-31"), input.Period);
        Assert.Equal("prod", input.Scope.Environment);
        Assert.Equal(["prod-eu"], input.Scope.Sources);
        Assert.Equal((31, 0, 1000), (input.Scope.CoveredDays, input.Scope.RestartDays, input.Scope.SampledPermille));
        Assert.Equal(new CardOrg("Acme", null), input.Org);
        Assert.Equal(free, input.Free);
        Assert.Equal(NowMs, input.GeneratedAtMs);
        var withDomain = new CardService(_database, Configured("acme.example"), _clock);
        Assert.Equal("acme.example",
            Assert.IsType<CardOutcome.Ready>(await withDomain.ComputeAsync(Ask("collective"), Token)).Input.Org.Domain);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.ComputeAsync(Ask("poster"), Token));
    }

    [Fact]
    public async Task Options_carry_the_refusal_sentence_and_the_signed_scope()
    {
        await _seed.LedgerAsync("a", Sealed(October, day => day.Day == 20730 ? null : day));

        var uncovered = await _service.OptionsAsync(Scope(), Token);

        Assert.Equal("uncovered_days", uncovered.Refusal);
        Assert.Equal(CardRules.Explain("uncovered_days", October), uncovered.RefusalText);

        await _seed.LedgerAsync("a", Sealed(October));
        var complete = await _service.OptionsAsync(Scope(), Token);

        Assert.Null(complete.Refusal);
        Assert.Null(complete.RefusalText);
        Assert.Equal(["prod-eu"], complete.Sources);
        Assert.Equal("prod", complete.Environment);
        Assert.Equal("October 2026", complete.PeriodName);
        Assert.Equal(new CardPeriod("month", "2026-10-01", "2026-10-31"), complete.Period);
    }

    // ------------------------------------------------------------ helpers

    private static CardScopeAsk Scope(
        DateOnly? at = null,
        string[]? ids = null,
        string[]? whole = null,
        string? environment = "prod",
        AccessScope? reads = null,
        SourceOptions[]? sources = null)
    {
        ids ??= ["a", "b"];
        return new CardScopeAsk(RecapPeriodKind.Month, at ?? new DateOnly(2026, 10, 1), ids, whole ?? ids, environment,
            reads, sources ?? Sources);
    }

    private static CardAsk Ask(string angle, CardScopeAsk? scope = null, string? signature = null)
    {
        return new CardAsk(scope ?? Scope(), angle, "en", signature, new CardFree(null, null, null));
    }

    private async Task<CardInput> ReadyAsync(CardAsk ask)
    {
        return Assert.IsType<CardOutcome.Ready>(await _service.ComputeAsync(ask, Token)).Input;
    }

    private async Task<string> RefusedAsync(CardAsk ask)
    {
        return Assert.IsType<CardOutcome.Refused>(await _service.ComputeAsync(ask, Token)).Reason;
    }

    // A slow fix done 2026-10-15 on a trace of 2026-10-12; four days later a clean day, with passes below 500 ms if given.
    private async Task SlowStoryAsync(long? passes, string firstTimestamp = "2026-10-12T08:30:00Z")
    {
        await _seed.LedgerAsync("a",
            Sealed(October, day => day.Day == Done + 4 ? day with { Thresholds = Slow500 } : day),
            passes is { } count ? [new DetectionCountRow(Done + 4, S1, 0, null, count)] : []);
        await _seed.FixAsync("a", S1, Done, "slow_sql", "pay-svc", p99Us: P99Us, firstTimestamp: firstTimestamp);
    }

    // August 100/1000, September 50/1000, October 20/2000 with 0.006 g low: July holds nothing.
    private async Task CurveAsync(bool uncoveredAugust = false)
    {
        await _seed.LedgerAsync("a",
        [
            .. Sealed(August, day => uncoveredAugust && day.Day == August.ToDay ? null : day),
            .. Sealed(September),
            .. Sealed(October)
        ]);
        await _seed.LedgerAsync("a",
        [
            new DetectionDayRow(August.FromDay, true, false, 0, null, 0, 0, 1.0, null, 100, null, 1000),
            new DetectionDayRow(September.FromDay, true, false, 0, null, 0, 0, 1.0, null, 50, null, 1000),
            new DetectionDayRow(October.FromDay, true, false, 0, null, 0, 0, 1.0, null, 20, 0.006, 2000)
        ]);
    }

    // Three October fixes on two services (two of extra calls, one slow) and one September fix.
    private async Task CollectiveAsync(DateOnly v20Applied)
    {
        await _seed.SchemaAppliedAsync(20, v20Applied);
        await _seed.LedgerAsync("a", [.. Sealed(September), .. Sealed(October)]);
        await _seed.FixAsync("a", N1, Done);
        await _seed.FixAsync("a", R1, Done + 1, "redundant_sql");
        await _seed.FixAsync("a", S1, Done + 2, "slow_sql", "pay-svc", p99Us: P99Us);
        await _seed.FixAsync("a", N2, 20700);
    }
}
