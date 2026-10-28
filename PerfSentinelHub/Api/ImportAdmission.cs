namespace PerfSentinelHub.Api;

/// <summary>
///     An import endpoint's admission control: the concurrency gate that decides
///     whether a request runs at all, and the counter that records every refusal.
///     One parameter rather than two because a refusal always touches both. Each
///     import route closes it over its own metrics type, so each counts on its own series.
/// </summary>
public sealed record ImportAdmission<TMetrics>(ImportGate Gate, TMetrics Metrics)
    where TMetrics : ImportMetrics
{
    /// <summary>Counts a refused import and hands its result straight back.</summary>
    public T Refuse<T>(ImportRejection reason, T result)
    {
        Metrics.Rejected(reason);
        return result;
    }
}
