using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Recaps;

public sealed class RecapCo2Tests
{
    private static CarbonDay Row(string source, long io, double? grams = null, long? ops = null, int day = 20750)
    {
        return new CarbonDay(source, "L", day, io, grams, ops);
    }

    [Theory]
    [InlineData(1234, 1000)]
    [InlineData(0.0456, 0.05)]
    [InlineData(96, 100)]
    [InlineData(0, 0)]
    public void Magnitude_keeps_one_significant_figure(double grams, double expected)
    {
        Assert.Equal(expected, RecapCo2.Magnitude(grams));
    }

    [Fact]
    public void A_source_without_coefficient_makes_a_partial_minimum()
    {
        var estimate = RecapCo2.Estimate([Row("a", 40, 3.0, 120), Row("b", 50)]);

        Assert.Equal(new Co2Estimate(1, 1, null), estimate);
    }

    [Fact]
    public void No_coefficient_anywhere_is_null_with_its_reason()
    {
        var estimate = RecapCo2.Estimate([Row("a", 40), Row("a", 10, day: 20751), Row("b", 5)]);

        Assert.Equal(new Co2Estimate(null, 2, "no_coefficient"), estimate);
    }

    [Fact]
    public void Zero_io_rows_are_ignored()
    {
        var estimate = RecapCo2.Estimate([Row("a", 0, 3.0, 120), Row("b", 0)]);

        Assert.Equal(new Co2Estimate(null, 0, "no_avoidable_io"), estimate);
        Assert.Equal(new Co2Estimate(null, 0, "no_avoidable_io"), RecapCo2.Estimate([]));
    }

    [Fact]
    public void A_zero_ops_day_has_no_coefficient()
    {
        var estimate = RecapCo2.Estimate([Row("a", 40, 0.0, 0)]);

        Assert.Equal(new Co2Estimate(null, 1, "no_coefficient"), estimate);
    }

    [Fact]
    public void Low_grams_price_each_day_at_its_low_coefficient()
    {
        CarbonDay[] rows = [new("a", "L", 20750, 40, null, 120, 1.5), new("b", "L", 20750, 10, null, 120)];

        Assert.Equal(0.5, RecapCo2.LowGrams(rows));
        Assert.Null(RecapCo2.LowGrams([rows[1]]));
        Assert.Null(RecapCo2.LowGrams([new CarbonDay("a", "L", 20750, 0, null, 120, 1.5)]));
    }

    [Fact]
    public void Each_day_is_priced_at_its_own_coefficient()
    {
        var estimate = RecapCo2.Estimate([Row("a", 100, 1.0, 100), Row("a", 100, 3.0, 100, 20751)]);

        Assert.Equal(new Co2Estimate(4, 0, null), estimate);
    }
}
