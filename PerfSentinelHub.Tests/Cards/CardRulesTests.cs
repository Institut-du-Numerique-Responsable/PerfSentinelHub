using PerfSentinelHub.Cards;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Cards;

public sealed class CardRulesTests
{
    private static readonly RecapPeriod October = RecapPeriod.Of(RecapPeriodKind.Month, new DateOnly(2026, 10, 1));

    private static RecapPeriodInfo Info(params string[] incomplete)
    {
        return new RecapPeriodInfo("month", "2026-10-01", "2026-10-31", false, incomplete, [], null, null);
    }

    private static DayCoverage Day(int day, bool gap)
    {
        return new DayCoverage("L", day, true, gap, 0, 0, 0, 1.0);
    }

    [Fact]
    public void Only_restart_is_tolerated()
    {
        Assert.Null(CardRules.Refusal(Info("restart")));
        Assert.Null(CardRules.Refusal(Info()));
        Assert.Equal("overflow", CardRules.Refusal(Info("overflow", "restart")));
        Assert.Equal("open_day", CardRules.Refusal(Info("uncovered_days", "open_day")));
        Assert.Equal("previous_overflow", CardRules.Refusal(Info("overflow", "restart"), "previous_"));
    }

    [Fact]
    public void Restart_days_count_distinct_gap_days_in_the_period()
    {
        var first = October.FromDay;
        SourceCoverage[] sources =
        [
            new("a", [Day(first, true), Day(first + 1, false), Day(first + 3, true), Day(first - 1, true)], []),
            new("b", [Day(first, true), Day(first + 3, true)], [])
        ];

        Assert.Equal(2, CardRules.RestartDays(sources, October));
    }

    [Theory]
    [InlineData(0.2505, 250)]
    [InlineData(0.0004, 0)]
    [InlineData(0.29, 290)]
    [InlineData(null, 1000)]
    public void Sampled_permille_floors(double? rate, int expected)
    {
        Assert.Equal(expected, CardRules.SampledPermille(rate));
    }

    [Fact]
    public void Every_reason_has_a_sentence()
    {
        const string previous = "No curve for October 2026. The curve compares with September 2026, and ";
        const string still = " A story or a collective card still works for October 2026.";
        var expected = new Dictionary<string, string>
        {
            ["open_period"] = "A card covers a closed period. Pick an earlier one.",
            ["uncovered_days"] = "Some days of this period were not recorded, so its figures are not whole.",
            ["open_day"] = "Some days of this period are still being counted. Try again tomorrow.",
            ["overflow"] =
                "On some days of this period there were too many different problems to count each one, so its figures are not whole.",
            ["previous_uncovered_days"] = previous + "some of its days were not recorded." + still,
            ["previous_open_day"] = previous + "some of its days are still being counted." + still,
            ["previous_overflow"] =
                previous + "on some of its days there were too many different problems to count each one." + still,
            ["no_confirmed_fix"] = "No fix of this period is confirmed by the measure yet.",
            ["unknown_fix"] = "This fix is not among the confirmed fixes of the period. Pick one from the list.",
            ["no_total_io"] =
                "The curve needs the total activity of each day, which this period or the one before does not hold yet.",
            ["curve_needs_whole_sources"] = "The curve covers whole sources, and you read only part of one.",
            ["unprintable_scope"] =
                "A source or environment name is too long or holds a hidden character, so it cannot be printed on a card. Ask an administrator to rename it."
        };

        Assert.Equal(expected.Keys, CardRules.Reasons);
        Assert.All(expected, pair => Assert.Equal(pair.Value, CardRules.Explain(pair.Key, October)));
        Assert.Equal(
            "No curve for October 2026. The curve compares with September 2026, and some of its days were not recorded. A story or a collective card still works for October 2026.",
            CardRules.Explain("previous_uncovered_days", October));
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("previous_restart")]
    public void A_reason_without_a_sentence_throws(string reason)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CardRules.Explain(reason, October));
    }

    [Theory]
    [InlineData(RecapPeriodKind.Month, "2026-10-14", "October 2026")]
    [InlineData(RecapPeriodKind.Quarter, "2026-08-14", "Q3 2026")]
    [InlineData(RecapPeriodKind.Semester, "2026-03-14", "the first half of 2026")]
    [InlineData(RecapPeriodKind.Semester, "2026-10-14", "the second half of 2026")]
    [InlineData(RecapPeriodKind.Year, "2026-10-14", "2026")]
    [InlineData(RecapPeriodKind.Week, "2026-10-08", "the week of 5 October 2026")]
    [InlineData(RecapPeriodKind.Day, "2026-10-05", "5 October 2026")]
    public void Period_names_read_in_words(RecapPeriodKind kind, string at, string expected)
    {
        Assert.Equal(expected, CardRules.PeriodName(RecapPeriod.Of(kind, DateOnly.Parse(at))));
    }

    [Theory]
    [InlineData("prod-eu", 120, true)]
    [InlineData("", 120, false)]
    [InlineData(null, 120, false)]
    [InlineData(" prod", 120, false)]
    [InlineData("eu‮", 120, false)]
    [InlineData("a​b", 120, false)]
    [InlineData("a\nb", 120, false)]
    public void Printable_follows_the_engine_text_rule(string? text, int maxChars, bool expected)
    {
        Assert.Equal(expected, CardRules.Printable(text, maxChars));
    }

    [Fact]
    public void Printable_bounds_the_length()
    {
        Assert.True(CardRules.Printable(new string('a', 120), 120));
        Assert.False(CardRules.Printable(new string('a', 121), 120));
    }
}
