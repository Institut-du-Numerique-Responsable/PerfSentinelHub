namespace PerfSentinelHub.Storage;

/// <summary>What one ledger's row of detection_days says about its day, as the fold reads it.</summary>
public sealed record DayCoverage(
    string LedgerId,
    int Day,
    bool Sealed,
    bool Gap,
    long OverflowDetections,
    long ShedTraces,
    long IngestRejected,
    double SamplingRate);

/// <summary>One ledger's life as the Hub read it: the daemon's start up to the Hub's last read.</summary>
public sealed record LedgerLife(long StartedMs, long LastReadMs, bool Polled);

/// <summary>
///     Folds the stored ledgers into the volume a windowed read serves. Nothing is
///     extrapolated: an incomplete count is the sum of the known parts with its reasons.
/// </summary>
public static class DetectionVolumeFold
{
    private const long DayMs = 86_400_000;

    /// <summary>
    ///     One source's volume for one signature. days are its detection_days rows over every
    ///     ledger, counts that signature's rows with their ledger. Null when no window day is
    ///     covered.
    /// </summary>
    public static DetectionVolume ForSource(
        IReadOnlyList<DayCoverage> days,
        IReadOnlyList<(string LedgerId, DetectionCountRow Row)> counts,
        VolumeWindow window,
        int today,
        bool multiInstance)
    {
        var inWindow = days.Where(day => day.Day >= window.FromDay && day.Day <= window.ToDay).ToList();
        var covered = inWindow.Select(day => day.Day).ToHashSet();
        var gaps = new SortedSet<string>(StringComparer.Ordinal);
        if (covered.Count < window.ToDay - window.FromDay + 1)
            gaps.Add("uncovered_days");
        if (covered.Count == 0)
            return new DetectionVolume(null, null, null, [.. gaps], null);

        // A count is trusted only on a day its own ledger covers: another ledger's day says
        // nothing of what this one missed. Covered by another, its own day is still to come.
        var ledgerDays = inWindow.Select(day => (day.LedgerId, day.Day)).ToHashSet();
        var counted = counts.Where(count => ledgerDays.Contains((count.LedgerId, count.Row.Day))).ToList();
        var countedDays = counted.Select(count => (count.LedgerId, count.Row.Day)).ToHashSet();
        var awaited = counts.Any(count =>
            covered.Contains(count.Row.Day) && !ledgerDays.Contains((count.LedgerId, count.Row.Day)));
        if (window.ToDay >= today || inWindow.Exists(day => !day.Sealed) || awaited)
            gaps.Add("open_day");
        if (inWindow.Exists(day => day.Gap))
            gaps.Add("restart");
        if (inWindow.Exists(day => day.SamplingRate < 1))
            gaps.Add("sampling");
        if (inWindow.Exists(day => day.ShedTraces > 0))
            gaps.Add("shed");
        if (inWindow.Exists(day => day.IngestRejected > 0))
            gaps.Add("ingest_rejected");
        // A signature with a row in that ledger that day was counted, its overflow only hid the others.
        if (inWindow.Exists(day => day.OverflowDetections > 0 && !countedDays.Contains((day.LedgerId, day.Day))))
            gaps.Add("overflow");
        if (multiInstance)
            gaps.Add("multi_instance");

        return new DetectionVolume(
            counted.Sum(count => count.Row.Detections),
            gaps.Count == 0,
            counted.Exists(count => count.Row.ExtraIoOps is null)
                ? null
                : counted.Sum(count => count.Row.ExtraIoOps!.Value),
            [.. gaps],
            inWindow.Min(day => day.SamplingRate));
    }

    /// <summary>
    ///     A finding's volume over its sources: the known sources summed, null only when none
    ///     is known, complete only when every source is known and complete.
    /// </summary>
    public static DetectionVolume ForFinding(IReadOnlyList<DetectionVolume> sources)
    {
        var gaps = sources.SelectMany(source => source.Gaps).Distinct().Order(StringComparer.Ordinal).ToList();
        var known = sources.Where(source => source.Detections is not null).ToList();
        if (known.Count == 0)
            return new DetectionVolume(null, null, null, gaps, null);

        var io = known.Where(source => source.AvoidableIoOps is not null).ToList();
        return new DetectionVolume(
            known.Sum(source => source.Detections!.Value),
            known.Count == sources.Count && known.TrueForAll(source => source.Complete == true),
            io.Count == 0 ? null : io.Sum(source => source.AvoidableIoOps!.Value),
            gaps,
            known.Min(source => source.SamplingRate));
    }

    /// <summary>
    ///     Two polled ledgers of one source alive at once on a window day: several pods behind
    ///     one address, which a poll cannot all read. Pushed ledgers add up without it.
    /// </summary>
    public static bool MultiInstance(IReadOnlyList<LedgerLife> ledgers, VolumeWindow window)
    {
        var polled = ledgers.Where(ledger => ledger.Polled).ToList();
        var windowStartMs = window.FromDay * DayMs;
        var windowEndMs = (window.ToDay + 1L) * DayMs;
        for (var i = 0; i < polled.Count; i++)
            for (var j = i + 1; j < polled.Count; j++)
            {
                var start = Math.Max(Math.Max(polled[i].StartedMs, polled[j].StartedMs), windowStartMs);
                var end = Math.Min(Math.Min(polled[i].LastReadMs, polled[j].LastReadMs), windowEndMs - 1);
                if (start <= end)
                    return true;
            }

        return false;
    }
}
