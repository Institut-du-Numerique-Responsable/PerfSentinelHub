using System.Globalization;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Recaps;

/// <summary>One source's ledgers as a recap reads them: every detection_days row and every ledger's life.</summary>
public sealed record SourceCoverage(
    string SourceId,
    IReadOnlyList<DayCoverage> Days,
    IReadOnlyList<LedgerLife> Ledgers);

public static class RecapCoverage
{
    /// <summary>Reasons that forbid an exact comparison of the volumes.</summary>
    public static readonly IReadOnlySet<string> IncompleteReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "uncovered_days", "open_day", "restart", "overflow"
    };

    /// <summary>Reasons worth a line but that leave the comparison standing.</summary>
    public static readonly IReadOnlySet<string> NoteReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "sampling", "shed", "ingest_rejected", "multi_instance"
    };

    /// <summary>
    ///     Judges how well the ledgers cover the period. truncatedToDays null folds the closed
    ///     days of the period (today is left to InProgress); n >= 1 folds its first n days;
    ///     0, like the first day of a period, has nothing closed to fold and judges nothing.
    /// </summary>
    public static RecapPeriodInfo Judge(
        RecapPeriod period,
        int today,
        IReadOnlyList<SourceCoverage> sources,
        int? truncatedToDays)
    {
        var last = truncatedToDays is { } days ? Math.Min(period.ToDay, period.FromDay + days - 1) : period.ToDay;
        var window = new VolumeWindow(period.FromDay, Math.Min(last, today - 1));
        var gaps = new SortedSet<string>(StringComparer.Ordinal);
        double? samplingRate = null;
        if (window.FromDay <= window.ToDay)
            foreach (var source in sources)
            {
                var volume = DetectionVolumeFold.ForSource(
                    source.Days, [], window, today, DetectionVolumeFold.MultiInstance(source.Ledgers, window));
                gaps.UnionWith(volume.Gaps);
                if (volume.SamplingRate is { } rate)
                    samplingRate = Math.Min(samplingRate ?? rate, rate);
            }

        return new RecapPeriodInfo(
            period.KindName,
            period.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            period.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            period.ToDay >= today,
            [.. gaps.Where(IncompleteReasons.Contains)],
            [.. gaps.Where(NoteReasons.Contains)],
            samplingRate,
            truncatedToDays);
    }
}
