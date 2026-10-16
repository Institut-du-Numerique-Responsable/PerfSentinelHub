using System.Text.Json;
using PerfSentinelHub.Api;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Collection;

/// <summary>
///     One side of a correlated pair, the correlator's own endpoint. The two grouping fields
///     are omitted on the wire when the finding had no grouping, and read as null.
/// </summary>
public sealed record EpisodeSide(
    string FindingType,
    string Service,
    string Template,
    string? GroupingKey,
    string? GroupingValue);

/// <summary>
///     One period during which a pair of findings kept following each other, as its
///     daemon cut it. The daemon is the author: the Hub merges copies and re-derives nothing.
/// </summary>
public sealed record ParsedEpisode(
    string Id,
    string PairKey,
    EpisodeSide Source,
    EpisodeSide Target,
    long StartedAtMs,
    long LastSeenMs,
    long? EndedAtMs,
    long GapMs,
    double PeakConfidence,
    long PeakCoOccurrences,
    double LastConfidence,
    long LastCoOccurrences,
    double? MedianLagMs,
    string? SampleTraceId,
    string? SourceSampleTraceId);

public sealed record ParsedEpisodeImport(
    string ProducerVersion,
    IReadOnlyList<ParsedEpisode> Episodes,
    int RejectedCount);

public static class CorrelationParser
{
    /// <summary>A pushed batch holds at most this many episodes, the daemon's own batch size.</summary>
    public const int MaxImportEpisodes = 100;

    /// <summary>A sample trace id travels into a link, so a longer one is refused rather than stored.</summary>
    public const int MaxTraceIdLength = 64;

    public static ParsedEpisodeImport ParseImport(ReadOnlyMemory<byte> payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, JsonRead.UniqueProperties);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The import body is not valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                JsonRead.ReadString(root, "producer_version") is not { Length: > 0 and <= 64 } producerVersion ||
                !root.TryGetProperty("episodes", out var episodes) ||
                episodes.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The import body is invalid.");

            var count = episodes.GetArrayLength();
            if (count > MaxImportEpisodes)
                throw new ImportBatchTooLargeException();
            if (count == 0)
                throw new InvalidDataException("The import batch is empty.");

            var parsed = new List<ParsedEpisode>(count);
            foreach (var element in episodes.EnumerateArray())
                if (TryParse(element, out var episode))
                    parsed.Add(episode);

            return new ParsedEpisodeImport(producerVersion, parsed, count - parsed.Count);
        }
    }

    // Each episode on its own: one the engine could not have written is refused and
    // counted, and its neighbours still land.
    private static bool TryParse(JsonElement element, out ParsedEpisode episode)
    {
        episode = null!;
        if (element.ValueKind != JsonValueKind.Object ||
            JsonRead.ReadString(element, "id") is not { } id || !IsHexId(id) ||
            JsonRead.ReadString(element, "pair_key") is not { } pairKey || !IsHexId(pairKey) ||
            ReadSide(element, "source") is not { } source ||
            ReadSide(element, "target") is not { } target ||
            JsonRead.ReadEpochMs(element, "started_at_ms") is not { } startedAtMs ||
            JsonRead.ReadEpochMs(element, "last_seen_ms") is not { } lastSeenMs ||
            startedAtMs > lastSeenMs ||
            !TryReadEnd(element, startedAtMs, out var endedAtMs) ||
            JsonRead.ReadLong(element, "gap_ms") is not { } gapMs || gapMs <= 0 ||
            ReadConfidence(element, "peak_confidence") is not { } peakConfidence ||
            ReadConfidence(element, "last_confidence") is not { } lastConfidence ||
            JsonRead.ReadLong(element, "peak_co_occurrences") is not { } peakCoOccurrences || peakCoOccurrences < 0 ||
            JsonRead.ReadLong(element, "last_co_occurrences") is not { } lastCoOccurrences || lastCoOccurrences < 0 ||
            !TryReadTraceId(element, "sample_trace_id", out var sampleTraceId) ||
            !TryReadTraceId(element, "source_sample_trace_id", out var sourceSampleTraceId))
            return false;

        episode = new ParsedEpisode(id, pairKey, source, target, startedAtMs, lastSeenMs, endedAtMs, gapMs,
            peakConfidence, peakCoOccurrences, lastConfidence, lastCoOccurrences, ReadMedianLag(element),
            sampleTraceId, sourceSampleTraceId);
        return true;
    }

    private static EpisodeSide? ReadSide(JsonElement episode, string name)
    {
        if (!episode.TryGetProperty(name, out var side) ||
            side.ValueKind != JsonValueKind.Object ||
            JsonRead.ReadString(side, "finding_type") is not { } findingType ||
            Array.IndexOf(FindingLabels.Types, findingType) < 0 ||
            JsonRead.ReadString(side, "service") is not { Length: > 0 } service ||
            JsonRead.ReadString(side, "template") is not { } template)
            return null;
        return new EpisodeSide(findingType, service, template, JsonRead.ReadString(side, "grouping_key"),
            JsonRead.ReadString(side, "grouping_value"));
    }

    // Absent or null while the episode runs, otherwise no earlier than its start.
    private static bool TryReadEnd(JsonElement element, long startedAtMs, out long? endedAtMs)
    {
        endedAtMs = null;
        if (!element.TryGetProperty("ended_at_ms", out var value) || value.ValueKind == JsonValueKind.Null)
            return true;
        endedAtMs = JsonRead.ReadEpochMs(element, "ended_at_ms");
        return endedAtMs >= startedAtMs;
    }

    private static double? ReadConfidence(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out var confidence) &&
               confidence is >= 0 and <= 1
            ? confidence
            : null;
    }

    // The lag is the daemon's estimate and nothing keys on it: an unreadable one is unknown.
    private static double? ReadMedianLag(JsonElement element)
    {
        return element.TryGetProperty("median_lag_ms", out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out var lag) &&
               lag >= 0
            ? lag
            : null;
    }

    private static bool TryReadTraceId(JsonElement element, string name, out string? traceId)
    {
        traceId = JsonRead.ReadString(element, name);
        return traceId is null or { Length: <= MaxTraceIdLength };
    }

    // The daemon prints both ids as SHA-256 prefixes in lowercase hex.
    private static bool IsHexId(string value)
    {
        return value.Length == 32 && value.All(char.IsAsciiHexDigitLower);
    }
}
