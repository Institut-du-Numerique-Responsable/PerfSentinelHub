namespace PerfSentinelHub.Collection;

/// <summary>
///     The ledger a daemon names in its status: which one it runs, and the UTC epoch days
///     it holds. A poll pass starts from its oldest day the first time it reads it.
/// </summary>
public sealed record DetectionLedgerStatus(
    string LedgerId,
    long StartedMs,
    int OldestDay,
    int CurrentDay,
    int? SealedThrough);
