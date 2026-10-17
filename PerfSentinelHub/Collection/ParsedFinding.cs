namespace PerfSentinelHub.Collection;

public sealed record ParsedBatch(IReadOnlyList<ParsedFinding> Findings, int RejectedCount);

public sealed record ParsedImport(string ProducerVersion, ParsedBatch Batch);

public sealed record ParsedFinding(
    string Signature,
    string EnvelopeJson,
    string Service,
    string FindingType,
    string Severity,
    string Endpoint,
    string TemplateHash,
    string? TraceId,
    string Confidence,
    int ConfidenceRank,
    long? FirstSeenMs,
    string? Grouping = null);
