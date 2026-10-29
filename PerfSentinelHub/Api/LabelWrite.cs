using System.Text.Json;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Api;

/// <summary>
///     What a caller asks the Hub to remember about an endpoint: its business name, or, for a null
///     <c>Label</c>, to forget it.
/// </summary>
public sealed record LabelWrite(string Service, string Endpoint, string? Label)
{
    public const int MaxLabelChars = 80;

    public static LabelWrite? TryParse(ReadOnlyMemory<byte> payload, out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return TryRead(document.RootElement, out error);
        }
        // The second is an unpaired surrogate, a string the reader cannot hand over.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Refuse("The body is not valid JSON.", out error);
        }
    }

    private static LabelWrite? TryRead(JsonElement root, out string? error)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Refuse("The body must be a JSON object.", out error);
        if (AckWrite.ReadText(root, "service", AckParser.MaxSignatureLength) is not { } service)
            return Refuse("service is required, 1024 characters at most and no control character.", out error);
        if (AckWrite.ReadText(root, "endpoint", AckParser.MaxSignatureLength) is not { } endpoint)
            return Refuse("endpoint is required, 1024 characters at most and no control character.", out error);
        if (!root.TryGetProperty("label", out var value))
            return Refuse("A label needs a label value; send null to remove it.", out error);
        if (!TryReadLabel(value, out var label))
            return Refuse("label must be 1 to 80 characters with no control character, or null to remove it.",
                out error);

        error = null;
        return new LabelWrite(service, endpoint, label);
    }

    // Trimmed first; nothing left, like null, removes the label.
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
        if (text.Length > MaxLabelChars || text.Any(char.IsControl))
            return false;
        label = text;
        return true;
    }

    private static LabelWrite? Refuse(string why, out string? error)
    {
        error = why;
        return null;
    }
}
