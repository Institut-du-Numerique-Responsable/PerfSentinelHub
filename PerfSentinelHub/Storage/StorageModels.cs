using System.Diagnostics.CodeAnalysis;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Storage;

public sealed record SourceSnapshot(
    string SourceId,
    string SourceName,
    string Environment,
    string ProducerVersion);

public sealed record StoredFinding(
    [property: SuppressMessage(
        "ReSharper",
        "NotAccessedPositionalProperty.Global",
        Justification = "Queried by storage contract tests; the HTTP writer preserves the original envelope.")]
    string Signature,
    string EnvelopeJson,
    long FirstSeenMs,
    long LastSeenMs,
    string MaxConfidence,
    string Status,
    List<FindingSourceObservation> Sources)
{
    /// <summary>
    ///     Chain back to the finding this one mutated from, attached after the
    ///     main query when a lineage link exists. Null for a finding with no
    ///     recorded predecessor.
    /// </summary>
    public LineageInfo? Lineage { get; set; }
}

/// <summary>
///     Flattened view of a finding's mutation chain: the earliest predecessor
///     birth (surviving the predecessors' retention purge, since it is copied
///     at link time) and how many hops the chain holds.
/// </summary>
public sealed record LineageInfo(long OriginalFirstSeenMs, int Predecessors);

public sealed record FindingSourceObservation(
    string SourceId,
    string SourceName,
    string Environment,
    string ProducerVersion,
    long LastSeenMs,
    long? UnreachableSinceMs,
    // The active ack this source's daemon holds on the finding, as last mirrored.
    MirroredAck? Ack = null);

/// <summary>
///     One row of source_acks as a read serves it. `Origin` is `daemon` or
///     `toml`, and the two dates are the daemon's text, relayed as they came.
/// </summary>
public sealed record MirroredAck(string Origin, string By, string? Reason, string At, string? ExpiresAt);

/// <summary>
///     Last known collection state for one configured source. Every field is
///     nullable because a source that has never been polled, and a push-only
///     source before its first import, both have no row at all.
/// </summary>
public sealed record SourceState(
    long? LastAttemptMs,
    long? LastSuccessMs,
    long? UnreachableSinceMs,
    string? ProducerVersion,
    string? LastErrorCode);

/// <summary>
///     One daemon's capture of an incident as the Hub keeps it: the daemon's
///     document without its findings, the Hub's own clock for when the copy was
///     first and last refreshed, and the findings when the read asked for them.
///     The id, the service, the kind, the start and the window's end are columns
///     of the table, which the listing filters and orders by, and properties of
///     the document, which is relayed whole, so the row carries them once rather
///     than twice.
/// </summary>
public sealed record StoredIncident(
    string SourceId,
    long? EndedAtMs,
    long WindowFromMs,
    long? OldestFindingMs,
    int FindingCount,
    string IncidentJson,
    long FirstSeenMs,
    long LastSeenMs,
    // Null on the listing, which never reads the column.
    string? FindingsJson);

/// <summary>
///     One daemon's copy of a correlation episode as a read serves it. EndedAtMs is the
///     served end: the daemon's, or the last co-occurrence of an episode silent past its
///     gap plus 15 minutes, which is the only case EndedBy is set, to `stale`. SourceName
///     is the configured name, filled by the route, since storage holds ids only.
/// </summary>
public sealed record StoredEpisode(
    string Id,
    string SourceId,
    string PairKey,
    EpisodeSide Source,
    EpisodeSide Target,
    long StartedAtMs,
    long LastSeenMs,
    long? EndedAtMs,
    string? EndedBy,
    long GapMs,
    double PeakConfidence,
    long PeakCoOccurrences,
    double LastConfidence,
    long LastCoOccurrences,
    double? MedianLagMs,
    string? SampleTraceId,
    string? SourceSampleTraceId,
    long FirstSeenMs,
    long LastSeenHubMs)
{
    public string? SourceName { get; init; }
}

/// <summary>
///     The outcome of the last read of one source, as its ledger filed it:
///     incident_reads or ack_reads, which share a shape.
/// </summary>
public sealed record SourceRead(long LastReadMs, string State, string? LastErrorCode);

public static class IncidentReadStates
{
    public const string Ok = "ok";
    public const string Absent = "absent";
    public const string Unauthorized = "unauthorized";
    public const string Error = "error";
}

public static class AckReadStates
{
    public const string Ok = "ok";

    /// <summary>
    ///     The daemon's listing was full, so its tail is missing. The rows
    ///     read are mirrored, but the mirror cannot say a finding is not acked.
    /// </summary>
    public const string Truncated = "truncated";

    /// <summary>A daemon with no ack store, or too old to list its baseline.</summary>
    public const string Absent = "absent";

    public const string Unauthorized = "unauthorized";
    public const string Error = "error";
}

public static class AnalysisStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";
    public const string Expired = "expired";

    /// <summary>
    ///     Every status, so a reader can emit a series for each one. A gauge that
    ///     vanishes when it reaches zero reads as a scrape failure rather than as
    ///     "nothing is in that state".
    /// </summary>
    public static readonly string[] All =
        [Pending, Running, Succeeded, Failed, Interrupted, Expired];
}

/// <summary>Distinct signatures of one scope sharing a type, a severity and a status.</summary>
public sealed record FindingCount(string FindingType, string Severity, string Status, int Count);

