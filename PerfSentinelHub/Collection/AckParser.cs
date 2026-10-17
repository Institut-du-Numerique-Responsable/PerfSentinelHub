using System.Globalization;
using System.Text.Json;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Collection;

public static class AckParser
{
    // The shape of a signature is the daemon's rule. The Hub bounds what it
    // stores and refuses what would break a log line or a terminal.
    public const int MaxSignatureLength = 1024;
    private const int MaxByLength = 256;
    private const int MaxReasonLength = 1024;

    // Also the longest expiry accepted: an RFC 3339 timestamp is half of it.
    private const int MaxAtLength = 64;

    public static ParsedAckPage Parse(ReadOnlyMemory<byte> payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The acks response is not valid JSON.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The acks response must be a JSON array.");

            var acks = new List<ParsedAck>();
            var rejected = 0;
            foreach (var element in document.RootElement.EnumerateArray())
                if (TryParse(element, out var ack))
                    acks.Add(ack);
                else
                    rejected++;

            return new ParsedAckPage(acks, rejected);
        }
    }

    private static bool TryParse(JsonElement element, out ParsedAck ack)
    {
        ack = null!;
        if (element.ValueKind != JsonValueKind.Object ||
            JsonRead.ReadString(element, "action") != "ack" ||
            JsonRead.ReadString(element, "source") is not { } origin ||
            origin is not ("daemon" or "toml") ||
            JsonRead.ReadString(element, "signature") is not { Length: > 0 and <= MaxSignatureLength } signature ||
            signature.Any(char.IsControl) ||
            JsonRead.ReadString(element, "by") is not { } by ||
            JsonRead.ReadString(element, "at") is not { } at ||
            !TryReadExpiry(element, out var expiresAt, out var expiresAtMs))
            return false;

        ack = new ParsedAck(
            signature,
            origin,
            Truncate(by, MaxByLength),
            JsonRead.ReadString(element, "reason") is { } reason ? Truncate(reason, MaxReasonLength) : null,
            Truncate(at, MaxAtLength),
            expiresAt,
            expiresAtMs);
        return true;
    }

    // An absent or null expiry is a permanent ack. One that is present and
    // unreadable is refused rather than read as permanent. The relay reads the
    // expiry a caller asks for by the same rule.
    internal static bool TryReadExpiry(JsonElement element, out string? expiresAt, out long? expiresAtMs)
    {
        expiresAt = null;
        expiresAtMs = null;
        if (!element.TryGetProperty("expires_at", out var value) || value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: <= MaxAtLength } text ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                out var parsed))
            return false;

        expiresAt = text;
        expiresAtMs = parsed.ToUnixTimeMilliseconds();
        return true;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
