using System.Collections.Concurrent;
using PerfSentinelHub.Analysis;

namespace PerfSentinelHub.Collection;

/// <summary>
///     How many findings left a polled daemon's ring before the Hub read them.
///     The daemon counts every finding it puts in its ring
///     (`findings_stored_total`, perf-sentinel 0.31.2 and later). Between two
///     polls of one process, the ones beyond the ring's current length were
///     evicted before the second poll could read them. Measure says how many;
///     the poller records them only once the daemon is known not to push them to
///     the Hub, which would have delivered them that way.
/// </summary>
public sealed class RingWatch
{
    // Two readings whose estimated process start differ by more than this are
    // two processes. The poller stamps a reading when the status answers, so
    // this covers the second-truncated uptime and the answer's own transfer.
    private const long SameProcessToleranceMs = 5_000;

    private readonly ConcurrentDictionary<string, Reading> _last = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _missed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _missedSinceStart = new(StringComparer.Ordinal);

    /// <summary>
    ///     The findings that left the ring unread since the previous poll of this
    ///     process. Null when nothing can be said: a first reading, a restart, or a
    ///     daemon without the counter. Counts nothing on its own: see Record.
    /// </summary>
    public long? Measure(string sourceId, DaemonStatus status, long answeredAtMs)
    {
        if (status.FindingsStoredTotal is not { } total ||
            status.UptimeSeconds is not { } uptime ||
            status.StoredFindings is not { } stored)
        {
            _last.TryRemove(sourceId, out _);
            return null;
        }

        var reading = new Reading(answeredAtMs - uptime * 1000, total);
        var previous = _last.GetValueOrDefault(sourceId);
        _last[sourceId] = reading;
        if (previous is null)
            return null;
        // A restart resets the counter, and its uptime can climb past the previous
        // reading's before the next poll, so the estimated start is what tells.
        // A restart is the exception, so it stays the guard.
        // ReSharper disable once InvertIf
        if (Math.Abs(reading.StartedAtMs - previous.StartedAtMs) > SameProcessToleranceMs ||
            total < previous.Total)
        {
            _missedSinceStart.TryRemove(sourceId, out _);
            return null;
        }

        return Math.Max(0, total - previous.Total - stored);
    }

    public void Record(string sourceId, long missed)
    {
        if (missed <= 0)
            return;
        _missed.AddOrUpdate(sourceId, missed, (_, sum) => sum + missed);
        _missedSinceStart.AddOrUpdate(sourceId, missed, (_, sum) => sum + missed);
    }

    /// <summary>Findings this source lost to its ring since the Hub started, for the counter.</summary>
    public long Missed(string sourceId)
    {
        return _missed.GetValueOrDefault(sourceId);
    }

    /// <summary>The same since the daemon last started, so a fix and a restart clear the hint.</summary>
    public long MissedSinceStart(string sourceId)
    {
        return _missedSinceStart.GetValueOrDefault(sourceId);
    }

    /// <summary>The Hub's own hint, drawn beside the daemon's.</summary>
    public static ResultWarning Hint(long missed)
    {
        return new ResultWarning(
            "tuning",
            $"The Hub missed {missed} findings from this daemon since the daemon last started: they "
            + "left its ring between two polls before the Hub read them. Raise [daemon] "
            + "max_retained_findings, shorten Hub:PollInterval, or turn on [daemon.hub_export].");
    }

    private sealed record Reading(long StartedAtMs, long Total);
}
