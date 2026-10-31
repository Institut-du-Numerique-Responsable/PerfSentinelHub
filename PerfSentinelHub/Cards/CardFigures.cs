using System.Globalization;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Cards;

/// <summary>The figures a share card signs, computed once from the same periods they are summed over.</summary>
public static class CardFigures
{
    private const long DayMs = 86_400_000;
    private const int MaxStoryChars = 200;
    private const long MaxEngineInteger = (1L << 53) - 1;

    /// <summary>
    ///     The story of a fix, or null when its cited figure is missing or its service or endpoint
    ///     cannot be printed. A trace dated after the done day (a daemon clock ahead of the Hub's)
    ///     reads as undated, since the engine refuses observed_at after done_at.
    /// </summary>
    public static CardStory? Story(FrozenFinding frozen, long doneAtMs)
    {
        if (Cited(frozen) is null || !Named(frozen))
            return null;

        var observed = frozen.ObservedAtMs is { } ms && ms / DayMs <= doneAtMs / DayMs ? Date(ms) : null;
        return new CardStory(frozen.FindingType, frozen.Service, frozen.Endpoint, observed, Date(doneAtMs));
    }

    /// <summary>A fix that holds its cited figure but whose service or endpoint the card cannot print.</summary>
    public static bool Unprintable(FrozenFinding frozen)
    {
        return Cited(frozen) is not null && !Named(frozen);
    }

    /// <summary>
    ///     A story's figures and the before-fix period they name: the volume's period when the
    ///     volume before is known, else the period of kind holding the cited trace when a threshold
    ///     is given. Pass the threshold only for a story whose ObservedAt Story kept.
    /// </summary>
    public static (CardFiguresBlock Figures, CardDayRange? BeforeFixPeriod) StoryFigures(
        FrozenFinding frozen,
        RecapPeriodKind kind,
        CardBefore? before,
        long? threshold)
    {
        var slow = IsSlow(frozen.FindingType);
        var figures = slow ? new CardFiguresBlock(CitedMs: Cited(frozen)) : new CardFiguresBlock(CitedOps: Cited(frozen));

        if (before is { Ops: > 0 and var ops })
            return (figures with { BeforeFixOps = ops, Co2LowUg = before.Co2LowUg }, Range(before.Period));

        if (threshold is not { } value || frozen.ObservedAtMs is not { } observedMs)
            return (figures, null);

        var period = RecapPeriod.Of(kind, DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(observedMs).UtcDateTime));
        return (slow ? figures with { ThresholdMs = value } : figures with { ThresholdOps = value }, Range(period));
    }

    /// <summary>The threshold the daemon applied on the latest signal day after the done day; null without one.</summary>
    public static long? ComparedThreshold(string findingType, int doneDay, IEnumerable<SignalDay> days)
    {
        if (!CardTypes.ThresholdTypes.Contains(findingType))
            return null;

        var latest = days.Where(day => day.Day > doneDay).MaxBy(day => day.Day);
        return latest is null ? null : CardTypes.ThresholdOf(latest.Thresholds, findingType);
    }

    public static CardDayRange Range(RecapPeriod period)
    {
        return new CardDayRange(Date(period.From), Date(period.To));
    }

    /// <summary>
    ///     One curve point over the rows carrying both counts; null without any, or when the avoidable
    ///     exceeds the total (an out-of-contract export), so a false ratio is never signed.
    /// </summary>
    public static CardPoint? Point(RecapPeriod period, IEnumerable<DayTotals> rows)
    {
        long avoidable = 0, total = 0;
        foreach (var row in rows)
            if (row is { AvoidableIoOps: { } rowAvoidable, TotalIoOps: { } rowTotal })
            {
                avoidable += rowAvoidable;
                total += rowTotal;
            }

        return total == 0 || avoidable > total
            ? null
            : new CardPoint(Date(period.From), Date(period.To), avoidable * 1000 / total, avoidable, total);
    }

    /// <summary>Fixes per family; a type newer than this Hub ("other") counts in none.</summary>
    public static CardFamilies Families(IEnumerable<string> findingTypes)
    {
        var families = findingTypes.Select(CardTypes.Family).ToList();
        return new CardFamilies(
            families.Count(family => family == "extra_calls"),
            families.Count(family => family == "slow_calls"),
            families.Count(family => family == "call_architecture"));
    }

    // Through decimal, which keeps 15 significant digits, so a sum like 19 x 0.3 floors to 5.7 g, not one microgram less.
    // Past the engine's integers, 2^53 - 1, the figure reads as none rather than a refused card.
    public static long? Micrograms(double? grams)
    {
        if (grams is not { } value || !double.IsFinite(value) || value < 0 || value > MaxEngineInteger / 1_000_000d)
            return null;
        var micrograms = (long)decimal.Floor((decimal)value * 1_000_000m);
        return micrograms <= MaxEngineInteger ? micrograms : null;
    }

    // The engine bounds story.service and story.endpoint at 200 characters.
    private static bool Named(FrozenFinding frozen)
    {
        return CardRules.Printable(frozen.Service, MaxStoryChars) && CardRules.Printable(frozen.Endpoint, MaxStoryChars);
    }

    private static long? Cited(FrozenFinding frozen)
    {
        return IsSlow(frozen.FindingType) ? frozen.P99Us / 1000 : frozen.Occurrences;
    }

    private static bool IsSlow(string findingType)
    {
        return findingType.StartsWith("slow_", StringComparison.Ordinal);
    }

    private static string Date(long ms)
    {
        return CardKeyLines.Day(ms);
    }

    private static string Date(DateOnly day)
    {
        return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
