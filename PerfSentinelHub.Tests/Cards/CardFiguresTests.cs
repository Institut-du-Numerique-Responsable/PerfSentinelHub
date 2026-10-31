using System.Text.Json;
using PerfSentinelHub.Cards;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Cards;

public sealed class CardFiguresTests
{
    private const long ObservedMs = 1_791_793_800_000; // 2026-10-12T08:30:00Z
    private static readonly long DoneMs = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly int DoneDay = (int)(DoneMs / 86_400_000);
    private static readonly RecapPeriod October = RecapPeriod.Of(RecapPeriodKind.Month, new DateOnly(2026, 10, 1));
    private static readonly RecapPeriod September = October.Previous();

    private static FrozenFinding NPlusOne(long? occurrences = 41, long? observedAtMs = ObservedMs)
    {
        return new FrozenFinding("n_plus_one_sql", "orders", "GET /orders", occurrences, null, observedAtMs);
    }

    private static FrozenFinding Slow(long? p99Us = 2_345_678, long? observedAtMs = ObservedMs, string endpoint = "GET /orders")
    {
        return new FrozenFinding("slow_sql", "orders", endpoint, 7, p99Us, observedAtMs);
    }

    private static SignalDay Signal(int day, long? slowQueryMs)
    {
        return new SignalDay("sig", "src", day, new DetectionThresholds(slowQueryMs, null, null, null, null, null));
    }

    [Fact]
    public void A_slow_story_cites_p99_in_milliseconds()
    {
        var (figures, period) = CardFigures.StoryFigures(Slow(), RecapPeriodKind.Month, null, null);

        Assert.Equal(new CardFiguresBlock(CitedMs: 2345), figures);
        Assert.Null(period);
    }

    [Fact]
    public void A_story_without_its_figure_is_null()
    {
        Assert.Null(CardFigures.Story(Slow(p99Us: null), DoneMs));
        Assert.Null(CardFigures.Story(NPlusOne(occurrences: null), DoneMs));
        Assert.NotNull(CardFigures.Story(NPlusOne(), DoneMs));
    }

    [Fact]
    public void A_story_with_an_endpoint_over_200_characters_is_null()
    {
        Assert.Null(CardFigures.Story(Slow(endpoint: new string('a', 201)), DoneMs));
        Assert.NotNull(CardFigures.Story(Slow(endpoint: new string('a', 200)), DoneMs));
        Assert.Null(CardFigures.Story(Slow() with { Service = "orders\u202E" }, DoneMs));
    }

    [Fact]
    public void A_story_with_a_service_over_200_characters_is_null_and_unprintable()
    {
        Assert.Null(CardFigures.Story(Slow() with { Service = new string('s', 201) }, DoneMs));
        Assert.True(CardFigures.Unprintable(Slow() with { Service = new string('s', 201) }));
        Assert.NotNull(CardFigures.Story(Slow() with { Service = new string('s', 200) }, DoneMs));
        Assert.False(CardFigures.Unprintable(Slow() with { Service = new string('s', 200) }));
        Assert.False(CardFigures.Unprintable(Slow(p99Us: null) with { Service = new string('s', 201) }));
    }

    [Fact]
    public void The_story_dates_are_utc_days()
    {
        Assert.Equal(
            new CardStory("slow_sql", "orders", "GET /orders", "2026-10-12", "2026-10-20"),
            CardFigures.Story(Slow(), DoneMs));
        Assert.Null(CardFigures.Story(Slow(observedAtMs: null), DoneMs)!.ObservedAt);
    }

    [Fact]
    public void A_trace_dated_after_the_done_day_reads_as_unreadable()
    {
        var skewed = DoneMs + 86_400_000;

        Assert.Null(CardFigures.Story(Slow(observedAtMs: skewed), DoneMs)!.ObservedAt);
        Assert.Equal("2026-10-20", CardFigures.Story(Slow(observedAtMs: DoneMs + 60_000), DoneMs)!.ObservedAt);
    }

    [Fact]
    public void Co2_is_written_only_with_the_volume_before()
    {
        var (figures, period) = CardFigures.StoryFigures(
            NPlusOne(), RecapPeriodKind.Month, new CardBefore(September, null, 5_700_000), null);

        Assert.Equal(new CardFiguresBlock(CitedOps: 41), figures);
        Assert.Null(period);
    }

    [Fact]
    public void The_before_fix_period_is_the_one_summed()
    {
        var (volume, volumePeriod) = CardFigures.StoryFigures(
            NPlusOne(), RecapPeriodKind.Month, new CardBefore(September, 1900, 5_700_000), null);
        Assert.Equal(new CardFiguresBlock(CitedOps: 41, BeforeFixOps: 1900, Co2LowUg: 5_700_000), volume);
        Assert.Equal(new CardDayRange("2026-09-01", "2026-09-30"), volumePeriod);

        var (threshold, thresholdPeriod) = CardFigures.StoryFigures(Slow(), RecapPeriodKind.Month, null, 500);
        Assert.Equal(new CardFiguresBlock(CitedMs: 2345, ThresholdMs: 500), threshold);
        Assert.Equal(new CardDayRange("2026-10-01", "2026-10-31"), thresholdPeriod);

        var (undated, undatedPeriod) = CardFigures.StoryFigures(
            Slow(observedAtMs: null), RecapPeriodKind.Month, null, 500);
        Assert.Equal(new CardFiguresBlock(CitedMs: 2345), undated);
        Assert.Null(undatedPeriod);
    }

