using System.Globalization;
using System.Text.Json;
using PerfSentinelHub.Cards;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Api;

/// <summary>
///     What a caller asks of POST /api/cards/render and /api/cards/attachment: an angle over a closed
///     period, its scope filters, and the three free fields. Every figure comes from the Hub, so any
///     other key is refused. The last closed period when no date is given.
/// </summary>
public sealed record CardRequest(
    string Angle,
    RecapPeriodKind Kind,
    DateOnly At,
    string? Environment,
    string? SourceId,
    string? Signature,
    CardFree Free,
    string Lang,
    string Format,
    string? EquivalenceCountry = null)
{
    private const int MaxFilterChars = 256;

    // The engine's bounds, so a value the Hub accepts never fails the render.
    private const int MaxTitleChars = 48;
    private const int MaxTeamChars = 48;

    // The previous period the curve and the collective read, which the calendar cannot give year 1.
    private static readonly DateOnly Earliest = new(1900, 1, 1);

    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "angle", "period", "at", "environment", "source_id", "signature", "title", "team", "label", "lang",
        "equivalence_country", "format"
    };

    private static readonly string[] Angles = ["story", "curve", "collective"];

    public static CardRequest? TryParse(ReadOnlyMemory<byte> body, DateOnly today, out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return TryRead(document.RootElement, today, out error);
        }
        // The second is an unpaired surrogate, a string the reader cannot hand over.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Refuse("The body is not valid JSON.", out error);
        }
    }

    /// <summary>
    ///     The GET options query: period, at, environment, source_id. A period still running is read,
    ///     so the screen shows why it cannot make a card of it before anyone asks for one.
    /// </summary>
    public static bool TryParseOptionsQuery(
        HttpRequest request,
        DateOnly today,
        out (RecapPeriodKind Kind, DateOnly At, string? Environment, string? SourceId) query)
    {
        query = default;
        if (request.Query.Any(item => item.Value.Count != 1) ||
            Optional(request, "period") is not { } name ||
            !RecapPeriod.TryParseCardKind(name, out var kind))
            return false;

        var at = RecapPeriod.Of(kind, today).Previous().From;
        if (Optional(request, "at") is { } raw && (!TryReadDay(raw, out at) || at > today))
            return false;
        query = (kind, at, Optional(request, "environment"), Optional(request, "source_id"));
        return true;
    }

    private static CardRequest? TryRead(JsonElement root, DateOnly today, out string? error)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Refuse("The body must be a JSON object.", out error);
        if (root.EnumerateObject().Any(property => !Keys.Contains(property.Name)))
            return Refuse(
                "Only angle, period, at, environment, source_id, signature, title, team, label, lang, " +
                "equivalence_country and format may be sent: the figures come from the Hub.", out error);
        if (Choice(root, "angle", null, Angles) is not { } angle)
            return Refuse("angle must be story, curve or collective.", out error);
        if (JsonRead.ReadString(root, "period") is not { } name || !RecapPeriod.TryParseCardKind(name, out var kind))
            return Refuse("period must be week, month, quarter, semester or year.", out error);

        var at = RecapPeriod.Of(kind, today).Previous().From;
        if (root.TryGetProperty("at", out var value) &&
            (value.ValueKind != JsonValueKind.String || !TryReadDay(value.GetString()!, out at)))
            return Refuse("at must be a date, yyyy-MM-dd.", out error);
        // A date after today first: the end of a period near 9999 would not fit the calendar.
        if (at > today || RecapPeriod.Of(kind, at).To >= today)
            return Refuse("A card covers a closed period. Pick an earlier one.", out error);

        return TryReadRest(root, angle, kind, at, out error);
    }

    private static CardRequest? TryReadRest(
        JsonElement root,
        string angle,
        RecapPeriodKind kind,
        DateOnly at,
        out string? error)
    {
        if (!TryReadText(root, "environment", MaxFilterChars, out var environment) ||
            !TryReadText(root, "source_id", MaxFilterChars, out var sourceId) ||
            !TryReadText(root, "signature", AckParser.MaxSignatureLength, out var signature))
            return Refuse("environment, source_id and signature must be strings with no control character.", out error);
        if (TryReadFree(root, out error) is not { } free)
            return null;
        if (angle != "story" && (signature is not null || free.Label is not null))
            return Refuse("signature and label belong to a story card.", out error);
        if (Choice(root, "lang", "en", "en", "fr") is not { } lang)
            return Refuse("lang must be en or fr.", out error);
        if (Choice(root, "format", "png", "png", "svg", "jxl", "pdf") is not { } format)
            return Refuse("format must be png, svg, jxl or pdf.", out error);
        // Absent is the Hub's own country; present, it must be one the engine knows.
        var country = Choice(root, "equivalence_country", null, HubOptionsValidator.CardCountries);
        if (country is null && root.TryGetProperty("equivalence_country", out _))
            return Refuse("equivalence_country must be fr, gb or us.", out error);

        error = null;
        return new CardRequest(angle, kind, at, environment, sourceId, signature, free, lang, format, country);
    }

    private static CardFree? TryReadFree(JsonElement root, out string? error)
    {
        var texts = new string?[3];
        var fields = new[] { ("title", MaxTitleChars), ("team", MaxTeamChars), ("label", LabelWrite.MaxLabelChars) };
        for (var i = 0; i < fields.Length; i++)
        {
            var (name, maxChars) = fields[i];
            if (TryReadFreeText(root, name, maxChars, out texts[i]))
                continue;
            var detail = Holds(root, name, CardRules.EnvelopeMarker)
                ? $"{name} cannot contain {CardRules.EnvelopeMarker}, the text the card file keeps for its signature."
                : $"{name} must be 1 to {maxChars} characters with no control or format character.";
            return Refuse<CardFree>(detail, out error);
        }

        error = null;
        return new CardFree(texts[0], texts[1], texts[2]);
    }

    // Trimmed; absent, null or blank reads as absent. The rest follows CardRules.Printable, as the figures'
    // names do: a format character would make the card read other than it is signed.
    private static bool TryReadFreeText(JsonElement root, string name, int maxChars, out string? text)
    {
        text = null;
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        var trimmed = value.GetString()!.Trim();
        if (trimmed.Length > 0 && !CardRules.Printable(trimmed, maxChars))
            return false;
        text = trimmed.Length == 0 ? null : trimmed;
        return true;
    }

    private static bool Holds(JsonElement root, string name, string part)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
               value.GetString()!.Contains(part, StringComparison.Ordinal);
    }

    // Absent or null is no filter; anything else must be a bounded string.
    private static bool TryReadText(JsonElement root, string name, int maxChars, out string? text)
    {
        text = null;
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return true;
        text = AckWrite.ReadText(root, name, maxChars);
        return text is not null;
    }

    // The fallback when absent; null when present and not one of the choices.
    private static string? Choice(JsonElement root, string name, string? fallback, params string[] choices)
    {
        if (!root.TryGetProperty(name, out var value))
            return fallback;
        return value.ValueKind == JsonValueKind.String && choices.Contains(value.GetString(), StringComparer.Ordinal)
            ? value.GetString()
            : null;
    }

    private static bool TryReadDay(string raw, out DateOnly day)
    {
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                   out day) && day >= Earliest;
    }

    // A blank value reads as absent, as the other query routes read it.
    private static string? Optional(HttpRequest request, string name)
    {
        return request.Query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value[0])
            ? value[0]
            : null;
    }

    private static CardRequest? Refuse(string why, out string? error)
    {
        return Refuse<CardRequest>(why, out error);
    }

    private static T? Refuse<T>(string why, out string? error) where T : class
    {
        error = why;
        return null;
    }
}
