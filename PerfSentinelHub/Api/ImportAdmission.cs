namespace PerfSentinelHub.Api;

/// <summary>The incident import's admission: the shared gate, its own refusal count.</summary>
public sealed record IncidentImportAdmission(ImportGate Gate, IncidentImportMetrics Metrics)
{
    public T Refuse<T>(ImportRejection reason, T result)
    {
        Metrics.Rejected(reason);
        return result;
    }
}

/// <summary>The correlation import's admission: the shared gate, its own refusal count.</summary>
public sealed record CorrelationImportAdmission(ImportGate Gate, CorrelationImportMetrics Metrics)
{
    public T Refuse<T>(ImportRejection reason, T result)
    {
        Metrics.Rejected(reason);
        return result;
    }
}

/// <summary>
///     The import endpoint's admission control: the concurrency gate that decides
///     whether a request runs at all, and the counter that records every refusal.
///     One parameter rather than two because a refusal always touches both.
/// </summary>
public sealed record ImportAdmission(ImportGate Gate, ImportMetrics Metrics)
{
    /// <summary>Counts a refused import and hands its result straight back.</summary>
    public T Refuse<T>(ImportRejection reason, T result)
    {
        Metrics.Rejected(reason);
        return result;
    }
}