    [Fact]
    public void A_threshold_type_other_than_slow_is_written_in_operations()
    {
        var chatty = new FrozenFinding("chatty_service", "orders", "GET /orders", 30, null, ObservedMs);

        var (figures, period) = CardFigures.StoryFigures(chatty, RecapPeriodKind.Quarter, null, 25);

        Assert.Equal(new CardFiguresBlock(CitedOps: 30, ThresholdOps: 25), figures);
        Assert.Equal(new CardDayRange("2026-10-01", "2026-12-31"), period);
    }

    [Fact]
    public void The_threshold_needs_a_day_after_done_with_passes_below()
    {
        Assert.Null(CardFigures.ComparedThreshold("slow_sql", DoneDay, [Signal(DoneDay, 500)]));
        Assert.Null(CardFigures.ComparedThreshold("slow_sql", DoneDay, []));
        Assert.Equal(500, CardFigures.ComparedThreshold("slow_sql", DoneDay, [Signal(DoneDay + 1, 500)]));
        Assert.Null(CardFigures.ComparedThreshold("n_plus_one_sql", DoneDay, [Signal(DoneDay + 1, 500)]));
    }

    [Fact]
    public void The_latest_signal_day_gives_the_threshold()
    {
        Assert.Equal(400, CardFigures.ComparedThreshold(
            "slow_sql", DoneDay, [Signal(DoneDay + 2, 300), Signal(DoneDay + 5, 400), Signal(DoneDay + 3, 500)]));
    }

    [Fact]
    public void Families_count_by_kind()
    {
        Assert.Equal(
            new CardFamilies(2, 1, 2),
            CardFigures.Families(
                ["n_plus_one_sql", "redundant_http", "slow_sql", "chatty_service", "pool_saturation", "brand_new"]));
    }

    [Fact]
    public void A_point_floors_the_waste_ratio()
    {
        var first = October.FromDay;

        Assert.Equal(
            new CardPoint("2026-10-01", "2026-10-31", 333, 1, 3),
            CardFigures.Point(October, [new DayTotals("a", first, 1, 3, null), new DayTotals("b", first, 4, null, null)]));
        Assert.Null(CardFigures.Point(October, [new DayTotals("a", first, 1, null, null)]));
        Assert.Null(CardFigures.Point(October, []));
    }

    [Fact]
    public void A_point_with_more_avoidable_than_total_is_null()
    {
        Assert.Null(CardFigures.Point(October, [new DayTotals("a", October.FromDay, 4, 3, null)]));
        Assert.Null(CardFigures.Point(October, [new DayTotals("a", October.FromDay, 0, 0, null)]));
    }

    [Fact]
    public void Micrograms_floor()
    {
        Assert.Equal(5_700_000, CardFigures.Micrograms(5.7));
        Assert.Equal(0, CardFigures.Micrograms(0.0000004));
        Assert.Null(CardFigures.Micrograms(null));
        Assert.Equal(5_700_000, CardFigures.Micrograms(Enumerable.Repeat(0.3, 19).Sum()));
    }

    // The engine's integers stop at 2^53 - 1: past it the figure is left out, never thrown or refused.
    [Fact]
    public void Micrograms_past_the_engine_bound_read_as_none()
    {
        Assert.Equal(9_000_000_000_000_000, CardFigures.Micrograms(9_000_000_000));
        Assert.Null(CardFigures.Micrograms(10_000_000_000));
        Assert.Null(CardFigures.Micrograms(1e30));
        Assert.Null(CardFigures.Micrograms(double.MaxValue));
    }

    [Fact]
    public void The_range_reads_utc_days()
    {
        Assert.Equal(new CardDayRange("2026-09-01", "2026-09-30"), CardFigures.Range(September));
    }

    [Fact]
    public void The_card_json_is_snake_case_without_absent_values()
    {
        var input = new CardInput(
            "story", "en", "fr", new CardPeriod("month", "2026-10-01", "2026-10-31"),
            new CardScope(null, ["src"], 31, 0, 1000),
            new CardFiguresBlock(CitedOps: 41, BeforeFixOps: 1900, Co2LowUg: 5_700_000),
            new CardStory("n_plus_one_sql", "orders", "GET /orders", null, "2026-10-20"),
            new CardDayRange("2026-09-01", "2026-09-30"),
            new CardFree(null, null, null),
            new CardOrg("Acme", null),
            1_791_793_800_000);

        Assert.Equal(
            """{"angle":"story","lang":"en","equivalence_country":"fr","period":{"kind":"month","from":"2026-10-01","to":"2026-10-31"},"scope":{"sources":["src"],"covered_days":31,"restart_days":0,"sampled_permille":1000},"figures":{"cited_ops":41,"before_fix_ops":1900,"co2_low_ug":5700000},"story":{"finding_type":"n_plus_one_sql","service":"orders","endpoint":"GET /orders","done_at":"2026-10-20"},"before_fix_period":{"from":"2026-09-01","to":"2026-09-30"},"free":{},"org":{"name":"Acme"},"generated_at_ms":1791793800000}""",
            JsonSerializer.Serialize(input, CardJsonContext.Default.CardInput));
    }
}
