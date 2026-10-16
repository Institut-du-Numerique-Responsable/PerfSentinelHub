using System.Text.Json;
using System.Text.Json.Serialization;
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
    IReadOnlyList<DetectionKnob> DetectionKnobs);

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
    // What the caller may do at this source, with Hub:Access:Enabled. Null and so
    // absent with access off, which leaves the payload as it was.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Permissions = null);

public sealed record ImportResponse(int Accepted, int Rejected);

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
