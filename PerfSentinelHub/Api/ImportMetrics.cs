namespace PerfSentinelHub.Api;

/// <summary>
///     Counts refused imports, in process. Deliberately not in SQLite: a refusal
///     happens on the paths that could not take the write lock, so recording it
///     would need the very lock that failed. The count resets on restart, which is
///     what a Prometheus counter is allowed to do.
/// </summary>
public class ImportMetrics
{
    private static readonly ImportRejection[] Reasons = Enum.GetValues<ImportRejection>();
    private readonly long[] _counts = new long[Reasons.Length];

    public void Rejected(ImportRejection reason)
    {
        Interlocked.Increment(ref _counts[(int)reason]);
    }

    /// <summary>
    ///     Every reason with its count, including the zeros. A series that appears
    ///     only once a failure has happened reads as a scrape gap rather than as a
    ///     healthy zero, and an alert on it cannot distinguish the two.
    /// </summary>
    public IEnumerable<(string Reason, long Count)> Snapshot()
    {
        return Reasons.Select(reason => (Label(reason), Interlocked.Read(ref _counts[(int)reason])));
    }

    private static string Label(ImportRejection reason)
    {
        return reason switch
        {
            ImportRejection.BadRequest => "bad_request",
            ImportRejection.Unauthorized => "unauthorized",
            ImportRejection.GateFull => "gate_full",
            ImportRejection.WriteTimeout => "write_timeout",
            ImportRejection.TooLarge => "too_large",
            // No catch-all: a reason added without a label here must fail the build
            // rather than publish refusals under a name that says nothing.
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        };
    }
}

/// <summary>
///     The incident import's refusals, on a series of their own: a label added to
///     the findings counter would change what an unaggregated alert on it matches.
/// </summary>
public sealed class IncidentImportMetrics : ImportMetrics;

/// <summary>
///     The correlation import's refusals, on a series of their own for the reason the
///     incident import has one.
/// </summary>
public sealed class CorrelationImportMetrics : ImportMetrics;

/// <summary>
///     The detection import's refusals, on a series of their own for the reason the
///     incident import has one.
/// </summary>
public sealed class DetectionImportMetrics : ImportMetrics;
