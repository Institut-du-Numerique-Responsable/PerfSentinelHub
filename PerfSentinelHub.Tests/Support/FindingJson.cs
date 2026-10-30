using System.Text;
using System.Text.Json.Nodes;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Tests.Support;

/// <summary>A finding as a daemon serves it, each field valid until a test overrides it.</summary>
internal static class FindingJson
{
    public static JsonObject Envelope(
        string signature,
        string type = "n_plus_one_sql",
        string severity = "warning",
        string service = "order-svc",
        string endpoint = "GET /orders",
        string template = "SELECT * FROM orders WHERE id = ?",
        long storedAtMs = 0,
        string? grouping = null)
    {
        return new JsonObject
        {
            ["finding"] = new JsonObject
            {
                ["type"] = type,
                ["severity"] = severity,
                ["trace_id"] = "trace-x",
                ["service"] = service,
                ["grouping"] = grouping is null ? new JsonArray() : JsonNode.Parse(grouping),
                ["source_endpoint"] = endpoint,
                ["pattern"] = new JsonObject { ["template"] = template },
                ["suggestion"] = "Batch the lookups",
                ["confidence"] = "daemon_production",
                ["signature"] = signature
            },
            ["first_seen_ms"] = storedAtMs
        };
    }

    public static ParsedBatch Batch(params JsonObject[] envelopes)
    {
        return FindingParser.Parse(Encoding.UTF8.GetBytes(new JsonArray([.. envelopes]).ToJsonString()));
    }
}
