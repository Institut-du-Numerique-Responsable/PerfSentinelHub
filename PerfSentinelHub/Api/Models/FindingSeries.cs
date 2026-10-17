using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

/// <summary>
///     One sample of perf_sentinel_hub_findings. The environment is configured,
///     the type and the severity are already folded into their closed vocabularies.
/// </summary>
public sealed record FindingSeries(string Environment, FindingCount Findings);
