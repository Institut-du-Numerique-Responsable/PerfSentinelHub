using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Tests.Support;

/// <summary>An episode as a daemon pushes it, each field valid until a test overrides it.</summary>
internal static class EpisodeJson
{
    // Low-entropy on purpose: a secret scanner reads a random-looking value here as a key.
    public const string PairKey = "000000000000000000000000000000aa";

    public static string Id(int index)
    {
        return index.ToString("x32", CultureInfo.InvariantCulture);
    }

    public static JsonObject Episode(
        string id,
        long startedAtMs = 1_800_000_000_000,
        long lastSeenMs = 1_800_000_060_000,
        long? endedAtMs = null,
        string pairKey = PairKey,
        string sourceService = "order-svc",
        string targetService = "payment-svc")
    {
        return new JsonObject
        {
            ["id"] = id,
            ["pair_key"] = pairKey,
            ["source"] = Side("n_plus_one_sql", sourceService, "SELECT * FROM orders WHERE id = ?"),
            ["target"] = Side("pool_saturation", targetService, "SELECT 1"),
            ["started_at_ms"] = startedAtMs,
            ["last_seen_ms"] = lastSeenMs,
            ["ended_at_ms"] = endedAtMs,
            ["peak_confidence"] = 0.92,
            ["peak_co_occurrences"] = 41,
            ["last_confidence"] = 0.88,
            ["last_co_occurrences"] = 37,
            ["median_lag_ms"] = 1200.0,
            ["sample_trace_id"] = "4bf92f3577b34da6a3ce929d0e0e4736",
            ["source_sample_trace_id"] = "00f067aa0ba902b74bf92f3577b34da6",
            ["gap_ms"] = 600_000
        };
    }

    public static string Body(params JsonObject[] episodes)
    {
        return new JsonObject
        {
            ["producer_version"] = "0.30.0",
            ["episodes"] = new JsonArray([.. episodes.Select(JsonNode? (episode) => episode.DeepClone())])
        }.ToJsonString();
    }

    public static IReadOnlyList<ParsedEpisode> Parsed(params JsonObject[] episodes)
    {
        return CorrelationParser.ParseImport(Encoding.UTF8.GetBytes(Body(episodes))).Episodes;
    }

    // As the correlator writes an endpoint: the two grouping fields are omitted when absent.
    private static JsonObject Side(string type, string service, string template)
    {
        return new JsonObject { ["finding_type"] = type, ["service"] = service, ["template"] = template };
    }
}
