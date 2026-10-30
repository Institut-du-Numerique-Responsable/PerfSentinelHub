namespace PerfSentinelHub.Recaps;

public enum RecapPeriodKind
{
    Day,
    Week,
    Month,
    Quarter,
    Semester,
    Year
}

/// <summary>
///     One UTC calendar period, both ends included: a day, an ISO week (Monday to Sunday),
///     a calendar month, quarter, semester or year. The recap reads the first three; share cards
///     read week to year. Days are epoch days, as the detection ledger counts them.
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
        RecapPeriodKind.Month => "month",
        RecapPeriodKind.Quarter => "quarter",
        RecapPeriodKind.Semester => "semester",
        RecapPeriodKind.Year => "year",
        _ => throw new InvalidOperationException($"Unknown period kind {Kind}.")
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
            case RecapPeriodKind.Month:
                return Months(kind, new DateOnly(at.Year, at.Month, 1), 1);
            case RecapPeriodKind.Quarter:
                return Months(kind, new DateOnly(at.Year, (at.Month - 1) / 3 * 3 + 1, 1), 3);
            case RecapPeriodKind.Semester:
                return Months(kind, new DateOnly(at.Year, at.Month <= 6 ? 1 : 7, 1), 6);
            case RecapPeriodKind.Year:
                return Months(kind, new DateOnly(at.Year, 1, 1), 12);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown period kind.");
        }
    }

    private static RecapPeriod Months(RecapPeriodKind kind, DateOnly first, int months)
    {
        return new RecapPeriod(kind, first, first.AddMonths(months).AddDays(-1));
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

    /// <summary>The five kinds a share card accepts: week, month, quarter, semester, year.</summary>
    public static bool TryParseCardKind(string value, out RecapPeriodKind kind)
    {
        switch (value)
        {
            case "week":
                kind = RecapPeriodKind.Week;
                return true;
            case "month":
                kind = RecapPeriodKind.Month;
                return true;
            case "quarter":
                kind = RecapPeriodKind.Quarter;
                return true;
            case "semester":
                kind = RecapPeriodKind.Semester;
                return true;
            case "year":
                kind = RecapPeriodKind.Year;
                return true;
            default:
                kind = default;
                return false;
        }
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
