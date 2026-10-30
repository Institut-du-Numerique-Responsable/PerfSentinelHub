using System.Text.Json.Serialization;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
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

/// <summary>What a share card cites of a finding as it stood when its action moved to done.</summary>
public sealed record FrozenFinding(
    string FindingType,
    string Service,
    string Endpoint,
    long? Occurrences,
    long? P99Us,
    long? ObservedAtMs);

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

/// <summary>
///     The perimeter a card is asked for: the candidate sources (both scopes reach them), those
///     both scopes hold whole (the curve's), the environment filter, the findings.read scope
///     (null = unrestricted) and the configured sources, for their names.
/// </summary>
public sealed record CardScopeAsk(
    RecapPeriodKind Kind,
    DateOnly At,
    IReadOnlyList<string> SourceIds,
    IReadOnlyList<string> WholeSourceIds,
    string? Environment,
    AccessScope? Reads,
    IReadOnlyList<SourceOptions> Sources);

public sealed record CardAsk(CardScopeAsk Scope, string Angle, string Lang, string? Signature, CardFree Free);

public abstract record CardOutcome
{
    public sealed record Ready(CardInput Input) : CardOutcome;

    public sealed record Refused(string Reason) : CardOutcome;
}

/// <summary>
///     What the screen shows before anyone signs: the period's own refusal and its sentence, the
///     signed scope, whether a collective card keeps its comparison, and the fixes a story can tell.
/// </summary>
public sealed record CardOptions(
    CardPeriod Period,
    string PeriodName,
    string? Refusal,
    string? RefusalText,
    int RestartDays,
    int SampledPermille,
    string? Environment,
    IReadOnlyList<string> Sources,
    bool PreviousComplete,
    IReadOnlyList<CardFix> Fixes,
    string? DefaultSignature,
    CardOrg Org);

public sealed record CardFix(
    string Signature,
    string FindingType,
    string Service,
    string Endpoint,
    string Plain,
    long DoneAtMs,
    long? BeforeFixOps,
    string? BeforePeriodName,
    string? Label);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CardInput))]
public sealed partial class CardJsonContext : JsonSerializerContext;

/// <summary>What card render made of a card.json: the file and its compact URL, or why not.</summary>
public abstract record CardRender
{
    public sealed record Rendered(byte[] Bytes, string CompactUrl) : CardRender;

    /// <summary>The engine's reason, and the card.json path of the text it could not draw when it names one.</summary>
    public sealed record Refused(string Reason, string? Field) : CardRender;

    public sealed record Malformed : CardRender;

    public sealed record Timeout : CardRender;

    public sealed record Unsupported : CardRender;
}

/// <summary>verify-card's verdict on a fresh card, and the hosts the check reached.</summary>
public sealed record CardAttachmentResponse(
    string Integrity,
    string Content,
    int Level,
    string? Domain,
    string Key,
    string? Reason,
    CardSignedFigures? Figures,
    IReadOnlyList<string> Contacted);

/// <summary>The card's big figures as the engine signed them, null where the angle has none.</summary>
public sealed record CardSignedFigures(
    string Angle,
    CardPeriod Period,
    CardDayRange? BeforeFixPeriod,
    long? CitedOps,
    long? CitedMs,
    long? BeforeFixOps,
    long? ThresholdOps,
    long? ThresholdMs,
    long? LastWastePermille,
    long? PreviousWastePermille,
    long? ConfirmedFixesCount,
    long? ServicesCount);

/// <summary>A refusal the screen shows under one free field: title, team or label.</summary>
public sealed record CardFieldProblem(string Detail, string Field);
