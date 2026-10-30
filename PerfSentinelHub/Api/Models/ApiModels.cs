using System.Text.Json;
using System.Text.Json.Serialization;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Api;

public sealed record StatusResponse(
    string Service,
    string Version,
    // Who the reverse proxy says is calling. Null when no proxy set the header,
    // which is the whole truth the Hub has: it never verifies the claim.
    string? Identity,
    string? EngineVersion,
    // The newest published release of each product, or null when the check is
    // off, has not run yet, could not reach GitHub, or found no release at all.
    // The Hub states them and lets the page decide what they mean.
    string? LatestEngineVersion,
    string? LatestHubVersion,
    int QueueDepth,
    int Workers,
    StatusLimits Limits,
    // The detection thresholds a run may override, with the engine's own
    // defaults and the bounds a submission is held to.
    IReadOnlyList<DetectionKnob> DetectionKnobs,
    // True when disclosures are on and the caller may read them, which is when the launcher
    // shows the tab. Omitted otherwise, so a Hub without disclosures answers as it did.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Disclosures = null);

/// <summary>
///     What a run costs, reported rather than assumed: the launcher shows these
///     four figures next to the button instead of guessing them. The read
///     limit is the operator's ceiling on a listing page, so the launcher sizes
///     its pages under it instead of learning it from a 400.
/// </summary>
public sealed record StatusLimits(
    int MaxTracesCap,
    int AnalysisTimeoutSeconds,
    int ReportRetentionHours,
    int MaxTracesEmbedded,
    int MaxReadLimit);

/// <summary>
///     One detection threshold a run may override. `Kind` is `integer`, `decimal`
///     or `choice`: the first two carry bounds and a numeric default, the last
///     carries its choices and a string default. `Default` is the engine's own
///     value as JSON, relayed rather than typed.
/// </summary>
public sealed record DetectionKnob(
    string Name,
    string Kind,
    double? Min,
    double? Max,
    JsonElement Default,
    IReadOnlyList<string>? Choices);

/// <summary>
///     A configured source joined to its last known collection state. The
///     timestamps are null for a source that has never been observed, so the
///     launcher can say "never polled" instead of showing the epoch.
/// </summary>
public sealed record SourceResponse(
    string Id,
    string Name,
    string Environment,
    string Kind,
    // Declared in the Hub's configuration, null when nobody did.
    int? RetentionHours,
    bool Reachable,
    long? LastAttemptMs,
    long? LastSuccessMs,
    long? UnreachableSinceMs,
    string? ProducerVersion,
    string? LastErrorCode,
    // The endpoint a printed command targets: the source's PublicUrl when one is
    // declared, its BaseUrl otherwise, spelled the way the Hub passes an endpoint
    // to the engine. Published rather than rebuilt in the page so the two
    // spellings stay the same bytes. The wire name predates PublicUrl.
    string BaseUrl,
    // The engine subcommand for this kind, null for a daemon: a daemon is not
    // queried, it is read.
    string? EngineSubcommand,
    // The header's name, never its value. Without it the note cannot say what
    // belongs in the variable, which the engine expects as "Name: Value". The
    // one the published address asks for, like BaseUrl above.
    string? AuthHeaderName,
    // What the last incidents read of this daemon came to: ok, absent (no such
    // route, or the store is off), unauthorized, error. Null when none has run.
    string? IncidentsState,
    // When that read was taken, on the Hub's clock. Null alongside the state
    // when none has run, which the incidents screen says as "never read": the
    // age of the copy is what tells a quiet fleet from a stale one.
    long? IncidentsReadMs,
    // The same two for the last read of this daemon's acks, which has one more
    // state: truncated, a listing at the daemon's cap whose tail is missing.
    string? AcksState,
    long? AcksReadMs,
    // Whether the Hub holds a credential to write acks to this daemon. Neither
    // its header nor its value is published: no printed command ever acks.
    bool AckRelay,
    // Signatures this daemon first saw in the last hour, null while the Hub has not
    // judged it: see SignatureRate. The warning is that count past
    // Hub:NewSignaturesPerHourWarning.
    int? NewSignaturesLastHour,
    bool SignatureRateWarning,
    // What the caller may do at this source, with Hub:Access:Enabled. Null and so
    // absent with access off, which leaves the payload as it was.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Permissions = null);

public sealed record ImportResponse(int Accepted, int Rejected);

// Watch is the C1 list: the signatures the Hub asks the pushing daemon to count passes
// below the threshold for, empty when none.
public sealed record DetectionImportResponse(int Accepted, int Rejected, IReadOnlyList<string> Watch);

// SourceIds is the resolved scope, as on IncidentQuery below. FromMs and ToMs
// bound the observation window in epoch milliseconds, each side open when null.
// Signature is an exact match. Access null reads every copy, as with access off;
// otherwise only the copies its patterns read.
public sealed record FindingQuery(
    string? Service,
    string? FindingType,
    string? Severity,
    int Limit,
    bool IncludeAcked = true,
    string? Status = null,
    int Offset = 0,
    IReadOnlyList<string>? SourceIds = null,
    long? FromMs = null,
    long? ToMs = null,
    string? Signature = null,
    string? Grouping = null,
    AccessScope? Access = null);

// SourceIds is the resolved set: one id from source_id, every id of an
// environment from environment, null for the whole fleet.
public sealed record IncidentQuery(
    string? Service,
    string? Namespace,
    string? Kind,
    IReadOnlyList<string>? SourceIds,
    int Offset,
    int Limit,
    AccessScope? Access = null);

// SourceIds is the resolved scope, as on IncidentQuery. FromMs and ToMs keep the episodes
// whose activity overlaps the span, bounds included, a running one reaching every later
// moment. NowMs dates the stale rule. Access null reads every episode, as with access off.
public sealed record CorrelationQuery(
    string? Service,
    string? PairKey,
    IReadOnlyList<string>? SourceIds,
    long? FromMs,
    long? ToMs,
    bool Active,
    long NowMs,
    int Offset,
    int Limit,
    AccessScope? Access = null);

public sealed record SubmittedAnalysis(string Id, string Status);

public sealed record AnalysisProblem(string Detail);

/// <summary>A publication the Hub refused, with the reason from the closed set.</summary>
public sealed record DisclosureRefusal(string Reason);

/// <summary>A publication the Hub took: its id, whether it rendered, and what was checked here.</summary>
public sealed record DisclosurePeriod(string Type, string From, string To);

public sealed record DisclosureWithdrawal(long AtMs, string? Reason, string? By);

public sealed record DisclosureFilesPresent(bool Report, bool Attestation, bool Bundle, bool Pdf);

/// <summary>One disclosure as the list and the detail show it, its files named, never carried.</summary>
public sealed record DisclosureItem(
    string Id,
    string ReportUuid,
    string Organisation,
    DisclosurePeriod Period,
    string Intent,
    string Confidentiality,
    string ContentHash,
    string? IntegrityLevel,
    JsonElement? Figures,
    DisclosureFilesPresent Files,
    string Pdf,
    string? RenderReason,
    long PublishedAtMs,
    long? SignatureAddedAtMs,
    DisclosureWithdrawal? Withdrawn);

/// <summary>
///     Who checked what. The Hub checks the content hash and the binding with its engine, and
///     never the signature, which it only reports as declared or absent.
/// </summary>
public sealed record DisclosureChecks(string ContentHash, string Binding, string Signature, string? EngineVersion);

/// <summary>A share link without its token, which was shown once and is stored as a hash.</summary>
public sealed record ShareLinkView(
    string Id,
    string State,
    string CreatedBy,
    long CreatedAtMs,
    long ExpiresAtMs,
    long? RevokedAtMs,
    string? RevokedBy,
    long Downloads,
    long? LastUsedAtMs);

/// <summary>A webhook delivery by the webhook's name: its URL carries a secret and is never shown.</summary>
public sealed record DeliveryView(
    string Id,
    string Webhook,
    string Event,
    string State,
    int Attempts,
    long? LastAttemptMs,
    long? NextAttemptMs,
    int? LastStatus);

public sealed record DisclosureDetail(
    DisclosureItem Disclosure,
    DisclosureChecks Checks,
    IReadOnlyList<ShareLinkView> ShareLinks,
    IReadOnlyList<DeliveryView> Deliveries,
    string? ExpectedIdentity,
    string? ExpectedIssuer,
    bool CanManage);

/// <summary>A new share link, its token shown this once.</summary>
public sealed record ShareLinkCreated(string Id, string Token, string Path, long ExpiresAtMs);

public sealed record DisclosureImportResponse(
    string Id,
    string Pdf,
    string? Reason,
    bool ContentHashChecked,
    bool? BindingChecked);

/// <summary>
///     One entry of GET /api/sources/{id}/acks: a daemon ack as the mirror holds it, in
///     the shape the daemon's own GET /api/acks lists, which a report reads either way.
/// </summary>
public sealed record SourceAckResponse(string Signature, string By, string? Reason, string At, string? ExpiresAt);

/// <summary>An action on a finding as POST and PATCH /api/actions answer it: who works on it and where it stands.</summary>
public sealed record ActionResponse(
    string Signature,
    string Status,
    string? Owner,
    string? OwnerDisplay,
    string CreatedBy,
    long CreatedAtMs,
    long UpdatedAtMs,
    long? DoneAtMs);

/// <summary>One delivery of a frozen recap report: where it goes and how far it has got.</summary>
public sealed record RecapDeliveryView(
    string Channel,
    string Target,
    string State,
    int Attempts,
    long? LastAttemptMs,
    long? NextAttemptMs,
    int? LastStatus);

public sealed record RecapPeriodRef(string Kind, string From, string To);

/// <summary>A frozen recap report without its snapshot. Subscription is null for a report somebody asked for.</summary>
public sealed record RecapReportItem(
    string Id,
    string? Subscription,
    RecapPeriodRef Period,
    IReadOnlyList<string> Views,
    bool Complete,
    bool NothingMoved,
    string? CreatedBy,
    long GeneratedAtMs,
    IReadOnlyList<RecapDeliveryView> Deliveries);

/// <summary>A report and its snapshot, the stored text served as it was frozen.</summary>
public sealed record RecapReportDetail(RecapReportItem Report, JsonElement Snapshot);

/// <summary>
///     GET /api/access, with Hub:Access:Enabled only: who the caller is and what they
///     reach. Configuration, Unmatched and Conflicts are null except for an admin. Never
///     a machine token's value or fingerprint.
/// </summary>
public sealed record AccessResponse(
    string? Identity,
    string Subject,
    string Caller,
    long? ExpiresAtMs,
    bool Admin,
    IReadOnlyList<AccessGroup> Groups,
    IReadOnlyList<AccessReach> Reach,
    IReadOnlyList<ProjectOptions> Projects,
    AccessConfiguration? Configuration,
    AccessUnmatched? Unmatched,
    AccessConflicts? Conflicts);

// Origin is provider or internal; an internal group is named without its prefix.
public sealed record AccessGroup(string Name, string Origin);

// Group null is Admins, which names the subject rather than a group.
public sealed record AccessReach(string? Group, string Project, string Role, IReadOnlyList<string> Permissions);

public sealed record AccessConfiguration(
    IReadOnlyList<ProjectOptions> Projects,
    IReadOnlyList<AccessRole> Roles,
    IReadOnlyList<GrantOptions> Grants,
    IReadOnlyList<InternalGroupOptions> InternalGroups,
    IReadOnlyList<string> Admins,
    IReadOnlyList<string> AdminGroups,
    IReadOnlyList<AccessMachineToken> MachineTokens,
    IReadOnlyList<string> LocalAccounts);

public sealed record AccessRole(string Name, IReadOnlyList<string> Permissions, bool BuiltIn);

// Active means not expired.
public sealed record AccessMachineToken(string Name, string Group, long ExpiresAtMs, bool Active);

public sealed record AccessUnmatched(
    IReadOnlyList<UnmatchedService> Services,
    IReadOnlyList<UnmatchedAttribute> Attributes,
    IReadOnlyList<UnmatchedSource> Sources,
    bool Truncated);

// Findings counts copies: one per (signature, source).
public sealed record UnmatchedService(string? Service, IReadOnlyList<string> SourceIds, int Findings);

public sealed record UnmatchedAttribute(string Key, string Value, int Findings);

// Findings is null for a trace backend, which holds no finding.
public sealed record UnmatchedSource(string SourceId, int? Findings);

public sealed record AccessConflicts(IReadOnlyList<AccessConflict> Copies, bool Truncated);

public sealed record AccessConflict(
    string Signature,
    string SourceId,
    string? Service,
    string Key,
    IReadOnlyList<string> Values,
    int IdentitiesWithoutKey);

// RFC 6749 section 5.2: the device flow's and the revocation's errors.
public sealed record OAuthError(string Error);

// RFC 8628 section 3.2, without verification_uri_complete: the code is always typed.
public sealed record DeviceAuthorizationResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int Interval);

// No refresh token: a new one takes a new sign-in.
public sealed record DeviceTokenResponse(string AccessToken, string TokenType, long ExpiresIn);

// What the approval page shows before a click: where and when the code was asked for.
public sealed record DeviceVerifyResponse(
    string UserCode,
    string ClientAddress,
    bool SameNetwork,
    long RequestedAtMs,
    string? Identity,
    long? TokenExpiresAtMs);

// A terminal token as GET /api/tokens lists it: never the token, nor its hash.
public sealed record TerminalTokenResponse(
    string Id,
    string Identity,
    string Subject,
    string? ClientAddress,
    long CreatedAtMs,
    long ExpiresAtMs,
    long? LastUsedAtMs);
