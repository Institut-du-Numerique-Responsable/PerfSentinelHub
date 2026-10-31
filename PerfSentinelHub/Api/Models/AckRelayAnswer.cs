namespace PerfSentinelHub.Api;

/// <summary>What the Hub answers a relayed write with. A 204 carries no detail and no code.</summary>
public readonly record struct AckRelayAnswer(int Status, string? Detail, string? Code = null);
