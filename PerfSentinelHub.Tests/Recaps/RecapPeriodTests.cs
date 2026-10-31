using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Recaps;

public sealed class RecapPeriodTests
{
    [Fact]
    public void A_week_runs_monday_to_sunday()
    {
        var week = RecapPeriod.Of(RecapPeriodKind.Week, new DateOnly(2026, 10, 29));

        Assert.Equal(new DateOnly(2026, 10, 26), week.From);
        Assert.Equal(new DateOnly(2026, 11, 1), week.To);
        Assert.Equal(20752, week.FromDay);
        Assert.Equal(20758, week.ToDay);
        Assert.Equal(1_792_972_800_000, week.FromMs);
        Assert.Equal(1_792_972_800_000 + 7 * 86_400_000L - 1, week.ToMs);
        Assert.Equal("week", week.KindName);
    }

    [Fact]
    public void A_week_crosses_the_year()
    {
        var week = RecapPeriod.Of(RecapPeriodKind.Week, new DateOnly(2027, 1, 1));

        Assert.Equal(new DateOnly(2026, 12, 28), week.From);
        Assert.Equal(new DateOnly(2027, 1, 3), week.To);
    }

    [Fact]
    public void The_previous_week_is_the_one_before()
    {
        var previous = RecapPeriod.Of(RecapPeriodKind.Week, new DateOnly(2026, 10, 29)).Previous();

        Assert.Equal(new DateOnly(2026, 10, 19), previous.From);
        Assert.Equal(new DateOnly(2026, 10, 25), previous.To);
    }

    [Fact]
    public void A_month_is_the_calendar_month_and_its_previous_the_month_before()
    {
        var month = RecapPeriod.Of(RecapPeriodKind.Month, new DateOnly(2026, 10, 29));
        var previous = month.Previous();

        Assert.Equal(new DateOnly(2026, 10, 1), month.From);
        Assert.Equal(new DateOnly(2026, 10, 31), month.To);
        Assert.Equal(new DateOnly(2026, 9, 1), previous.From);
        Assert.Equal(new DateOnly(2026, 9, 30), previous.To);
        Assert.Equal("month", month.KindName);
    }

    [Fact]
    public void A_day_is_one_utc_day()
    {
        var day = RecapPeriod.Of(RecapPeriodKind.Day, new DateOnly(2026, 10, 29));

        Assert.Equal(day.From, day.To);
        Assert.Equal(20755, day.FromDay);
        Assert.Equal(20755, day.ToDay);
        Assert.Equal(20755 * 86_400_000L, day.FromMs);
        Assert.Equal(20756 * 86_400_000L - 1, day.ToMs);
        Assert.Equal(new DateOnly(2026, 10, 28), day.Previous().From);
        Assert.Equal("day", day.KindName);
    }

    [Fact]
    public void Truncated_keeps_the_first_days()
    {
        var week = RecapPeriod.Of(RecapPeriodKind.Week, new DateOnly(2026, 10, 29));
        var truncated = week.Previous().Truncated(4);

        Assert.Equal(new DateOnly(2026, 10, 19), truncated.From);
        Assert.Equal(new DateOnly(2026, 10, 22), truncated.To);
        Assert.Equal(week.Previous(), week.Previous().Truncated(30));
        Assert.Throws<ArgumentOutOfRangeException>(() => week.Truncated(0));
    }

    [Theory]
    [InlineData("day", true)]
    [InlineData("week", true)]
    [InlineData("month", true)]
    [InlineData("quarter", false)]
    [InlineData("year", false)]
    [InlineData("Week", false)]
    [InlineData("", false)]
    public void Kind_parses_three_words_only(string value, bool parsed)
    {
        Assert.Equal(parsed, RecapPeriod.TryParseKind(value, out _));
    }

    [Fact]
    public void A_quarter_is_three_calendar_months()
    {
        var quarter = RecapPeriod.Of(RecapPeriodKind.Quarter, new DateOnly(2026, 10, 29));
        var previous = quarter.Previous();

        Assert.Equal(new DateOnly(2026, 10, 1), quarter.From);
        Assert.Equal(new DateOnly(2026, 12, 31), quarter.To);
        Assert.Equal("quarter", quarter.KindName);
        Assert.Equal(new DateOnly(2026, 7, 1), previous.From);
        Assert.Equal(new DateOnly(2026, 9, 30), previous.To);
    }

    [Fact]
    public void The_quarter_before_the_first_is_the_last_of_the_year_before()
    {
        var previous = RecapPeriod.Of(RecapPeriodKind.Quarter, new DateOnly(2026, 2, 14)).Previous();

        Assert.Equal(new DateOnly(2025, 10, 1), previous.From);
        Assert.Equal(new DateOnly(2025, 12, 31), previous.To);
    }

    [Fact]
    public void A_semester_is_half_a_year()
    {
        var semester = RecapPeriod.Of(RecapPeriodKind.Semester, new DateOnly(2026, 10, 29));
        var previous = semester.Previous();

        Assert.Equal(new DateOnly(2026, 7, 1), semester.From);
        Assert.Equal(new DateOnly(2026, 12, 31), semester.To);
        Assert.Equal("semester", semester.KindName);
        Assert.Equal(new DateOnly(2026, 1, 1), previous.From);
        Assert.Equal(new DateOnly(2026, 6, 30), previous.To);
    }

    [Fact]
    public void A_year_is_the_calendar_year()
    {
        var year = RecapPeriod.Of(RecapPeriodKind.Year, new DateOnly(2026, 10, 29));
        var previous = year.Previous();

        Assert.Equal(new DateOnly(2026, 1, 1), year.From);
        Assert.Equal(new DateOnly(2026, 12, 31), year.To);
        Assert.Equal("year", year.KindName);
        Assert.Equal(new DateOnly(2025, 1, 1), previous.From);
        Assert.Equal(new DateOnly(2025, 12, 31), previous.To);
        Assert.Equal(364, year.ToDay - year.FromDay);
    }

    [Theory]
    [InlineData("week", true)]
    [InlineData("month", true)]
    [InlineData("quarter", true)]
    [InlineData("semester", true)]
    [InlineData("year", true)]
    [InlineData("day", false)]
    [InlineData("Year", false)]
    [InlineData("", false)]
    public void Card_kinds_parse_five_words(string value, bool parsed)
    {
        Assert.Equal(parsed, RecapPeriod.TryParseCardKind(value, out _));
    }

    [Fact]
    public void The_detection_retention_covers_a_year_card_and_the_year_before()
    {
        var today = new DateOnly(2026, 12, 31);
        var lastClosedYear = RecapPeriod.Of(RecapPeriodKind.Year, today).Previous();
        var yearBefore = lastClosedYear.Previous();

        Assert.Equal(new DateOnly(2025, 1, 1), lastClosedYear.From);
        Assert.Equal(new DateOnly(2024, 1, 1), yearBefore.From);
        var span = today.DayNumber - yearBefore.From.DayNumber;
        Assert.Equal(1095, span);
        Assert.True(TimeSpan.FromDays(span) <= new HubOptions().DetectionRetention);
    }
}
