using System.Globalization;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Recaps;

/// <summary>Pure rules of the scheduled reports: when a closed period is due and which sources a scope names.</summary>
public static class RecapSchedule
{
    /// <summary>A DeliverAt value: HH:mm, 00:00 to 23:59.</summary>
    public static bool TryParseDeliverAt(string? value, out TimeOnly time)
    {
        return TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    /// <summary>A TimeZone value, an IANA id the system knows; null reads as UTC.</summary>
    public static bool TryFindTimeZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (id is null)
            return true;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>When a subscription's report generated at generatedAtMs may leave: at once without DeliverAt.</summary>
    public static long DeliveryDueMs(long generatedAtMs, RecapSubscriptionOptions subscription)
    {
        return TryParseDeliverAt(subscription.DeliverAt, out var deliverAt) &&
               TryFindTimeZone(subscription.TimeZone, out var zone)
            ? DeliveryDueMs(generatedAtMs, deliverAt, zone)
            : generatedAtMs;
    }

    /// <summary>
    ///     The first deliverAt local time in zone at or after generatedAtMs. A report frozen past that
    ///     time waits for the next day's, so a recipient always reads it at the hour they expect.
    /// </summary>
    public static long DeliveryDueMs(long generatedAtMs, TimeOnly deliverAt, TimeZoneInfo zone)
    {
        var generated = DateTimeOffset.FromUnixTimeMilliseconds(generatedAtMs);
        var localDay = TimeZoneInfo.ConvertTime(generated, zone).Date;
        for (var day = 0; ; day++)
        {
            var local = localDay.AddDays(day) + deliverAt.ToTimeSpan();
            // shortcut: a time a DST change skips moves an hour on, the gap of every zone with DST
            // today; an exotic half-hour gap would land 30 minutes late.
            if (zone.IsInvalidTime(local))
                local = local.AddHours(1);
            // A time the clocks go back over happens twice: the first one, the larger offset, comes first.
            TimeSpan[] offsets = zone.IsAmbiguousTime(local)
                ? [.. zone.GetAmbiguousTimeOffsets(local).OrderDescending()]
                : [zone.GetUtcOffset(local)];
            foreach (var offset in offsets)
            {
                var candidate = new DateTimeOffset(local, offset);
                if (candidate >= generated)
                    return candidate.ToUnixTimeMilliseconds();
            }
        }
    }

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
