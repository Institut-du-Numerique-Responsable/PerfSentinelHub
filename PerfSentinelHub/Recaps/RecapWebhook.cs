using System.Buffers;
using System.Text.Json;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Recaps;

/// <summary>
///     What a recap webhook receives: the report's coordinates, links when the Hub knows its own address, and the
///     management snapshot.
/// </summary>
public static class RecapWebhook
{
    public static byte[] Body(RecapReportSummary report, RecapSnapshot pilotage, Uri? publicUrl)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("event", "recap");
            writer.WriteString("report_id", report.Id);
            writer.WriteString("subscription", report.Subscription);
            writer.WriteStartObject("period");
            writer.WriteString("kind", report.PeriodKind);
            writer.WriteString("from", report.PeriodFrom);
            writer.WriteString("to", report.PeriodTo);
            writer.WriteEndObject();
            writer.WriteBoolean("complete", report.Complete);
            writer.WriteString("pilotage_url", LinkTo(report, RecapViewNames.Pilotage, publicUrl));
            writer.WriteString("tech_url", LinkTo(report, RecapViewNames.Tech, publicUrl));
            writer.WritePropertyName("recap");
            JsonSerializer.Serialize(writer, pilotage, HubJsonContext.Default.RecapSnapshot);
            writer.WriteEndObject();
        }

        return [.. buffer.WrittenSpan];
    }

    internal static string? LinkTo(RecapReportSummary report, string view, Uri? publicUrl)
    {
        return publicUrl is null || !report.Views.Contains(view)
            ? null
            : new Uri(publicUrl, $"/recaps/{report.Id}/{view}.html").ToString();
    }
}