/// <summary>
///     The closed vocabularies a finding's type and severity fold into before
///     they become Prometheus label values. Both columns are free text a daemon
///     sends, and a label fed from that is how /metrics dies of cardinality.
///     The types are the engine's own. The severities are listed most severe
///     first, and <see cref="Collection.FindingParser.SeverityRank" /> ranks a
///     severity by its place here, so the order is stored and must not change.
/// </summary>
public static class FindingLabels
{
    public const string Other = "other";

    public static readonly string[] Types =
    [
        "n_plus_one_sql", "n_plus_one_http", "n_plus_one_messaging", "redundant_sql", "redundant_http",
        "slow_sql", "slow_http", "slow_messaging", "excessive_fanout", "chatty_service", "pool_saturation",
        "serialized_calls"
    ];

    public static readonly string[] Severities = ["critical", "warning", "info"];

    public static string Fold(string[] allowed, string value)
    {
        return Array.IndexOf(allowed, value) >= 0 ? value : Other;
    }
}

/// <summary>
///     One analysis run. The source's name, environment and kind are copied at
///     submission because a run outlives the configuration entry it came from.
///     `RequestJson` and `ResultJson` stay opaque here: their shape is the
///     launcher's contract and varies with the source kind.
/// </summary>
public sealed record AnalysisRun(
    string Id,
    string Status,
    string SourceId,
    string SourceName,
    string Environment,
    string Kind,
    string RequestJson,
    string RequestedBy,
    long CreatedAtMs,
    long? StartedAtMs,
    long? FinishedAtMs,
    long? ExpiresAtMs,
    string? ProducerVersion,
    string? ErrorCode,
    string? ResultJson);

// What no project covers: services and pairs already cut and ordered, and every
// source's count of unmatched copies, which the page joins to the configured sources.
public sealed record UnmatchedCopies(
    IReadOnlyList<UnmatchedService> Services,
    IReadOnlyList<UnmatchedAttribute> Attributes,
    IReadOnlyDictionary<string, int> CountsBySource);

/// <summary>
///     A terminal token's row: never the token, only its SHA-256. Subject owns it, Identity is
///     shown; ProviderGroups and RequiredGroup are the approving session's, frozen at approval.
/// </summary>
public sealed record TerminalToken(
    string Id,
    byte[] TokenHash,
    string Subject,
    string Identity,
    IReadOnlyList<string> ProviderGroups,
    string? RequiredGroup,
    string? ClientAddress,
    long CreatedAtMs,
    long ExpiresAtMs,
    long? LastUsedAtMs);

public enum DisclosureFile
{
    Report,
    Attestation,
    Bundle,
    Pdf
}

/// <summary>The three files a scheduled job publishes, each exactly as received.</summary>
public sealed record DisclosureFiles(byte[] Report, byte[]? Attestation, byte[]? Bundle);

public sealed record DisclosureRecord(
    string Id,
    string ReportUuid,
    string Organisation,
    string PeriodType,
    string PeriodFrom,
    string PeriodTo,
    string Intent,
    string Confidentiality,
    string ContentHash,
    string? IntegrityLevel,
    string? FiguresJson,
    DisclosureFiles Files,
    byte[]? Pdf,
    string? RenderReason,
    string? EngineVersion,
    bool? HashChecked,
    bool? BindingChecked,
    long PublishedAtMs);

/// <summary>A disclosure without its files: what every listing and every check reads.</summary>
public sealed record DisclosureSummary(
    string Id,
    string ReportUuid,
    string Organisation,
    string PeriodType,
    string PeriodFrom,
    string PeriodTo,
    string Intent,
    string Confidentiality,
    string ContentHash,
    string? IntegrityLevel,
    string? FiguresJson,
    bool HasAttestation,
    bool HasBundle,
    bool HasPdf,
    string? RenderReason,
    string? EngineVersion,
    bool? HashChecked,
    bool? BindingChecked,
    long PublishedAtMs,
    long? SignatureAddedAtMs,
    long? WithdrawnAtMs,
    string? WithdrawnReason,
    string? WithdrawnBy)
{
    public bool Withdrawn => WithdrawnAtMs is not null;
}

public sealed record ShareLinkRecord(
    string Id,
    string DisclosureId,
    byte[] TokenHash,
    string CreatedBy,
    long CreatedAtMs,
    long ExpiresAtMs);

public sealed record ShareLinkSummary(
    string Id,
    string CreatedBy,
    long CreatedAtMs,
    long ExpiresAtMs,
    long? RevokedAtMs,
    string? RevokedBy,
    long Downloads,
    long? LastUsedAtMs,
    string? LastClient);

/// <summary>Downloads counted in memory since the last flush, with the last one's time and client.</summary>
public sealed record ShareDownloads(long Count, long LastUsedAtMs, string? LastClient);

public sealed record DeliveryRecord(
    string Id,
    string DisclosureId,
    string Webhook,
    string Event,
    string State,
    int Attempts,
    long? LastAttemptMs,
    long? NextAttemptMs,
    int? LastStatus);

public static class DeliveryStates
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
}

/// <summary>An ack's target at one source: the held copy, if any, and whether the mirror lists an ack of it.</summary>
public sealed record AckTarget(bool Held, string? Service, bool Mirrored, IReadOnlyList<AccessAttribute> Rows);

/// <summary>A mirrored daemon ack as the listing reads it, with its held copy at that source, if any.</summary>
public sealed record ListedAck(
    string Signature,
    string By,
    string? Reason,
    string At,
    string? ExpiresAt,
    bool Held,
    string? Service,
    IReadOnlyList<AccessAttribute> Rows);
