namespace PerfSentinelHub.Api;

/// <summary>What the Hub answers a relayed write with. A 204 carries no detail.</summary>
public readonly record struct AckRelayAnswer(int Status, string? Detail);
