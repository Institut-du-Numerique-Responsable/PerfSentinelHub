using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Cards;

/// <summary>
///     What a share card knows about a finding type: its family, whether it carries avoidable
///     volume, and which detection threshold the daemon applies to it (C1). A type is the
///     signature's prefix before the first colon, as the engine's compute_signature writes it.
/// </summary>
public static class CardTypes
{
    /// <summary>The types a daemon watches under their threshold once fixed.</summary>
    public static readonly IReadOnlySet<string> ThresholdTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "slow_sql", "slow_http", "slow_messaging", "excessive_fanout", "chatty_service", "pool_saturation",
        "serialized_calls"
    };

    public static string TypeOf(string signature)
    {
        var colon = signature.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? signature : signature[..colon];
    }

    public static bool HasAvoidableVolume(string findingType)
    {
        return findingType is "n_plus_one_sql" or "n_plus_one_http" or "n_plus_one_messaging" or "redundant_sql"
            or "redundant_http";
    }

    public static string Family(string findingType)
    {
        return findingType switch
        {
            _ when HasAvoidableVolume(findingType) => "extra_calls",
            "slow_sql" or "slow_http" or "slow_messaging" => "slow_calls",
            "excessive_fanout" or "chatty_service" or "pool_saturation" or "serialized_calls" => "call_architecture",
            _ => FindingLabels.Other
        };
    }

    // The engine applies detection.slow_query_threshold_ms to every slow detector but HTTP.
    public static long? ThresholdOf(DetectionThresholds thresholds, string findingType)
    {
        return findingType switch
        {
            "slow_sql" or "slow_messaging" => thresholds.SlowQueryThresholdMs,
            "slow_http" => thresholds.SlowHttpThresholdMs,
            "excessive_fanout" => thresholds.MaxFanout,
            "chatty_service" => thresholds.ChattyThreshold,
            "pool_saturation" => thresholds.PoolSaturationThreshold,
            "serialized_calls" => thresholds.SerializedMinCalls,
            _ => null
        };
    }
}
