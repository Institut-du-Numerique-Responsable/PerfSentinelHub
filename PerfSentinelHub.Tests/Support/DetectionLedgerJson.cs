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

    // As the daemon writes a day: the four carbon fields are omitted, not null, while it scores no carbon.
    public static JsonObject Day(
        int day,
        bool sealedDay = false,
        double samplingRate = 1.0,
        double? avoidableGco2 = null,
        long? avoidableIoOps = null,
        double? avoidableGco2Low = null,
        long? totalIoOps = null)
    {
        var json = new JsonObject
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
        if (avoidableGco2 is { } grams)
            json["avoidable_gco2"] = grams;
        if (avoidableIoOps is { } avoidable)
            json["avoidable_io_ops"] = avoidable;
        if (avoidableGco2Low is { } low)
            json["avoidable_gco2_low"] = low;
        if (totalIoOps is { } total)
            json["total_io_ops"] = total;
        return json;
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
