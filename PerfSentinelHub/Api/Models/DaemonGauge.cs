namespace PerfSentinelHub.Api;

/// <summary>
///     One figure against the cap it runs into. Pct is null when either side is
///     unknown, which keeps the ratio out of the state rules instead of letting a
///     missing cap read as room to spare.
/// </summary>
public sealed record DaemonGauge(long? Value, long? Capacity, double? Pct, bool AtCapacity);

public sealed record DaemonGauges(DaemonGauge Traces, DaemonGauge AnalysisQueue, DaemonGauge Findings);
