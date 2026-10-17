namespace PerfSentinelHub.Api;

/// <summary>A count and the moment it was taken, on the Hub's clock.</summary>
public sealed record Counted<T>(T Value, DateTimeOffset CountedAt);
