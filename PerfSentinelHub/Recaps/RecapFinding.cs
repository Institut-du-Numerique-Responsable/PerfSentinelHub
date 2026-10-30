using System.Text.Json;
using PerfSentinelHub.Api;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Recaps;

/// <summary>One finding as the recap ranks and compares it: the envelope's words plus the period's volume.</summary>
public sealed record RecapFinding(
    string Signature,
    string FindingType,
    string Severity,
    string Service,
    string Endpoint,
    string? Template,
    string? Suggestion,
    RecapFix? SuggestedFix,
    RecapCodeLocation? CodeLocation,
    string Confidence,
    long? Detections,
    long? AvoidableIoOps,
    bool VolumeKnown,
    IReadOnlyList<string> SourceIds);

public static class RecapEnvelope
{
    /// <summary>Reads a stored envelope; a missing or mistyped string reads "" (null where optional), never throws.</summary>
    public static RecapFinding Read(StoredFinding finding)
    {
        using var document = JsonDocument.Parse(finding.EnvelopeJson);
        var body = Child(document.RootElement, "finding");
        var pattern = Child(body, "pattern");
        var fix = Child(body, "suggested_fix");
        var location = Child(body, "code_location");

        return new RecapFinding(
            finding.Signature,
            Text(body, "type"),
            Text(body, "severity"),
            Text(body, "service"),
            Text(body, "source_endpoint"),
            Optional(pattern, "template"),
            Optional(body, "suggestion"),
            Optional(fix, "recommendation") is { } recommendation
                ? new RecapFix(recommendation, Optional(fix, "framework"), Optional(fix, "reference_url"))
                : null,
            location is null
                ? null
                : new RecapCodeLocation(
                    Optional(location, "function"),
                    Optional(location, "filepath"),
                    ReadLine(location),
                    Optional(location, "namespace")),
            Text(body, "confidence"),
            finding.Volume?.Detections,
            finding.Volume?.AvoidableIoOps,
            finding.Volume?.Detections is not null,
            [.. finding.Sources.Select(source => source.SourceId)]);
    }

    private static JsonElement? Child(JsonElement? parent, string name)
    {
        return parent is { ValueKind: JsonValueKind.Object } element &&
               element.TryGetProperty(name, out var child) &&
               child.ValueKind == JsonValueKind.Object
            ? child
            : null;
    }

    private static string? Optional(JsonElement? parent, string name)
    {
        return parent is { ValueKind: JsonValueKind.Object } element ? JsonRead.ReadString(element, name) : null;
    }

    private static string Text(JsonElement? parent, string name)
    {
        return Optional(parent, name) ?? "";
    }

    private static int? ReadLine(JsonElement? location)
    {
        return location is { } element && JsonRead.ReadLong(element, "lineno") is >= 0 and <= int.MaxValue and var line
            ? (int)line
            : null;
    }
}
