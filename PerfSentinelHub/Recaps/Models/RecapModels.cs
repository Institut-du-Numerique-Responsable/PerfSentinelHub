using System.Text.Json.Serialization;

namespace PerfSentinelHub.Recaps;

// The recap snapshot GET /api/recap serves, snake_case through HubJsonContext. In the pilotage
// view the technical keys (signature, template, suggestion, suggested_fix, code_location,
// confidence, events) are absent, not null, and so is endpoint, kept only on a priority or a move
// with no label. RecapViews.Frozen, the view a frozen report and its webhook carry, drops it there
// too. label is always there, null while nobody has named the endpoint.

public sealed record RecapSnapshot(
    int SchemaVersion,
    string View,
    long GeneratedAtMs,
    RecapPeriodInfo Period,
    RecapPeriodInfo Previous,
    RecapFilters Filters,
    bool ComparisonExact,
    RecapTiles Tiles,
    IReadOnlyList<RecapPriority> Priorities,
    int MorePriorities,
    RecapMovements Movements,
    bool NothingMoved,
    IReadOnlyList<string> SourcesWithoutVolume,
    bool Truncated);

public sealed record RecapPeriodInfo(
    string Kind,
    string From,
    string To,
    bool InProgress,
    IReadOnlyList<string> Incomplete,
    IReadOnlyList<string> Notes,
    double? SamplingRate,
    int? TruncatedToDays);

public sealed record RecapFilters(string? Environment, string? SourceId, string? Service, string? Grouping);

public sealed record RecapTiles(
    long? AvoidableIoOps,
    long? PreviousAvoidableIoOps,
    double? Co2Grams,
    double? PreviousCo2Grams,
    int Co2SourcesWithoutCoefficient,
    string? Co2MissingReason,
    int ActiveFindings,
    int PreviousActiveFindings,
    int AckedFindings,
    RecapActionCounts Actions);

public sealed record RecapActionCounts(
    int Todo,
    int InProgress,
    int Done,
    int Confirmed,
    int Contradicted,
    int CameBack);

public sealed record RecapPriority(
    int Rank,
    string FindingType,
    string Severity,
    string Service,
    string Plain,
    long? Detections,
    long? AvoidableIoOps,
    double? Co2Grams,
    bool Co2Partial,
    bool VolumeKnown,
    string Trend,
    double? Change,
    string? Label,
    RecapAction? Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Signature,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Endpoint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Template,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Suggestion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RecapFix? SuggestedFix,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RecapCodeLocation? CodeLocation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Confidence);

public sealed record RecapFix(string Recommendation, string? Framework, string? ReferenceUrl);

public sealed record RecapCodeLocation(string? Function, string? Filepath, int? Lineno, string? Namespace);

public sealed record RecapAction(
    string Status,
    string? Owner,
    string? OwnerDisplay,
    long UpdatedAtMs,
    long? DoneAtMs,
    string? Confirmation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RecapActionEvent>? Events);

public sealed record RecapActionEvent(
    string? From,
    string To,
    string? Owner,
    string? OwnerDisplay,
    string By,
    long AtMs);

public sealed record RecapMovements(
    IReadOnlyList<RecapMove> New,
    IReadOnlyList<RecapMove> Gone,
    int GoneWithConfirmedAction,
    IReadOnlyList<RecapMove> Up,
    IReadOnlyList<RecapMove> Down,
    IReadOnlyList<RecapMove> CameBack,
    IReadOnlyList<RecapMove> DoneStillDetected);

public sealed record RecapMove(
    string FindingType,
    string Service,
    string Plain,
    string Trend,
    double? Change,
    string? Label,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Signature,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Endpoint);
