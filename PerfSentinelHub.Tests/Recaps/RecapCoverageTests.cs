using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Recaps;

public sealed class RecapCoverageTests
{
    private const long DayMs = 86_400_000;

    // Week of Monday 2026-10-26 (day 20752) to Sunday 2026-11-01 (20758).
    private static readonly RecapPeriod Week = RecapPeriod.Of(RecapPeriodKind.Week, new DateOnly(2026, 10, 29));
    private const int Monday = 20752;
    private const int Thursday = 20755;
    private const int NextWeek = 20760;

    private static DayCoverage Day(
        int day,
        bool sealedDay = true,
        bool gap = false,
        long overflow = 0,
        double samplingRate = 1.0,
        string ledger = "L")
    {
        return new DayCoverage(ledger, day, sealedDay, gap, overflow, 0, 0, samplingRate);
    }

    private static SourceCoverage Source(IEnumerable<DayCoverage> days, params LedgerLife[] ledgers)
    {
        return new SourceCoverage("s", [.. days], ledgers);
    }

    private static IEnumerable<DayCoverage> Days(int from, int to)
    {
        return Enumerable.Range(from, to - from + 1).Select(day => Day(day));
    }

    [Fact]
    public void A_sealed_complete_week_is_exact()
    {
        var info = RecapCoverage.Judge(Week, NextWeek, [Source(Days(Monday, Monday + 6))], null);

        Assert.Equal("week", info.Kind);
        Assert.Equal("2026-10-26", info.From);
        Assert.Equal("2026-11-01", info.To);
        Assert.False(info.InProgress);
        Assert.Empty(info.Incomplete);
        Assert.Empty(info.Notes);
        Assert.Equal(1.0, info.SamplingRate);
        Assert.Null(info.TruncatedToDays);
    }

    [Fact]
    public void A_day_without_a_row_is_uncovered()
    {
        var info = RecapCoverage.Judge(Week, NextWeek, [Source(Days(Monday, Monday + 5))], null);

        Assert.Equal(["uncovered_days"], info.Incomplete);
    }

    [Fact]
    public void A_gap_day_is_incomplete_as_restart()
    {
        var days = Days(Monday, Monday + 6).Select(day => day.Day == Monday + 2 ? day with { Gap = true } : day);

        var info = RecapCoverage.Judge(Week, NextWeek, [Source(days)], null);

        Assert.Equal(["restart"], info.Incomplete);
    }

    [Fact]
    public void A_sampled_day_is_a_note_with_its_rate()
    {
        var days = Days(Monday, Monday + 6).Select(day => day.Day == Monday + 1 ? day with { SamplingRate = 0.5 } : day);

        var info = RecapCoverage.Judge(Week, NextWeek, [Source(days)], null);

        Assert.Empty(info.Incomplete);
        Assert.Equal(["sampling"], info.Notes);
        Assert.Equal(0.5, info.SamplingRate);
    }

    [Fact]
    public void Today_in_the_period_is_in_progress_not_incomplete()
    {
        var days = Days(Monday, Thursday - 1).Append(Day(Thursday, false));

        var info = RecapCoverage.Judge(Week, Thursday, [Source(days)], null);

        Assert.True(info.InProgress);
        Assert.Empty(info.Incomplete);
    }

    [Fact]
    public void An_unsealed_yesterday_is_open_day()
    {
        var days = Days(Monday, Thursday - 2).Append(Day(Thursday - 1, false));

        var info = RecapCoverage.Judge(Week, Thursday, [Source(days)], null);

        Assert.Equal(["open_day"], info.Incomplete);
    }

    [Fact]
    public void The_first_day_of_a_period_judges_nothing()
    {
        var info = RecapCoverage.Judge(Week, Monday, [Source([Day(Monday, false)])], null);

        Assert.True(info.InProgress);
        Assert.Empty(info.Incomplete);
        Assert.Empty(info.Notes);
        Assert.Null(info.SamplingRate);
    }

    [Fact]
    public void Overflow_is_incomplete_and_multi_instance_a_note()
    {
        var days = Days(Monday, Monday + 6).Select(day => day.Day == Monday ? day with { OverflowDetections = 3 } : day);
        var start = Monday * DayMs;
        var ledgers = new[]
        {
            new LedgerLife(start, start + 5 * DayMs, true),
            new LedgerLife(start + DayMs, start + 4 * DayMs, true)
        };

        var info = RecapCoverage.Judge(Week, NextWeek, [Source(days, ledgers)], null);

        Assert.Equal(["overflow"], info.Incomplete);
        Assert.Equal(["multi_instance"], info.Notes);
    }

    [Fact]
    public void No_ledger_source_judges_nothing()
    {
        var info = RecapCoverage.Judge(Week, NextWeek, [], null);

        Assert.Empty(info.Incomplete);
        Assert.Empty(info.Notes);
        Assert.Null(info.SamplingRate);
    }

    [Fact]
    public void A_truncated_previous_judges_its_first_days_only()
    {
        var previous = Week.Previous();
        var days = Days(previous.FromDay, previous.FromDay + 3).Concat([Day(previous.FromDay + 5)]);

        var info = RecapCoverage.Judge(previous, Thursday, [Source(days)], 4);

        Assert.Empty(info.Incomplete);
        Assert.False(info.InProgress);
        Assert.Equal(4, info.TruncatedToDays);
        Assert.Equal("2026-10-19", info.From);
        Assert.Equal("2026-10-25", info.To);
    }

    [Fact]
    public void A_previous_truncated_to_zero_days_judges_nothing_and_echoes_it()
    {
        var info = RecapCoverage.Judge(Week.Previous(), Monday, [Source([])], 0);

        Assert.Empty(info.Incomplete);
        Assert.Null(info.SamplingRate);
        Assert.Equal(0, info.TruncatedToDays);
    }
}
