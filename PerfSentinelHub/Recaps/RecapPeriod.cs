namespace PerfSentinelHub.Recaps;

public enum RecapPeriodKind
{
    Day,
    Week,
    Month
}

/// <summary>
///     One UTC calendar period, both ends included: a day, an ISO week (Monday to Sunday)
///     or a calendar month. Days are epoch days, as the detection ledger counts them.
/// </summary>
public readonly record struct RecapPeriod(RecapPeriodKind Kind, DateOnly From, DateOnly To)
{
    private const long DayMs = 86_400_000;
    private static readonly int EpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    public int FromDay => From.DayNumber - EpochDayNumber;

    public int ToDay => To.DayNumber - EpochDayNumber;

    public long FromMs => FromDay * DayMs;

    public long ToMs => (ToDay + 1L) * DayMs - 1;

    public string KindName => Kind switch
    {
        RecapPeriodKind.Day => "day",
        RecapPeriodKind.Week => "week",
        _ => "month"
    };

    public static RecapPeriod Of(RecapPeriodKind kind, DateOnly at)
    {
        switch (kind)
        {
            case RecapPeriodKind.Day:
                return new RecapPeriod(kind, at, at);
            case RecapPeriodKind.Week:
                var monday = at.AddDays(-(((int)at.DayOfWeek + 6) % 7));
                return new RecapPeriod(kind, monday, monday.AddDays(6));
            default:
                var first = new DateOnly(at.Year, at.Month, 1);
                return new RecapPeriod(kind, first, first.AddMonths(1).AddDays(-1));
        }
    }

    /// <summary>The period of the same kind just before this one.</summary>
    public RecapPeriod Previous()
    {
        return Of(Kind, From.AddDays(-1));
    }

    /// <summary>The first days of this period, never past its end; days is at least 1.</summary>
    public RecapPeriod Truncated(int days)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        var end = From.AddDays(days - 1);
        return this with { To = end < To ? end : To };
    }

    public static bool TryParseKind(string value, out RecapPeriodKind kind)
    {
        switch (value)
        {
            case "day":
                kind = RecapPeriodKind.Day;
                return true;
            case "week":
                kind = RecapPeriodKind.Week;
                return true;
            case "month":
                kind = RecapPeriodKind.Month;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
