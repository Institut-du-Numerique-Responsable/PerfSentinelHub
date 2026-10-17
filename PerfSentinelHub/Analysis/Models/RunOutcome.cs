namespace PerfSentinelHub.Analysis;

public sealed record RunOutcome(
    string Status,
    string? ErrorCode,
    string? ProducerVersion,
    ReportSummary? Summary);
