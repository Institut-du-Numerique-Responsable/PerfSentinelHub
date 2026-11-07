using System.Text.Json.Serialization;

namespace PerfSentinelHub.Recaps;

// The recap snapshot GET /api/recap serves, snake_case through HubJsonContext. In the pilotage
// view the technical keys (signature, template, suggestion, suggested_fix, code_location,
// confidence, events) are absent, not null, and so is endpoint, kept only on a priority or a move
// with no label. RecapViews.Frozen, the view a frozen report and its webhook carry, drops it there
// too. label is always there, null while nobody has named the endpoint, and so is service_label for
// the service, which no view drops: a receiver can still group by the technical service.

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
    bool Truncated,
    // The sources that recorded nothing until after the period, left out of it; null on a report
    // frozen before the field existed.
    IReadOnlyList<string>? LateSources = null);

public sealed record RecapPeriodInfo(
    string Kind,
    string From,
    string To,
    bool InProgress,
    IReadOnlyList<string> Incomplete,
    IReadOnlyList<string> Notes,
    double? SamplingRate,
    int? TruncatedToDays);

// service_label is the business name of the service filter, so a Management scope line reads by it.
// Service and Grouping hold the value when exactly one is chosen, Services and Groupings the list
// when several are. The lists are absent from the JSON otherwise, so a recap of one value, and a
// report stored before the lists, read and write as they always did.
public sealed record RecapFilters(
    string? Environment,
    string? SourceId,
    string? Service,
    string? Grouping,
    string? ServiceLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Services = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Groupings = null)
{
    /// <summary>The filters of a request that chose these lists: a lone value goes to the string, else the list.</summary>
    public static RecapFilters Of(
        string? environment,
        string? sourceId,
        IReadOnlyList<string>? services,
        IReadOnlyList<string>? groupings)
    {
        return new RecapFilters(
            environment, sourceId, LoneOf(services), LoneOf(groupings), null, ManyOf(services), ManyOf(groupings));
    }

    private static string? LoneOf(IReadOnlyList<string>? values)
    {
        return values is [var one] ? one : null;
    }

    private static IReadOnlyList<string>? ManyOf(IReadOnlyList<string>? values)
    {
        return values is { Count: > 1 } ? values : null;
    }
}

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
    string? ServiceLabel,
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
    IReadOnlyList<RecapMove> DoneStillDetected,
    // Each list's whole length once GET /api/recap cut them; absent from a report or a webhook, whose
    // lists are whole.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RecapMoveCounts? Counts = null);

public sealed record RecapMoveCounts(int New, int Gone, int Up, int Down, int CameBack, int DoneStillDetected);

public sealed record RecapMove(
    string FindingType,
    string Service,
    string Plain,
    string Trend,
    double? Change,
    string? Label,
    string? ServiceLabel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Signature,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Endpoint);
