namespace PerfSentinelHub.Collection;

/// <summary>
///     What a daemon says about itself right now. Version stays mandatory, it is
///     what the poller records as the producer version, so a body without one is
///     still an InvalidStatusException. The gauges are optional: a capacity is null
///     when the field is missing or zero, because a capacity of zero is not a
///     capacity but an unknown.
/// </summary>
public sealed record DaemonStatus(
    string Version,
    long? UptimeSeconds,
    long? ActiveTraces,
    long? MaxActiveTraces,
    long? AnalysisQueueDepth,
    long? AnalysisQueueCapacity,
    long? StoredFindings,
    long? MaxRetainedFindings)
{
    /// <summary>
    ///     Findings the daemon put in its ring since it started, evicted ones
    ///     included (perf-sentinel 0.31.2 and later). Read by RingWatch.
    /// </summary>
    public long? FindingsStoredTotal { get; init; }

    /// <summary>
    ///     The daemon's detection ledger (perf-sentinel 0.37.0 and later). Null when the
    ///     daemon is older, its ledger is off, or the object does not read: the Hub then
    ///     never asks for the ledger. Read by DetectionReader.
    /// </summary>
    public DetectionLedgerStatus? DetectionLedger { get; init; }
}
