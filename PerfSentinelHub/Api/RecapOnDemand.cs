using System.Globalization;
using System.Text.Json;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Api;

/// <summary>
///     What a caller asks of POST /api/recap/reports: a closed period, named by its kind and by any date
///     inside it, and the filters the recap takes. The last closed period when no date is given.
/// </summary>
public sealed record RecapOnDemand(
    RecapPeriodKind Kind,
    DateOnly At,
    string? Environment,
    string? SourceId,
    string? Service,
    string? Grouping)
{
    private const int MaxChars = 256;

    // The recap reads the period before the one asked for, which the calendar cannot give year 1.
    private static readonly DateOnly Earliest = new(1900, 1, 1);

    private static readonly string[] Filters = ["environment", "source_id", "service", "grouping"];

    public static RecapOnDemand? TryParse(ReadOnlyMemory<byte> body, DateOnly today, out string? error)
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

    private static RecapOnDemand? TryRead(JsonElement root, DateOnly today, out string? error)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Refuse("The body must be a JSON object.", out error);
        if (!root.TryGetProperty("period", out var period) ||
            period.ValueKind != JsonValueKind.String ||
            !RecapPeriod.TryParseKind(period.GetString()!, out var kind))
            return Refuse("period must be day, week or month.", out error);
        if (!TryReadDate(root, kind, today, out var at))
            return Refuse("at must be a date, yyyy-MM-dd.", out error);
        // A period still running, or not begun, has nothing frozen to report.
        if (at > today || RecapPeriod.Of(kind, at).To >= today)
            return Refuse("A report covers a closed period; pick a date in an earlier one.", out error);

        var filters = new string?[Filters.Length];
        for (var i = 0; i < Filters.Length; i++)
            if (!TryReadText(root, Filters[i], out filters[i]))
                return Refuse($"{Filters[i]} must be 1 to {MaxChars} characters with no control character.", out error);

        error = null;
        return new RecapOnDemand(kind, at, filters[0], filters[1], filters[2], filters[3]);
    }

    private static bool TryReadDate(JsonElement root, RecapPeriodKind kind, DateOnly today, out DateOnly at)
    {
        at = RecapPeriod.Of(kind, today).Previous().From;
        if (!root.TryGetProperty("at", out var value))
            return true;
        return value.ValueKind == JsonValueKind.String &&
               DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out at) &&
               at >= Earliest;
    }

    // Absent or null is no filter; anything else must be a bounded string.
    private static bool TryReadText(JsonElement root, string name, out string? text)
    {
        text = null;
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return true;
        text = AckWrite.ReadText(root, name, MaxChars);
        return text is not null;
    }

    private static RecapOnDemand? Refuse(string why, out string? error)
    {
        error = why;
        return null;
    }
}
