using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Recaps;

public static class RecapRanking
{
    /// <summary>
    ///     Findings with avoidable I/O first, by that volume; the others by severity. Ties go to
    ///     the finding detected more often (unknown last), then to the signature.
    /// </summary>
    public static IReadOnlyList<RecapFinding> Order(IEnumerable<RecapFinding> findings)
    {
        return
        [
            .. findings
                .OrderBy(finding => finding.AvoidableIoOps > 0 ? 0 : 1)
                .ThenByDescending(finding => finding.AvoidableIoOps > 0 ? finding.AvoidableIoOps : 0)
                .ThenByDescending(finding => finding.AvoidableIoOps > 0 ? 0 : FindingParser.SeverityRank(finding.Severity))
                .ThenByDescending(finding => finding.Detections ?? -1)
                .ThenBy(finding => finding.Signature, StringComparer.Ordinal)
        ];
    }
}
