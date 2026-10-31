using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Recaps;

/// <summary>Pure rules of the scheduled reports: when a closed period is due and which sources a scope names.</summary>
public static class RecapSchedule
{
    /// <summary>
    ///     False while the period runs. Once it has ended, true when every source that read a ledger
    ///     during it holds a sealed row for its last day, or when the max delay has passed. A source
    ///     whose ledgers were all last read before the period is retired and holds nothing back.
    /// </summary>
    public static bool IsDue(
        RecapPeriod period,
        IReadOnlyList<SourceCoverage> coverage,
        long nowMs,
        TimeSpan maxDelay)
    {
        if (nowMs <= period.ToMs)
            return false;
        if (nowMs >= period.ToMs + 1 + (long)maxDelay.TotalMilliseconds)
            return true;
        return RecapCoverage.SplitLate(coverage, period).Started
            .Where(source => source.Ledgers.Any(ledger => ledger.LastReadMs >= period.FromMs))
            .All(source => source.Days.Any(day => day.Sealed && day.Day == period.ToDay));
    }

    /// <summary>
    ///     The source scope the findings, the incidents and the recap reads share, null for the whole
    ///     fleet. A source id or an environment is configuration, so an unknown one is refused rather
    ///     than read as an empty page: a typo would otherwise read as "nothing there". Given
    ///     together they intersect, and a source outside the named environment leaves the empty set,
    ///     since a screen offers the two as filters that both apply. With access on, the sources are
    ///     the caller's own list, so one outside it reads as unknown.
    /// </summary>
    public static bool TryScope(
        string? environment,
        string? sourceId,
        IReadOnlyList<SourceOptions> sources,
        out IReadOnlyList<string>? sourceIds)
    {
        sourceIds = null;
        if (sourceId is not null &&
            !sources.Any(source => string.Equals(source.Id, sourceId, StringComparison.Ordinal)))
            return false;

        if (environment is not null)
        {
            sourceIds =
            [
                .. sources
                    .Where(source => string.Equals(source.Environment, environment, StringComparison.Ordinal))
                    .Select(source => source.Id)
            ];
            if (sourceIds.Count == 0)
                return false;
        }

        if (sourceId is not null)
            sourceIds = sourceIds is null || sourceIds.Contains(sourceId) ? [sourceId] : [];
        return true;
    }
}
