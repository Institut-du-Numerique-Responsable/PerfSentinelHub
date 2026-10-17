namespace PerfSentinelHub.Collection;

public sealed record PollResult(
    int ImportedCount,
    int RejectedCount,
    string ProducerVersion,
    bool IsPossiblyTruncated,
    // Null when the daemon publishes no incidents, or refused or failed the read.
    int? IncidentCount,
    // Null when RingWatch cannot judge this poll: see Observe.
    long? MissedFindings = null);
