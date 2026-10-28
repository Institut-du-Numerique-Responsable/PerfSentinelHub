using System.Globalization;
using System.Text.Json;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class FindingEnvelopeWriter
{
    private static readonly int UnixEpochDayNumber = DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;

    public static async Task WriteArrayAsync(
        HttpResponse response,
        IReadOnlyList<StoredFinding> rows,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(response.BodyWriter);
        writer.WriteStartArray();

        foreach (var row in rows)
        {
            // Parsed before anything is written: a throw here would abort a
            // body already partly flushed, and the client would see a truncated
            // array behind a 200. A row whose envelope is unreadable is skipped
            // rather than allowed to take the page down with it.
            JsonDocument envelope;
            try
            {
                envelope = JsonDocument.Parse(row.EnvelopeJson);
            }
            catch (JsonException exception)
            {
                // Dropping it silently would make a finding disappear from the
                // page with nothing anywhere saying a row was unreadable.
                LogUnreadableEnvelope(logger, exception, row.Signature);
                continue;
            }

            using var document = envelope;
            writer.WriteStartObject();
            WriteOriginalProperties(writer, document.RootElement);
            WriteHubFields(writer, row);
            WriteSources(writer, row, now);
            WriteAcks(writer, row);
            writer.WriteEndObject();

            // Flush as we go: a full 10 000-envelope page would otherwise sit in memory at once.
            if (writer.BytesPending < 64 * 1024)
                continue;

            await writer.FlushAsync(cancellationToken);
            await response.BodyWriter.FlushAsync(cancellationToken);
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken);
        await response.BodyWriter.FlushAsync(cancellationToken);
    }

    [LoggerMessage(1800, LogLevel.Error, "Finding {Signature} has an unreadable envelope and was skipped.")]
    private static partial void LogUnreadableEnvelope(ILogger logger, Exception exception, string signature);

    /// <summary>
    ///     What the Hub knows and the daemon does not: how long this finding has
    ///     been around, and what it is now.
    /// </summary>
    private static void WriteHubFields(Utf8JsonWriter writer, StoredFinding row)
    {
        writer.WriteNumber("first_seen", row.FirstSeenMs);
        writer.WriteNumber("last_seen", row.LastSeenMs);
        writer.WriteString("max_confidence", row.MaxConfidence);
        // Derived, never stored: active within the grace window,
        // likely_resolved when the endpoint still heartbeats from a
        // reachable source without the finding, not_observed otherwise.
        writer.WriteString("status", row.Status);
        // Only on a windowed read, so an unwindowed one reads as it always has.
        if (row is { Window: { } window, Volume: { } volume })
        {
            WriteVolume(writer, volume);
            writer.WriteString("detections_from", EpochDay(window.FromDay));
            writer.WriteString("detections_to", EpochDay(window.ToDay));
        }

        if (row.Lineage is not { } lineage)
            return;

        writer.WriteStartObject("lineage");
        writer.WriteNumber("original_first_seen", lineage.OriginalFirstSeenMs);
        writer.WriteNumber("predecessors", lineage.Predecessors);
        writer.WriteEndObject();
    }

    /// <summary>
    ///     Every source that reported this finding, those of the scope when the
    ///     read has one, the id first and ordered by id so two identical pages
    ///     compare equal.
    /// </summary>
    private static void WriteSources(Utf8JsonWriter writer, StoredFinding row, DateTimeOffset now)
    {
        writer.WriteStartArray("sources");
        foreach (var source in row.Sources.OrderBy(item => item.SourceId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", source.SourceId);
            writer.WriteString("name", source.SourceName);
            writer.WriteString("environment", source.Environment);
            // Null on a source that only counted the finding: it holds no copy to date or version.
            JsonWrite.StringOrNull(writer, "producer_version", source.ProducerVersion);
            JsonWrite.NumberOrNull(writer, "age_seconds",
                source.LastSeenMs is { } lastSeenMs
                    ? Math.Max(0, (now.ToUnixTimeMilliseconds() - lastSeenMs) / 1000)
                    : null);
            writer.WriteString("status", source.UnreachableSinceMs is null ? "ok" : "unreachable_since");
            if (source.Volume is { } volume)
                WriteVolume(writer, volume);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    ///     The active ack each listed source holds on the finding, as the Hub last
    ///     mirrored it, ordered as `sources` is. Absent when no source holds one,
    ///     so a finding nobody acked reads as it always has.
    /// </summary>
    private static void WriteAcks(Utf8JsonWriter writer, StoredFinding row)
    {
        if (!row.Sources.Exists(source => source.Ack is not null))
            return;

        writer.WriteStartArray("acks");
        foreach (var source in row.Sources.OrderBy(item => item.SourceId, StringComparer.Ordinal))
        {
            if (source.Ack is not { } ack)
                continue;

            writer.WriteStartObject();
            writer.WriteString("source_id", source.SourceId);
            writer.WriteString("source", ack.Origin);
            writer.WriteString("by", ack.By);
            if (ack.Reason is not null)
                writer.WriteString("reason", ack.Reason);
            writer.WriteString("at", ack.At);
            if (ack.ExpiresAt is not null)
                writer.WriteString("expires_at", ack.ExpiresAt);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteVolume(Utf8JsonWriter writer, DetectionVolume volume)
    {
        JsonWrite.NumberOrNull(writer, "detections", volume.Detections);
        if (volume.Complete is { } complete)
            writer.WriteBoolean("detections_complete", complete);
        else
            writer.WriteNull("detections_complete");
        JsonWrite.NumberOrNull(writer, "avoidable_io_ops", volume.AvoidableIoOps);
        writer.WriteStartArray("detections_gaps");
        foreach (var gap in volume.Gaps)
            writer.WriteStringValue(gap);
        writer.WriteEndArray();
        if (volume.SamplingRate is { } samplingRate)
            writer.WriteNumber("detections_sampling_rate", samplingRate);
        else
            writer.WriteNull("detections_sampling_rate");
    }

    private static string EpochDay(int day)
    {
        return DateOnly.FromDayNumber(UnixEpochDayNumber + day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static void WriteOriginalProperties(Utf8JsonWriter writer, JsonElement envelope)
    {
        // LINQ would box JsonElement.ObjectEnumerator and allocate on every envelope.
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
#pragma warning disable S3267
        foreach (var property in envelope.EnumerateObject())
            if (property.Name is not ("first_seen" or "last_seen" or "max_confidence" or "sources"
                or "status" or "lineage" or "acks" or "detections" or "detections_complete" or "avoidable_io_ops"
                or "detections_gaps" or "detections_sampling_rate" or "detections_from" or "detections_to"))
                property.WriteTo(writer);
#pragma warning restore S3267
    }
}
