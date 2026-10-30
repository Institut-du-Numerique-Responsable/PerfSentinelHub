using System.Globalization;

namespace PerfSentinelHub.Recaps;

/// <summary>One ledger's day as the CO2 estimate reads it: the extra I/O and the day's grams per avoidable I/O.</summary>
public sealed record CarbonDay(
    string SourceId,
    string LedgerId,
    int Day,
    long ExtraIoOps,
    double? AvoidableGco2,
    long? AvoidableIoOps);

public sealed record Co2Estimate(double? Grams, int SourcesWithoutCoefficient, string? MissingReason);

public static class RecapCo2
{
    /// <summary>
    ///     A partial minimum: the extra I/O of each day priced at that day's own coefficient,
    ///     the days without one left out and their sources counted. Never extrapolated.
    /// </summary>
    public static Co2Estimate Estimate(IReadOnlyList<CarbonDay> rows)
    {
        var counted = rows.Where(row => row.ExtraIoOps > 0).ToList();
        if (counted.Count == 0)
            return new Co2Estimate(null, 0, "no_avoidable_io");

        var priced = counted.Where(HasCoefficient).ToList();
        var without = counted.Where(row => !HasCoefficient(row)).Select(row => row.SourceId).Distinct().Count();
        if (priced.Count == 0)
            return new Co2Estimate(null, without, "no_coefficient");

        var grams = priced.Sum(row => row.ExtraIoOps * row.AvoidableGco2!.Value / row.AvoidableIoOps!.Value);
        return new Co2Estimate(Magnitude(grams), without, null);
    }

    /// <summary>One significant figure; zero stays zero.</summary>
    public static double Magnitude(double grams)
    {
        return double.Parse(grams.ToString("E0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static bool HasCoefficient(CarbonDay row)
    {
        return row.AvoidableGco2 is not null && row.AvoidableIoOps > 0;
    }
}
