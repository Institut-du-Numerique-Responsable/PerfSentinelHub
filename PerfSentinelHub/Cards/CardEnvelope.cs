using System.Globalization;
using System.Text.Json;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Cards;

public static class CardEnvelope
{
    private static readonly FrozenFinding Empty = new("", "", "", null, null, null);

    /// <summary>Reads a frozen envelope; a missing or mistyped value reads "" or null, never throws.</summary>
    public static FrozenFinding Read(string envelopeJson)
    {
        try
        {
            using var document = JsonDocument.Parse(envelopeJson);
            if (Child(document.RootElement, "finding") is not { } body)
                return Empty;
            var pattern = Child(body, "pattern");

            return new FrozenFinding(
                JsonRead.ReadString(body, "type") ?? "",
                JsonRead.ReadString(body, "service") ?? "",
                JsonRead.ReadString(body, "source_endpoint") ?? "",
                Long(pattern, "occurrences"),
                Long(pattern, "span_duration_us_p99"),
                DateTimeOffset.TryParse(
                    JsonRead.ReadString(body, "first_timestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var observed)
                    ? observed.ToUnixTimeMilliseconds()
                    : null);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static long? Long(JsonElement? parent, string name)
    {
        return parent is { } element ? JsonRead.ReadLong(element, name) : null;
    }

    private static JsonElement? Child(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(name, out var child) &&
               child.ValueKind == JsonValueKind.Object
            ? child
            : null;
    }
}
