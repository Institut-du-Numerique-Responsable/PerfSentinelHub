using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Disclosures;

/// <summary>
///     What a webhook receives: a small JSON body and an HMAC-SHA256 of the timestamp and
///     that body under the webhook's secret, so a receiver checks both the sender and the age.
/// </summary>
public static class WebhookDelivery
{
    public const string TimestampHeader = "X-PerfSentinel-Timestamp";
    public const string SignatureHeader = "X-PerfSentinel-Signature";

    /// <summary><c>sha256=</c> and the lowercase hex HMAC-SHA256 of <c>"&lt;timestamp&gt;.&lt;body&gt;"</c>.</summary>
    public static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    /// <summary>
    ///     Posts a signed body and answers the status, or null when nothing answered within the timeout.
    ///     A redirect is an answer and is never followed by the client it is given.
    /// </summary>
    internal static async Task<int?> PostAsync(
        HttpClient client,
        Uri url,
        string secret,
        byte[] body,
        TimeProvider timeProvider,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(SignatureHeader, Sign(secret, timestamp, body));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(request, limit.Token);
            return (int)response.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException ||
                                          (exception is OperationCanceledException &&
                                           !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    public static byte[] Body(DisclosureSummary disclosure, string eventName, string? shareUrl)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("event", eventName);
            writer.WriteString("disclosure_id", disclosure.Id);
            writer.WriteString("organisation", disclosure.Organisation);
            writer.WriteStartObject("period");
            writer.WriteString("type", disclosure.PeriodType);
            writer.WriteString("from", disclosure.PeriodFrom);
            writer.WriteString("to", disclosure.PeriodTo);
            writer.WriteEndObject();
            writer.WriteString("content_hash", disclosure.ContentHash);
            writer.WriteString("intent", disclosure.Intent);
            writer.WriteString("integrity_level", disclosure.IntegrityLevel);
            writer.WriteString("share_url", shareUrl);
            writer.WriteEndObject();
        }

        return [.. buffer.WrittenSpan];
    }
}
