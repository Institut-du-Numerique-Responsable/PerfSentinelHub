using System.Collections.Concurrent;

namespace PerfSentinelHub.Collection;

/// <summary>How one poll pass over a daemon's detection ledger ended.</summary>
public enum DetectionReadOutcome
{
    /// <summary>Every page read and the pass sealed.</summary>
    Ok,

    /// <summary>The ledger changed or went away mid-pass: what was read stays, unsealed.</summary>
    Aborted,

    /// <summary>The daemon refused the Hub's key on /api/detections.</summary>
    Unauthorized,

    /// <summary>A page failed to arrive, parse or store.</summary>
    Failed,

    /// <summary>The pass reached the page cap before the ledger's end, and sealed nothing.</summary>
    Capped
}

/// <summary>
///     What the detection ledger polls came to, per daemon, since the Hub started. In
///     process like RingWatch: the counters reset on restart, which a Prometheus counter
///     may do.
/// </summary>
public sealed class DetectionWatch
{
    private readonly ConcurrentDictionary<(string SourceId, DetectionReadOutcome Outcome), long> _reads = new();
    private readonly ConcurrentDictionary<string, long> _rows = new(StringComparer.Ordinal);

    public static IReadOnlyList<DetectionReadOutcome> Outcomes { get; } = Enum.GetValues<DetectionReadOutcome>();

    /// <summary>Files one pass and the count rows it merged, an unfinished pass's included.</summary>
    public void Record(string sourceId, DetectionReadOutcome outcome, long rows)
    {
        _reads.AddOrUpdate((sourceId, outcome), 1, (_, sum) => sum + 1);
        _rows.AddOrUpdate(sourceId, rows, (_, sum) => sum + rows);
    }

    public long Reads(string sourceId, DetectionReadOutcome outcome)
    {
        return _reads.GetValueOrDefault((sourceId, outcome));
    }

    public long Rows(string sourceId)
    {
        return _rows.GetValueOrDefault(sourceId);
    }

    public static string Label(DetectionReadOutcome outcome)
    {
        return outcome switch
        {
            DetectionReadOutcome.Ok => "ok",
            DetectionReadOutcome.Aborted => "aborted",
            DetectionReadOutcome.Unauthorized => "unauthorized",
            DetectionReadOutcome.Failed => "failed",
            DetectionReadOutcome.Capped => "capped",
            // No catch-all, as for the import refusals: an outcome without a label fails loudly.
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };
    }
}
