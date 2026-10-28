using System.Text;
using System.Text.Json.Nodes;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Support;

/// <summary>A detection ledger page as a daemon serves or pushes it, each field valid until a test overrides it.</summary>
internal static class DetectionLedgerJson
{
    // Low-entropy on purpose: a secret scanner reads a random-looking value here as a key.
    public const string LedgerId = "00000000-0000-4000-8000-000000000001";

    public const string Signature = "n_plus_one_sql:order-svc:SELECT * FROM orders WHERE id = ?";

    /// <summary>The epoch ms a day starts at, for a clock that should read that day as today.</summary>
    public static long Midnight(int day)
    {
        return day * 86_400_000L;
    }

    // As the daemon writes a row: extra_io_ops is omitted, not null, while green is off.
    public static JsonObject Count(int day, string signature = Signature, long detections = 5, long? extraIoOps = 40)
    {
        var count = new JsonObject { ["day"] = day, ["signature"] = signature, ["detections"] = detections };
        if (extraIoOps is { } io)
            count["extra_io_ops"] = io;
        return count;
    }

    public static JsonObject Day(int day, bool sealedDay = false, double samplingRate = 1.0)
    {
        return new JsonObject
        {
            ["day"] = day,
            ["sealed"] = sealedDay,
            ["gap"] = false,
            ["overflow_detections"] = 0,
            ["overflow_extra_io_ops"] = 0,
            ["shed_traces"] = 0,
            ["ingest_rejected"] = 0,
            ["sampling_rate"] = samplingRate
        };
    }

    /// <summary>A page whose ledger began two days before <paramref name="currentDay" /> and sealed the one before it.</summary>
    public static JsonObject Page(JsonObject[] counts, JsonObject[] days, int currentDay = 2)
    {
        return new JsonObject
        {
            ["ledger_id"] = LedgerId,
            ["started_ms"] = Midnight(currentDay - 2) + 1_000,
            ["current_day"] = currentDay,
            ["oldest_day"] = currentDay - 2,
            ["sealed_through"] = currentDay - 1,
            ["counts"] = new JsonArray([.. counts.Select(JsonNode? (count) => count.DeepClone())]),
            ["days"] = new JsonArray([.. days.Select(JsonNode? (day) => day.DeepClone())]),
            ["next_after"] = null
        };
    }

    public static DetectionLedgerPage Parse(JsonObject page, long nowMs)
    {
        return DetectionLedgerParser.ParsePage(Encoding.UTF8.GetBytes(page.ToJsonString()), nowMs);
    }
}
