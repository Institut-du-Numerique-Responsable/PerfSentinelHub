using System.Text.Json.Serialization;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Cards;

public enum CardRevokeOutcome
{
    Revoked,
    Unknown,
    BadDate
}

/// <summary>The engine could not serve a card request: <c>unsupported</c>, <c>timeout</c> or <c>engine_contract</c>.</summary>
public sealed class CardEngineException(string reason) : Exception($"The engine could not serve the card request: {reason}.")
{
    public string Reason { get; } = reason;
}

/// <summary>One signing key as the administrator publishes it: dates as UTC days, the DNS line ready to paste.</summary>
public sealed record CardKeyItem(
    string Fingerprint,
    string PublicKey,
    string From,
    string? To,
    string? Revoked,
    bool Current,
    string Dns);

/// <summary>Every key, oldest first, with the record name (null without a domain) and the .well-known file.</summary>
public sealed record CardKeysResponse(string? RecordName, IReadOnlyList<CardKeyItem> Keys, string WellKnown);

/// <summary>The engine's card.json (C2): snake_case, integers only, dates as UTC yyyy-MM-dd, absent values omitted.</summary>
public sealed record CardInput(
    string Angle,
    string Lang,
    CardPeriod Period,
    CardScope Scope,
    CardFiguresBlock Figures,
    CardStory? Story,
    CardDayRange? BeforeFixPeriod,
    CardFree Free,
    CardOrg Org,
    long GeneratedAtMs);

public sealed record CardPeriod(string Kind, string From, string To);

/// <summary>Calendar bounds of one period; its kind is always the card's.</summary>
public sealed record CardDayRange(string From, string To);

public sealed record CardScope(
    string? Environment,
    IReadOnlyList<string> Sources,
    int CoveredDays,
    int RestartDays,
    int SampledPermille);

public sealed record CardStory(string FindingType, string Service, string Endpoint, string? ObservedAt, string DoneAt);

public sealed record CardFree(string? Title, string? Team, string? Label);

public sealed record CardOrg(string Name, string? Domain);

/// <summary>Every figure key of the three angles; each angle fills its own, the rest stay null and are omitted.</summary>
public sealed record CardFiguresBlock(
    long? CitedOps = null,
    long? CitedMs = null,
    long? BeforeFixOps = null,
    long? ThresholdOps = null,
    long? ThresholdMs = null,
    long? Co2LowUg = null,
    IReadOnlyList<long>? WastePermille = null,
    long? AvoidableOps = null,
    long? ConfirmedFixesCount = null,
    long? PreviousFixesCount = null,
    long? ServicesCount = null,
    CardFamilies? FixesByFamilyCount = null);

public sealed record CardFamilies(int ExtraCalls, int SlowCalls, int CallArchitecture);

/// <summary>The volume a story's fix removed over the period before its done day; Hub-internal.</summary>
public sealed record CardBefore(RecapPeriod Period, long? Ops, long? Co2LowUg);

/// <summary>One period of the curve; only its ratio is signed, the dates and totals stay in the Hub.</summary>
public sealed record CardPoint(string From, string To, long WastePermille, long AvoidableOps, long TotalOps);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CardInput))]
public sealed partial class CardJsonContext : JsonSerializerContext;
