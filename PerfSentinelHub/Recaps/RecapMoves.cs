namespace PerfSentinelHub.Recaps;

public static class RecapMoves
{
    private const int NothingMovedRanks = 10;

    /// <summary>
    ///     How a finding moved between two equal-length windows. The I/O volume is the metric
    ///     when either side has it, the detections otherwise. A side the data cannot compare
    ///     reads "unknown", never a drop.
    /// </summary>
    public static (string Trend, double? Change) Trend(RecapFinding current, RecapFinding? previous, double threshold)
    {
        if (previous is null) return ("new", null);

        var io = current.AvoidableIoOps > 0 || previous.AvoidableIoOps > 0;
        var now = io ? current.AvoidableIoOps : current.Detections;
        var before = io ? previous.AvoidableIoOps : previous.Detections;

        if (now is null && before is null) return ("unknown", null);
        if (now > 0 && (before is null || before == 0)) return ("up", null);
        if (now is not { } cur || before is not { } prev) return ("unknown", null);
        if (prev == 0) return ("flat", 0);

        var change = (double)(cur - prev) / prev;
        return (change >= threshold ? "up" : change <= -threshold ? "down" : "flat", change);
    }

    /// <summary>
    ///     True when no finding appeared, left, came back or was done-but-detected, and none of
    ///     the top ten went up or down. A gone finding whose fix was confirmed is in Gone already.
    /// </summary>
    public static bool NothingMoved(RecapMovements moves, IReadOnlyList<RecapPriority> ranked)
    {
        return moves.New.Count == 0 &&
               moves.Gone.Count == 0 &&
               moves.CameBack.Count == 0 &&
               moves.DoneStillDetected.Count == 0 &&
               !ranked.Take(NothingMovedRanks).Any(priority => priority.Trend is "up" or "down");
    }
}
