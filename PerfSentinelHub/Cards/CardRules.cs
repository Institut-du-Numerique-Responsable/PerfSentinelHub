using System.Globalization;
using PerfSentinelHub.Api;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Cards;

/// <summary>
///     The rules a share card's period and texts follow, and the plain sentence of each refusal,
///     so the API's problem detail and the screen's options read the same copy.
/// </summary>
public static class CardRules
{
    private const string PreviousPrefix = "previous_";

    /// <summary>Every reason a card is refused, stable: the API and the screen key on them.</summary>
    public static readonly IReadOnlyList<string> Reasons =
    [
        "open_period", "uncovered_days", "open_day", "overflow", "previous_uncovered_days", "previous_open_day",
        "previous_overflow", "no_confirmed_fix", "unknown_fix", "unprintable_fix", "no_total_io",
        "curve_needs_whole_sources", "unprintable_scope", "scope_too_long"
    ];

    /// <summary>The first incomplete reason but restart, which only shows on the card as a day count.</summary>
    public static string? Refusal(RecapPeriodInfo info, string prefix = "")
    {
        return info.Incomplete.Where(reason => reason != "restart").Order(StringComparer.Ordinal)
            .Select(reason => prefix + reason).FirstOrDefault();
    }

    public static int RestartDays(IReadOnlyList<SourceCoverage> sources, RecapPeriod period)
    {
        return sources.SelectMany(source => source.Days)
            .Where(day => day.Gap && day.Day >= period.FromDay && day.Day <= period.ToDay)
            .Select(day => day.Day).Distinct().Count();
    }

    // Through decimal so 0.29 reads 290, not the 289 of its binary sum. No sampled day reads the whole traffic.
    public static int SampledPermille(double? rate)
    {
        return rate is { } value && value < 1 ? Math.Max(0, (int)decimal.Floor((decimal)value * 1000m)) : 1000;
    }

    /// <summary>Where the engine places the card file's signature; refused in every card text.</summary>
    public const string EnvelopeMarker = "{\"content_sig\":\"";

    /// <summary>
    ///     The engine's text rule: trimmed, non-empty, at most maxChars, no control or format character,
    ///     and never the card file's marker.
    /// </summary>
    public static bool Printable(string? text, int maxChars)
    {
        return !string.IsNullOrEmpty(text) && text.Length <= maxChars && text == text.Trim() &&
               !text.Any(LabelWrite.Hidden) && !text.Contains(EnvelopeMarker, StringComparison.Ordinal);
    }

    /// <summary>A card period in words, as the Hub screen names it.</summary>
    public static string PeriodName(RecapPeriod period)
    {
        var year = period.From.Year.ToString(CultureInfo.InvariantCulture);
        return period.Kind switch
        {
            RecapPeriodKind.Week => $"the week of {Day(period.From)}",
            RecapPeriodKind.Month => period.From.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            RecapPeriodKind.Quarter => $"Q{(period.From.Month - 1) / 3 + 1} {year}",
            RecapPeriodKind.Semester => period.From.Month == 1 ? $"the first half of {year}" : $"the second half of {year}",
            RecapPeriodKind.Year => year,
            _ => Day(period.From)
        };
    }

    public static string Explain(string reason, RecapPeriod period)
    {
        if (reason.StartsWith(PreviousPrefix, StringComparison.Ordinal))
            return $"No curve for {PeriodName(period)}. The curve compares with {PeriodName(period.Previous())}, and " +
                   $"{PreviousClause(reason[PreviousPrefix.Length..])} A story or a collective card still works for {PeriodName(period)}.";

        return reason switch
        {
            "open_period" => "A card covers a closed period. Pick an earlier one.",
            "uncovered_days" => "Some days of this period were not recorded, so its figures are not whole.",
            "open_day" => "Some days of this period are still being counted. Try again tomorrow.",
            "overflow" =>
                "On some days of this period there were too many different problems to count each one, so its figures are not whole.",
            "no_confirmed_fix" => "No fix of this period is confirmed by the measure yet.",
            "unknown_fix" => "This fix is not among the confirmed fixes of the period. Pick one from the list.",
            "unprintable_fix" =>
                "This fix's service or endpoint name is too long or holds a hidden character, so it cannot be printed on a card.",
            "no_total_io" =>
                "The curve needs the total activity of each day, which this period or the one before does not hold yet.",
            "curve_needs_whole_sources" => "The curve covers whole sources, and you read only part of one.",
            "unprintable_scope" =>
                "A source or environment name is too long or holds a hidden character, so it cannot be printed on a card. Ask an administrator to rename it.",
            "scope_too_long" =>
                "The environment and source names of this card are too long together to print on it. Pick an environment or a source.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown card refusal.")
        };
    }

    private static string PreviousClause(string reason)
    {
        return reason switch
        {
            "uncovered_days" => "some of its days were not recorded.",
            "open_day" => "some of its days are still being counted.",
            "overflow" => "on some of its days there were too many different problems to count each one.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown card refusal.")
        };
    }

    private static string Day(DateOnly day)
    {
        return day.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
    }
}
