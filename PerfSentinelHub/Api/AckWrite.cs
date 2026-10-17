using System.Text.Json;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Api;

/// <summary>
///     What a caller asks the relay to write at a daemon: an ack with its reason
///     and its optional expiry, or a revoke, which names the signature alone.
/// </summary>
public sealed record AckWrite(string Signature, bool Revoke, string? Reason, long? ExpiresAtMs)
{
    private const int MaxReasonChars = 1024;

    public static AckWrite? TryParse(ReadOnlyMemory<byte> payload, bool revoke, long nowMs, out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return TryRead(document.RootElement, revoke, nowMs, out error);
        }
        // The second is an unpaired surrogate, a string the reader cannot hand over.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Refuse("The body is not valid JSON.", out error);
        }
    }

    private static AckWrite? TryRead(JsonElement root, bool revoke, long nowMs, out string? error)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Refuse("The body must be a JSON object.", out error);
        // The shape of a signature is the daemon's rule. The Hub refuses what
        // would not fit its store, break a log line, or, for the two dot
        // segments, walk out of the daemon's ack path.
        if (ReadText(root, "signature", AckParser.MaxSignatureLength) is not { } signature ||
            signature is "." or "..")
            return Refuse("signature is required, 1024 characters at most and no control character.", out error);
        if (revoke)
            return Accept(new AckWrite(signature, true, null, null), out error);
        if (ReadText(root, "reason", MaxReasonChars) is not { } reason || string.IsNullOrWhiteSpace(reason))
            return Refuse("reason is required, 1024 characters at most and no control character.", out error);
        if (!AckParser.TryReadExpiry(root, out _, out var expiresAtMs) || expiresAtMs <= nowMs)
            return Refuse("expires_at must be an RFC 3339 time in the future.", out error);

        return Accept(new AckWrite(signature, false, reason, expiresAtMs), out error);
    }

    private static string? ReadText(JsonElement root, string name, int maxChars)
    {
        return JsonRead.ReadString(root, name) is { Length: > 0 } text &&
               text.Length <= maxChars &&
               !text.Any(char.IsControl)
            ? text
            : null;
    }

    private static AckWrite Accept(AckWrite write, out string? error)
    {
        error = null;
        return write;
    }

    private static AckWrite? Refuse(string why, out string? error)
    {
        error = why;
        return null;
    }
}
