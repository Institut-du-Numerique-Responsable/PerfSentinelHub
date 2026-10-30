using System.Globalization;
using System.Text.Json;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Api;

/// <summary>
///     What a caller asks the Hub to remember about an endpoint, or about a whole service when
///     <c>Endpoint</c> is null: its business name, or, for a null <c>Label</c>, to forget it.
/// </summary>
public sealed record LabelWrite(string Service, string? Endpoint, string? Label)
{
    public const int MaxLabelChars = 80;

    /// <summary>The body of <c>PUT /api/labels</c>: service, endpoint and label.</summary>
    public static LabelWrite? TryParse(ReadOnlyMemory<byte> payload, out string? error)
    {
        return Parse(payload, true, out error);
    }

    /// <summary>The body of <c>PUT /api/labels/services</c>: service and label.</summary>
    public static LabelWrite? TryParseService(ReadOnlyMemory<byte> payload, out string? error)
    {
        return Parse(payload, false, out error);
    }

    private static LabelWrite? Parse(ReadOnlyMemory<byte> payload, bool withEndpoint, out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return TryRead(document.RootElement, withEndpoint, out error);
        }
        // The second is an unpaired surrogate, a string the reader cannot hand over.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Refuse("The body is not valid JSON.", out error);
        }
    }

    private static LabelWrite? TryRead(JsonElement root, bool withEndpoint, out string? error)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Refuse("The body must be a JSON object.", out error);
        if (AckWrite.ReadText(root, "service", AckParser.MaxSignatureLength) is not { } service)
            return Refuse("service is required, 1024 characters at most and no control character.", out error);
        var endpoint = withEndpoint ? AckWrite.ReadText(root, "endpoint", AckParser.MaxSignatureLength) : null;
        if (withEndpoint && endpoint is null)
            return Refuse("endpoint is required, 1024 characters at most and no control character.", out error);
        if (!root.TryGetProperty("label", out var value))
            return Refuse("A label needs a label value; send null to remove it.", out error);
        if (!TryReadLabel(value, out var label))
            return Refuse(
                "label must be 1 to 80 characters with no control or format character, or null to remove it.",
                out error);

        error = null;
        return new LabelWrite(service, endpoint, label);
    }

    // Trimmed first; nothing left, like null, removes the label. A format character (a direction
    // override, a zero-width one) would make the name read other than it is stored.
    private static bool TryReadLabel(JsonElement value, out string? label)
    {
        label = null;
        if (value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        var text = value.GetString()!.Trim();
        if (text.Length == 0)
            return true;
        if (text.Length > MaxLabelChars || text.Any(Hidden))
            return false;
        label = text;
        return true;
    }

    private static bool Hidden(char c)
    {
        return char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format;
    }

    private static LabelWrite? Refuse(string why, out string? error)
    {
        error = why;
        return null;
    }
}
