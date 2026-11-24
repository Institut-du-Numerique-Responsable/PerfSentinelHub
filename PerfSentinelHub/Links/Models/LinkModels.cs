using System.Text.Json.Serialization;

namespace PerfSentinelHub.Links;

[JsonConverter(typeof(JsonStringEnumConverter<LinkLevel>))]
public enum LinkLevel
{
    [JsonStringEnumMemberName("weak")] Weak = 1,
    [JsonStringEnumMemberName("medium")] Medium = 2,
    [JsonStringEnumMemberName("strong")] Strong = 3
}

/// <summary>Detection history of one signature over the 7 covered days before an incident; <c>CoveredDays == 0</c> means unknown.</summary>
public sealed record Baseline(int CoveredDays, int DaysSeen, long Detections);

/// <summary>Earlier incidents of the same environment, service and kind: <c>n</c> in total, <c>k</c> carrying the signature.</summary>
public sealed record Recurrence(int Earlier, int WithSignature);

public sealed record LinkInput(
    string IncidentKind,
    string FindingType,
    double? Saturation,
    // What the incident's frozen findings add up to, in the window and over the covered baseline days.
    long WindowTotal,
    long BaselineTotal,
    long SeenCount,
    bool AppearedAfterDeploy,
    Baseline Baseline,
    Recurrence Recurrence,
    bool SeenInBaselineDays = false);

public sealed record LinkReason(
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Ratio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? K = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? N = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? UsualShare = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Value = null);

public sealed record LinkResult(LinkLevel Level, IReadOnlyList<LinkReason> Reasons);
