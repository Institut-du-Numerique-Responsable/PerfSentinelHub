using System.Globalization;
using System.Text;
using System.Text.Json;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Cards;

/// <summary>
///     The C3 lines an administrator publishes so a card's key can be checked: one DNS TXT record per
///     key and the <c>/.well-known/perf-sentinel-key</c> file, both oldest key first.
/// </summary>
public static class CardKeyLines
{
    public static string Dns(StoredCardKey key)
    {
        var line = new StringBuilder($"v=ps1; k={key.Fingerprint}; from={Day(key.CreatedAtMs)}");
        if (key.RetiredAtMs is { } to)
            line.Append(CultureInfo.InvariantCulture, $"; to={Day(to)}");
        if (key.RevokedOn is { } revoked)
            line.Append(CultureInfo.InvariantCulture, $"; revoked={revoked}");
        return line.ToString();
    }

    // Indented with \n line ends: a file the administrator opens, rereads and publishes as shown.
    public static string WellKnown(IReadOnlyList<StoredCardKey> keys)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("keys");
            foreach (var key in keys)
            {
                writer.WriteStartObject();
                writer.WriteString("fingerprint", key.Fingerprint);
                writer.WriteString("public_key", key.PublicKey);
                writer.WriteString("from", Day(key.CreatedAtMs));
                if (key.RetiredAtMs is { } to)
                    writer.WriteString("to", Day(to));
                if (key.RevokedOn is { } revoked)
                    writer.WriteString("revoked", revoked);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string? RecordName(string? domain)
    {
        return domain is null ? null : $"_perf-sentinel.{domain}";
    }

    /// <summary>The UTC day of an instant, as <c>yyyy-MM-dd</c>.</summary>
    public static string Day(long atMs)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(atMs).UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
