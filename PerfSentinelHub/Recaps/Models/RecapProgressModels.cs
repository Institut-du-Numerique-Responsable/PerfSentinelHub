using PerfSentinelHub.Api;

namespace PerfSentinelHub.Recaps;

/// <summary>The steps of one recap computation, in the order RecapService takes them.</summary>
public enum RecapStage
{
    FindingsPeriod,
    FindingsOpen,
    FindingsClosed,
    FindingsPrevious,
    Coverage,
    Carbon,
    Actions,
    Labels,
    Assemble
}

/// <summary>
///     Told when a computation starts each stage, or passes over one it has nothing to read for on
///     that day. A stage ends when the next one starts or the computation returns.
/// </summary>
public interface IRecapProgress
{
    void Enter(RecapStage stage);

    void Skip(RecapStage stage);
}

/// <summary>
///     Who a computed recap is for: the caller's subject and that scope's patterns when their
///     findings.read scope is narrowed, null when it is not, and the ids of the sources they see. Two callers with the same audience
///     would be computed the same recap, so they share one.
/// </summary>
public sealed record RecapJobAudience(string? Owner, string Sources);

/// <summary>One recap computation: its audience and the query, the view and the moves cut excepted.</summary>
public sealed record RecapJobKey(
    RecapJobAudience Audience,
    RecapPeriodKind Kind,
    DateOnly At,
    string? Environment,
    string? SourceId,
    string? Service,
    string? Grouping);

public enum RecapJobStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum RecapStageState
{
    Pending,
    Running,
    Done,
    Skipped
}

/// <summary>What happened to a request for a recap job, as the metrics count it.</summary>
public enum RecapJobOutcome
{
    Started,
    Reused,
    Busy,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>A job started, joined or reused, or none because as many as the Hub runs at once are running.</summary>
public sealed record RecapJobStart(RecapJobState? Job, bool Busy);
