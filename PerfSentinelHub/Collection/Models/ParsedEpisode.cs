namespace PerfSentinelHub.Collection;

/// <summary>
///     One side of a correlated pair, the correlator's own endpoint. The two grouping fields
///     are omitted on the wire when the finding had no grouping, and read as null.
/// </summary>
public sealed record EpisodeSide(
    string FindingType,
    string Service,
    string Template,
    string? GroupingKey,
    string? GroupingValue);

/// <summary>
///     One period during which a pair of findings kept following each other, as its
///     daemon cut it. The daemon is the author: the Hub merges copies and re-derives nothing.
/// </summary>
public sealed record ParsedEpisode(
    string Id,
    string PairKey,
    EpisodeSide Source,
    EpisodeSide Target,
    long StartedAtMs,
    long LastSeenMs,
    long? EndedAtMs,
    long GapMs,
    double PeakConfidence,
    long PeakCoOccurrences,
    double LastConfidence,
    long LastCoOccurrences,
    double? MedianLagMs,
    string? SampleTraceId,
    string? SourceSampleTraceId);

public sealed record ParsedEpisodeImport(
    string ProducerVersion,
    IReadOnlyList<ParsedEpisode> Episodes,
    int RejectedCount);
