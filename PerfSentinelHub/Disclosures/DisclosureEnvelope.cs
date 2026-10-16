using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace PerfSentinelHub.Disclosures;

/// <summary>
///     What the Hub files about a published report, read from its JSON before the engine
///     sees it: the identity, the period, the confidentiality the internal check needs, and
///     the few figures a card shows, kept so a listing never reads the report itself. The
///     engine checks the rest.
/// </summary>
public sealed record DisclosureEnvelope(
    string SchemaVersion,
    string ReportUuid,
    string Intent,
    string Confidentiality,
    string? IntegrityLevel,
    string Organisation,
    string PeriodType,
    string PeriodFrom,
    string PeriodTo,
    string ContentHash,
    string FiguresJson)
{
    private const int MaxText = 512;
    private const int MaxApplications = 50;

    public bool Internal => Confidentiality == "internal";

    public static bool TryParse(ReadOnlyMemory<byte> report, [NotNullWhen(true)] out DisclosureEnvelope? envelope)
    {
        envelope = null;
        try
        {
            using var document = JsonDocument.Parse(report, new JsonDocumentOptions { MaxDepth = 64 });
            envelope = Read(document.RootElement);
            return envelope is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DisclosureEnvelope? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var metadata = Child(root, "report_metadata");
        var period = Child(root, "period");
        if (Text(root, "schema_version") is not { } schema || !schema.StartsWith("perf-sentinel-report/", StringComparison.Ordinal) ||
            Text(metadata, "report_uuid") is not { } uuid ||
            Text(metadata, "intent") is not { } intent ||
            // The schema has two levels: anything else would pass the internal check as public.
            Text(metadata, "confidentiality_level") is not { } confidentiality || confidentiality is not ("public" or "internal") ||
            Text(Child(root, "organisation"), "name") is not { } organisation ||
            Text(period, "period_type") is not { } periodType ||
            Text(period, "from_date") is not { } from || !IsDate(from) ||
            Text(period, "to_date") is not { } to || !IsDate(to) ||
            Text(Child(root, "integrity"), "content_hash") is not { } contentHash)
            return null;
        return new DisclosureEnvelope(
            schema, uuid, intent, confidentiality, Text(metadata, "integrity_level"), organisation, periodType, from, to,
            contentHash, Figures(root));
    }

    // What a card draws: totals, the canonical avoidable share (the older top-level ratio
    // when a report predates the split), runtime coverage, and the applications' energy.
    private static string Figures(JsonElement root)
    {
        var aggregate = Child(root, "aggregate");
        var scope = Child(root, "scope_manifest");
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteNumber(writer, "energy_kwh", Number(aggregate, "total_energy_kwh"));
            WriteNumber(writer, "carbon_kgco2eq", Number(aggregate, "total_carbon_kgco2eq"));
            WriteNumber(writer, "avoidable_ratio",
                Number(Child(aggregate, "canonical_waste"), "waste_ratio") ?? Number(aggregate, "aggregate_waste_ratio"));
            WriteNumber(writer, "coverage", Number(aggregate, "period_coverage"));
            WriteNumber(writer, "applications_declared", Number(scope, "total_applications_declared"));
            WriteNumber(writer, "applications_measured", Number(scope, "applications_measured"));
            writer.WriteStartArray("applications");
            if (root.TryGetProperty("applications", out var applications) && applications.ValueKind == JsonValueKind.Array)
                foreach (var application in applications.EnumerateArray().Take(MaxApplications))
                {
                    if (application.ValueKind != JsonValueKind.Object ||
                        (Text(application, "display_name") ?? Text(application, "service_name")) is not { } name)
                        continue;
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    WriteNumber(writer, "energy_kwh", Number(application, "energy_kwh"));
                    writer.WriteEndObject();
                }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static JsonElement Child(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) &&
               child.ValueKind == JsonValueKind.Object
            ? child
            : default;
    }

    private static string? Text(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= MaxText } text &&
               !text.Any(char.IsControl)
            ? text
            : null;
    }

    private static bool IsDate(string text)
    {
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static double? Number(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : null;
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
            writer.WriteNumber(name, number);
        else
            writer.WriteNull(name);
    }
}
