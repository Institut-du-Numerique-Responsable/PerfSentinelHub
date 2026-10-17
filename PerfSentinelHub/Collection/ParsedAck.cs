namespace PerfSentinelHub.Collection;

/// <summary>
///     One active acknowledgment as a daemon listed it. `Origin` is the daemon's
///     `source`, renamed because a source is a daemon everywhere else in the Hub:
///     `daemon` for an ack taken at runtime, `toml` for one of its CI baseline.
///     `At` is text and never parsed, since the baseline writes it freely.
/// </summary>
public sealed record ParsedAck(
    string Signature,
    string Origin,
    string By,
    string? Reason,
    string At,
    string? ExpiresAt,
    long? ExpiresAtMs);

public sealed record ParsedAckPage(IReadOnlyList<ParsedAck> Acks, int RejectedCount);
