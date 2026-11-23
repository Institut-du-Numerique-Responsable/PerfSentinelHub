using System.Globalization;

namespace PerfSentinelHub.Recaps;

/// <summary>
///     One ledger's day as the CO2 estimate reads it: the extra I/O and the day's grams per avoidable I/O,
///     at the central tier and, for a share card, at the low one.
/// </summary>
public sealed record CarbonDay(
    string SourceId,
    string LedgerId,
    int Day,
    long ExtraIoOps,
    double? AvoidableGco2,
    long? AvoidableIoOps,
    double? AvoidableGco2Low = null);

/// <summary>The estimate, with the sources and the distinct days whose extra I/O it could not price.</summary>
public sealed record Co2Estimate(
    double? Grams, int SourcesWithoutCoefficient, string? MissingReason, int DaysWithoutCoefficient = 0);

public static class RecapCo2
{
    /// <summary>
    ///     A partial minimum: the extra I/O of each day priced at that day's own coefficient,
    ///     the days without one left out and counted, by source and by day. Never extrapolated.
    /// </summary>
    public static Co2Estimate Estimate(IReadOnlyList<CarbonDay> rows)
    {
        var counted = rows.Where(row => row.ExtraIoOps > 0).ToList();
        if (counted.Count == 0)
            return new Co2Estimate(null, 0, "no_avoidable_io");

        var priced = counted.Where(HasCoefficient).ToList();
        var unpriced = counted.Where(row => !HasCoefficient(row)).ToList();
        var without = unpriced.Select(row => row.SourceId).Distinct().Count();
        var days = unpriced.Select(row => row.Day).Distinct().Count();
        if (priced.Count == 0)
            return new Co2Estimate(null, without, "no_coefficient", days);

        var grams = priced.Sum(row => row.ExtraIoOps * row.AvoidableGco2!.Value / row.AvoidableIoOps!.Value);
        return new Co2Estimate(Magnitude(grams), without, null, days);
    }

    /// <summary>
    ///     A share card's partial minimum: the extra I/O of each day priced at its low-tier coefficient,
    ///     unrounded, since the card floors it to micrograms. Null when no day is priced.
    /// </summary>
    public static double? LowGrams(IReadOnlyList<CarbonDay> rows)
    {
        var priced = rows.Where(row => row is { ExtraIoOps: > 0, AvoidableGco2Low: not null, AvoidableIoOps: > 0 })
            .ToList();
        return priced.Count == 0
            ? null
            : priced.Sum(row => row.ExtraIoOps * row.AvoidableGco2Low!.Value / row.AvoidableIoOps!.Value);
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
