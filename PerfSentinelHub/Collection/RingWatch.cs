using System.Collections.Concurrent;
using PerfSentinelHub.Analysis;

namespace PerfSentinelHub.Collection;

/// <summary>
///     How many findings left a polled daemon's ring before the Hub read them.
///     The daemon counts every finding it puts in its ring
///     (`findings_stored_total`, perf-sentinel 0.31.2 and later). Between two
///     polls of one process, the ones beyond the ring's current length were
///     evicted before the second poll could read them. A daemon that pushes to
///     the Hub delivered them that way, so its stretch moves the baseline and
///     counts nothing.
/// </summary>
public sealed class RingWatch
{
    private readonly ConcurrentDictionary<string, Reading> _last = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _missed = new(StringComparer.Ordinal);

    /// <summary>
    ///     The findings missed since the previous poll of this process. Null when
    ///     nothing can be said: a first reading, a restart, or a daemon without
    ///     the counter.
    /// </summary>
    public long? Observe(string sourceId, DaemonStatus status, bool delivered)
    {
        if (status.FindingsStoredTotal is not { } total ||
            status.UptimeSeconds is not { } uptime ||
            status.StoredFindings is not { } stored)
        {
            _last.TryRemove(sourceId, out _);
            return null;
        }

        var previous = _last.GetValueOrDefault(sourceId);
        _last[sourceId] = new Reading(uptime, total);
        // A restart resets both figures, which would otherwise read as a
        // negative count or, past an underflow, as millions missed.
        if (previous is null || uptime < previous.Uptime || total < previous.Total)
            return null;
        if (delivered)
            return 0;

        var missed = Math.Max(0, total - previous.Total - stored);
        if (missed > 0)
            _missed.AddOrUpdate(sourceId, missed, (_, sum) => sum + missed);
        return missed;
    }

    /// <summary>Findings this source lost to its ring since the Hub started.</summary>
    public long Missed(string sourceId)
    {
        return _missed.GetValueOrDefault(sourceId);
    }

    /// <summary>The Hub's own hint, drawn beside the daemon's.</summary>
    public static ResultWarning Hint(long missed)
    {
        return new ResultWarning(
            "tuning",
            $"The Hub missed {missed} findings from this daemon since it started: they left the "
            + "daemon's ring between two polls before the Hub read them. Raise [daemon] "
            + "max_retained_findings, shorten Hub:PollInterval, or turn on [daemon.hub_export].");
    }

    private sealed record Reading(long Uptime, long Total);
}
