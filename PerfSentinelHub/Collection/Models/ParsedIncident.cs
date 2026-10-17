namespace PerfSentinelHub.Collection;

/// <summary>
///     One incident as the daemon listed it: the columns the Hub indexes beside
///     the document kept verbatim, the way a finding envelope is. The findings
///     travel apart from the rest of the document, since they can run to a
///     thousand and only the single-incident route re-emits them.
/// </summary>
public sealed record ParsedIncident(
    string Id,
    string Service,
    string Kind,
    long AtMs,
    long? EndedAtMs,
    long WindowFromMs,
    long WindowToMs,
    long? OldestFindingMs,
    int FindingCount,
    // The daemon's document without its `findings` property.
    string IncidentJson,
    // The `findings` array as the daemon wrote it.
    string FindingsJson);

public sealed record ParsedIncidentPage(IReadOnlyList<ParsedIncident> Incidents, int RejectedCount);

public sealed record ParsedIncidentImport(string ProducerVersion, ParsedIncidentPage Page);
