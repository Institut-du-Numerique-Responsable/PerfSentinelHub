using PerfSentinelHub.Analysis;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Api;

/// <summary>
///     What one daemon reported, already classified. The configuration sections
///     stay as raw JSON text: they are relayed, never read by the Hub.
/// </summary>
public sealed record DaemonViewData(
    string SourceId,
    long ObservedAtMs,
    string State,
    string? ErrorCode,
    DaemonStatus? Status,
    string? ConfigJson,
    string? ConfigUnavailableReason,
    string? DetectionConfigJson,
    string? ScoringConfigJson,
    string? EnergyModel,
    string? EnergySource,
    // The engine version whose defaults are published alongside, which is the
    // binary this Hub embeds and not necessarily the one the daemon runs.
    string DefaultsEngineVersion,
    // Null when the export snapshot was read; otherwise the error code of the
    // read that failed, because unread hints are not the same thing as none.
    string? HintsUnavailableReason,
    IReadOnlyList<ResultWarning> Warnings,
    int WarningsDropped);
