using System.Text.Json;
using PerfSentinelHub.Api;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Collection;

/// <summary>
///     One page of a daemon's detection ledger, as GET /api/detections serves it and as the
///     daemon pushes it. The whole page is refused on the first value out of bounds: its rows
///     merge by MAX, so a wrong count once stored can never come down again.
/// </summary>
public static class DetectionLedgerParser
{
    /// <summary>The daemon's own page size, on a poll and on a push.</summary>
    public const int MaxCountRows = 5000;

    /// <summary>The daemon retains 31 days at most, so no page describes more.</summary>
    public const int MaxDayRows = 31;

    private const int MaxSignatureLength = 1024;

    // The largest integer a JavaScript reader of the served count still holds exactly.
    private const long MaxInteger = 1L << 53;

    // The epoch days (UTC) accepted around the Hub's own today.
    private const int DaysBefore = 400;
    private const int DaysAfter = 2;

    private const long MsPerDay = 86_400_000;

    public static DetectionLedgerPage ParsePage(ReadOnlyMemory<byte> payload, long nowMs)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, JsonRead.UniqueProperties);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The detection page is not valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("counts", out var counts) || counts.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("days", out var days) || days.ValueKind != JsonValueKind.Array)
                throw Invalid("The detection page has no counts or days array.");
            if (counts.GetArrayLength() > MaxCountRows || days.GetArrayLength() > MaxDayRows)
                throw new ImportBatchTooLargeException();

            var window = new DayWindow(nowMs / MsPerDay);
            return new DetectionLedgerPage(
                ReadLedgerId(root),
                Integer(root, "started_ms"),
                window.Day(root, "current_day"),
                window.Day(root, "oldest_day"),
                IsAbsent(root, "sealed_through") ? null : window.Day(root, "sealed_through"),
                [.. counts.EnumerateArray().Select(row => ReadCount(Row(row), window))],
                [.. days.EnumerateArray().Select(row => ReadDay(Row(row), window))],
                ReadNextAfter(root));
        }
    }

    private static DetectionCountRow ReadCount(JsonElement row, DayWindow window)
    {
        if (JsonRead.ReadString(row, "signature") is not { Length: > 0 and <= MaxSignatureLength } signature ||
            signature.Any(char.IsControl))
            throw Invalid("A detection count has no readable signature.");
        return new DetectionCountRow(
            window.Day(row, "day"), signature, Integer(row, "detections"), OptionalInteger(row, "extra_io_ops"),
            OptionalInteger(row, "below_threshold_passes"));
    }

    private static DetectionDayRow ReadDay(JsonElement row, DayWindow window)
    {
        if (!row.TryGetProperty("sampling_rate", out var rate) ||
            rate.ValueKind != JsonValueKind.Number ||
            !rate.TryGetDouble(out var samplingRate) ||
            samplingRate is < 0 or > 1)
            throw Invalid("A detection day has no sampling rate in [0, 1].");
        return new DetectionDayRow(
            window.Day(row, "day"),
            Flag(row, "sealed"),
            Flag(row, "gap"),
            Integer(row, "overflow_detections"),
            OptionalInteger(row, "overflow_extra_io_ops"),
            Integer(row, "shed_traces"),
            Integer(row, "ingest_rejected"),
            samplingRate,
            OptionalNumber(row, "avoidable_gco2"),
            OptionalInteger(row, "avoidable_io_ops"),
            OptionalNumber(row, "avoidable_gco2_low"),
            OptionalInteger(row, "total_io_ops"),
            ReadThresholds(row));
    }

    // Absent on an engine before C1. Unknown keys are ignored so a later engine may add one.
    private static DetectionThresholds? ReadThresholds(JsonElement row)
    {
        if (IsAbsent(row, "thresholds"))
            return null;
        var thresholds = row.GetProperty("thresholds");
        if (thresholds.ValueKind != JsonValueKind.Object)
            throw Invalid("thresholds is not an object.");
        return new DetectionThresholds(
            OptionalInteger(thresholds, "slow_query_threshold_ms"),
            OptionalInteger(thresholds, "slow_http_threshold_ms"),
            OptionalInteger(thresholds, "max_fanout"),
            OptionalInteger(thresholds, "chatty_threshold"),
            OptionalInteger(thresholds, "pool_saturation_threshold"),
            OptionalInteger(thresholds, "serialized_min_calls"));
    }

    // The daemon writes a lower-case hyphenated UUID, and the ledger is keyed on it: an
    // upper-case spelling of the same GUID must not open a second ledger.
    private static string ReadLedgerId(JsonElement root)
    {
        return JsonRead.ReadString(root, "ledger_id") is { } ledgerId && Guid.TryParseExact(ledgerId, "D", out _)
            ? ledgerId.ToLowerInvariant()
            : throw Invalid("The detection page's ledger_id is not a GUID.");
    }

    private static string? ReadNextAfter(JsonElement root)
    {
        if (IsAbsent(root, "next_after"))
            return null;
        return JsonRead.ReadString(root, "next_after") ??
               throw Invalid("The detection page's next_after is not a string.");
    }

    private static JsonElement Row(JsonElement row)
    {
        return row.ValueKind == JsonValueKind.Object ? row : throw Invalid("A detection row is not an object.");
    }

    private static bool IsAbsent(JsonElement element, string name)
    {
        return !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;
    }

    private static long Integer(JsonElement element, string name)
    {
        return JsonRead.ReadLong(element, name) is { } value and >= 0 and <= MaxInteger
            ? value
            : throw Invalid($"{name} is not an integer in [0, 2^53].");
    }

    // Absent while the daemon scores no carbon: the grams are unknown, not zero.
    private static double? OptionalNumber(JsonElement element, string name)
    {
        if (IsAbsent(element, name))
            return null;
        return element.GetProperty(name) is { ValueKind: JsonValueKind.Number } value &&
               value.TryGetDouble(out var number) &&
               number is >= 0 and <= MaxInteger
            ? number
            : throw Invalid($"{name} is not a number in [0, 2^53].");
    }

    // Absent while green is off on the daemon: the I/O is unknown, not zero.
    private static long? OptionalInteger(JsonElement element, string name)
    {
        return IsAbsent(element, name) ? null : Integer(element, name);
    }

    private static bool Flag(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw Invalid($"{name} is not a boolean.");
    }

    private static InvalidDataException Invalid(string message)
    {
        return new InvalidDataException(message);
    }

    // A day is a u32 on the daemon, so never negative, even while the window's lower edge is.
    private readonly record struct DayWindow(long Today)
    {
        public int Day(JsonElement element, string name)
        {
            return JsonRead.ReadLong(element, name) is { } day and >= 0 &&
                   day >= Today - DaysBefore && day <= Today + DaysAfter
                ? (int)day
                : throw Invalid($"{name} is not a day in [today - {DaysBefore}, today + {DaysAfter}].");
        }
    }
}
