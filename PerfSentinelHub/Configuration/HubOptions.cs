using System.Net;
using Microsoft.Extensions.Options;

namespace PerfSentinelHub.Configuration;

public sealed record HubOptions
{
    public const string SectionName = "Hub";

    public string DatabasePath { get; set; } = "/data/hub.db";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxConcurrentPolls { get; set; } = 4;

    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(180);

    // Window behind the per-finding status: seen within it = active; older,
    // with the endpoint still heartbeating from a reachable source =
    // likely_resolved; anything else = not_observed.
    public TimeSpan ResolutionGrace { get; set; } = TimeSpan.FromDays(7);
    // A daemon that first sees more signatures than this in an hour is flagged on
    // /api/sources and logged: see SignatureRate. 0 turns the check off.
    public int NewSignaturesPerHourWarning { get; set; } = 1000;
    public int DefaultReadLimit { get; set; } = 1000;
    public int MaxReadLimit { get; set; } = 10_000;
    public AnalysisOptions Analysis { get; set; } = new();
    public UpdateCheckOptions UpdateCheck { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public AckRelayOptions AckRelay { get; set; } = new();
    public AccessOptions Access { get; set; } = new();
    public DisclosuresOptions Disclosures { get; set; } = new();

    // The one port /metrics answers on, and nothing else answers there. Unset, /metrics
    // answers on every port as it always has; access control needs one (A14).
    public int? MetricsPort { get; set; }

    public IReadOnlyList<SourceOptions> Sources { get; set; } = [];
}

/// <summary>
///     Who reaches which findings, sources and runs, declared as configuration: the
///     Hub stores no user and no membership. Off by default, and nothing here is
///     read or validated while it is off. Every list defaults to empty, since a
///     bound list appends to its default.
/// </summary>
public sealed record AccessOptions
{
    public bool Enabled { get; set; }

    // Subjects holding the built-in admin role on '*'.
    public IReadOnlyList<string> Admins { get; set; } = [];

    // Group references, provider or internal:, holding the built-in admin role on '*'.
    public IReadOnlyList<string> AdminGroups { get; set; } = [];

    public IReadOnlyList<InternalGroupOptions> Groups { get; set; } = [];
    public IReadOnlyList<ProjectOptions> Projects { get; set; } = [];
    public IReadOnlyList<RoleOptions> Roles { get; set; } = [];
    public IReadOnlyList<GrantOptions> Grants { get; set; } = [];
    public IReadOnlyList<MachineTokenOptions> MachineTokens { get; set; } = [];

    // Break-glass accounts for when the provider is down, signed in on the Hub's own page.
    public IReadOnlyList<LocalAccountOptions> LocalAccounts { get; set; } = [];
}

// Referenced as internal:<Name>. Members are subjects, never machine tokens.
public sealed record InternalGroupOptions
{
    public string Name { get; set; } = "";
    public IReadOnlyList<string> Members { get; set; } = [];
}

public sealed record ProjectOptions
{
    public string Name { get; set; } = "";
    public IReadOnlyList<PatternOptions> Patterns { get; set; } = [];
}

// Each part is exact, or a prefix ending in one '*'. Attribute is key=value, split on the first '='.
public sealed record PatternOptions
{
    public string? Source { get; set; }
    public string? Service { get; set; }
    public string? Attribute { get; set; }
}

public sealed record RoleOptions
{
    public string Name { get; set; } = "";
    public IReadOnlyList<string> Permissions { get; set; } = [];
}

// Project is a declared project or '*', which also covers records no project matches.
public sealed record GrantOptions
{
    public string Group { get; set; } = "";
    public string Project { get; set; } = "";
    public string Role { get; set; } = "";
}

public sealed record MachineTokenOptions
{
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }

    // Trimmed on binding, like SourceOptions.ImportApiKey: a Secret value often ends with a newline.
    public string? Token
    {
        get;
        set => field = value?.Trim();
    }
}

// Signs in as local:<Name>. PasswordHash is the PHC string hash-password prints, delivered
// through a Secret; trimmed on binding, since a Secret value often ends with a newline.
public sealed record LocalAccountOptions
{
    public string Name { get; set; } = "";

    public string? PasswordHash
    {
        get;
        set => field = value?.Trim();
    }
}

/// <summary>The eight permissions, the three built-in roles and what a machine token may hold.</summary>
public static class AccessPermissions
{
    public const string FindingsRead = "findings.read";
    public const string TracesRead = "traces.read";
    public const string SourcesRead = "sources.read";
    public const string AnalysesRun = "analyses.run";
    public const string AcksWrite = "acks.write";
    public const string AccessAdmin = "access.admin";

    // Hub-level, granted on '*' only: a disclosure belongs to no source.
    public const string DisclosuresRead = "disclosures.read";
    public const string DisclosuresManage = "disclosures.manage";

    public const string ReaderRole = "reader";
    public const string MemberRole = "member";
    public const string AdminRole = "admin";

    public static readonly IReadOnlyList<string> All =
        [FindingsRead, TracesRead, SourcesRead, AnalysesRun, AcksWrite, AccessAdmin, DisclosuresRead, DisclosuresManage];

    // reader is not read-only: it starts analyses.
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> BuiltInRoles =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [ReaderRole] = [FindingsRead, TracesRead, AnalysesRun],
            [MemberRole] = [FindingsRead, TracesRead, AnalysesRun, AcksWrite],
            [AdminRole] = All
        };

    public static readonly IReadOnlyList<string> MachineTokenPermissions = [FindingsRead];

    // A write is matched against every grouping a copy was seen in, a read against any.
    public static bool IsWrite(string permission)
    {
        return permission is AnalysesRun or AcksWrite;
    }
}

/// <summary>
///     Periodic disclosures a scheduled job publishes to the Hub. On when ImportKey is set: the import
///     route, the Disclosures screen, the share links and the webhooks all answer only then.
/// </summary>
public sealed record DisclosuresOptions
{
    // Trimmed on binding, like a source's import key, so a key mounted from a file with a
    // trailing newline compares equal to the one the CI sends.
    public string? ImportKey
    {
        get;
        set => field = value?.Trim();
    }

    // An internal report carries G1 detail. Shown to disclosures.read holders only, never
    // shared nor pushed, and refused at startup on a Hub without access control.
    public bool AllowInternal { get; set; }

    // Passed to disclose-pdf, which prints them in the PDF's verify command and checks nothing
    // with them: the signature is the reader's to check. Both or neither.
    public string? ExpectedIdentity { get; set; }
    public string? ExpectedIssuer { get; set; }

    public int ShareLinkDays { get; set; } = 7;
    public IReadOnlyList<WebhookOptions> Webhooks { get; set; } = [];

    // The Hub's own address as a receiver reaches it, for the share link a webhook with
    // IncludeShareLink carries: the delivery runs outside any request to read it from.
    public Uri? PublicUrl { get; set; }

    public bool Enabled => ImportKey is not null;
}

/// <summary>
///     A receiver of the published and withdrawn events. Its URL is never stored nor shown:
///     Slack and Teams URLs carry their own secret.
/// </summary>
public sealed record WebhookOptions
{
    public string Name { get; set; } = "";
    public Uri? Url { get; set; }

    public string? Secret
    {
        get;
        set => field = value?.Trim();
    }

    // Only public: the field states the rule in one place, an internal report never leaves.
    public string Confidentiality { get; set; } = "public";
    public bool IncludeShareLink { get; set; }
    public int ShareLinkDays { get; set; } = 7;
}

public sealed record AckRelayOptions
{
    // Who acks has to be known. A Hub:Auth session always says. The header a
    // reverse proxy sets, Hub:Analysis:IdentityHeader, is a claim the Hub cannot
    // verify, so it names the caller only once this opts in.
    public bool TrustIdentityHeader { get; set; }
}

public sealed record AnalysisOptions
{
    // Not a setting: configuration binding ignores a const, and it sits with the
    // properties only to be near the one it bounds.
    // The engine's own ceiling on --max-traces, which it enforces when it parses
    // its arguments. A cap above it would pass every check here and then fail the
    // run on an engine error the operator never asked for.
    public const int EngineMaxTraces = 10_000;

    // Absent means the Hub keeps collecting findings but cannot run an
    // analysis: the launcher reads a null engine version and says so.
    public string? EngineBinaryPath { get; set; }

    // Where rendered reports live. Must be writable: the container is
    // read-only everywhere else.
    public string ReportDirectory { get; set; } = "/data/reports";

    // Header a reverse proxy sets with the established identity. The Hub has
    // no account surface and records the value as a claim, never verifies it.
    public string IdentityHeader { get; set; } = "X-Forwarded-User";

    public int Workers { get; set; } = 2;

    public int MaxTracesCap { get; set; } = 2000;

    // Span trees embedded in the rendered report. Passing this at all opts the
    // sink out of size targeting, which is why it is set: without it a wide
    // sweep loses findings to the budget, and the finding list is the thing an
    // operator came for. The trees are what gets capped instead.
    public int MaxTracesEmbedded { get; set; } = 50;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(300);

    // Memory the container's cgroup must still have free for a run to start the
    // engine, which runs inside it: a run that cannot fit is refused with
    // hub_memory_low instead of getting the whole Hub OOM-killed. 0 turns it off.
    public int MinFreeMemoryMiB { get; set; } = 256;

    public TimeSpan ReportRetention { get; set; } = TimeSpan.FromHours(24);

    // How long a finished run's row survives. Far longer than ReportRetention on
    // purpose: the report is deleted after a day, the row outlives it so an
    // expired run can still be read and relaunched with its own parameters.
    public TimeSpan RunRetention { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>
///     Whether the Hub asks GitHub what the newest published release of each
///     product is. This is the only outbound destination the Hub has that is not a
///     configured source, so it is stated here rather than implied, and one key
///     turns it off for a deployment with no egress.
/// </summary>
public sealed record UpdateCheckOptions
{
    public bool Enabled { get; set; } = true;

    // A release lands a few times a month at most. Anything shorter spends
    // requests to learn nothing, and the rate limit is 60 an hour per address.
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    public Uri EngineEndpoint { get; set; } =
        new("https://api.github.com/repos/Institut-du-Numerique-Responsable/perf-sentinel/releases/latest");

    public Uri HubEndpoint { get; set; } =
        new("https://api.github.com/repos/Institut-du-Numerique-Responsable/PerfSentinelHub/releases/latest");
}

/// <summary>
///     Browser sign-in against an OAuth2 provider, so no authenticating proxy has
///     to stand in front of the Hub. Generic on purpose: three endpoints and the
///     userinfo field that names the user fit Keycloak, Entra ID, Google, GitLab and
///     Bitbucket alike, and none of them needs a package beyond the framework.
/// </summary>
public sealed record AuthOptions
{
    public bool Enabled { get; set; }
    public Uri? AuthorizationEndpoint { get; set; }
    public Uri? TokenEndpoint { get; set; }
    public Uri? UserInformationEndpoint { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    // Space-separated, as it goes on the wire. A list would append to its own
    // default when bound, not replace it.
    public string Scopes { get; set; } = "openid profile email";

    // The userinfo field recorded as the identity.
    public string IdentityClaim { get; set; } = "email";

    // The userinfo field holding the authorization key, an id the provider never reassigns.
    // Read only with Hub:Access:Enabled; IdentityClaim stays the name people read.
    public string SubjectClaim { get; set; } = "sub";

    // The userinfo field listing the user's groups, read when RequiredGroups is set or
    // Hub:Access:Enabled.
    public string GroupsClaim { get; set; } = "groups";

    // Empty lets in whoever the provider signs in. Otherwise a user must belong to one
    // of these, spelled as the provider sends them. Empty by default, since a bound
    // list appends to its default.
    public IReadOnlyList<string> RequiredGroups { get; set; } = [];

    // CIDR networks whose X-Forwarded-For and X-Forwarded-Proto are trusted. Empty keeps the
    // scheme trusted from any peer and the address never; terminal sign-in needs the list.
    public IReadOnlyList<string> TrustedProxyNetworks { get; set; } = [];
}

public static class SourceKinds
{
    public const string Daemon = "daemon";
    public const string Tempo = "tempo";
    public const string JaegerQuery = "jaeger_query";

    public static bool IsKnown(string kind)
    {
        return kind is Daemon or Tempo or JaegerQuery;
    }
}

public sealed record SourceOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    public string Environment { get; set; } = "";

    // A daemon detects its own findings and is polled. A trace backend stores
    // traces and detects nothing, so it is never polled and only ever read by
    // an analysis run.
    public string Kind { get; set; } = SourceKinds.Daemon;

    // How far back this backend keeps traces, declared here because no backend
    // API exposes it. Bounds the launcher's time-range picker. Declared and not
    // measured, so it carries the same caveat as the environment: it keeps a
    // stale claim until someone edits it.
    public int? RetentionHours { get; set; }

    public Uri? BaseUrl { get; set; }

    // Where a workstation and a viewer's browser reach this source, when that is
    // not where the Hub does: a Hub inside the cluster polls a Service name that
    // nothing outside it resolves. Printed commands and live reports use it,
    // the Hub's own reads and runs never do.
    public Uri? PublicUrl { get; set; }

    // The endpoint as it goes on the engine's command line when the Hub runs it.
    public string EndpointArgument => BaseUrl!.ToString().TrimEnd('/');

    // What leaves the Hub, the printed command and the live report: the public
    // URL, or the one the Hub runs against when none is declared.
    public Uri PublishedUrl => (PublicUrl ?? BaseUrl)!;

    // PublishedUrl spelled the way EndpointArgument spells BaseUrl.
    public string PublicEndpointArgument => PublishedUrl.ToString().TrimEnd('/');

    // The engine subcommand that reads this source, null for a daemon: a daemon is
    // read over HTTP and has no subcommand of its own.
    public string? EngineSubcommand => Kind switch
    {
        SourceKinds.Daemon => null,
        SourceKinds.Tempo => "tempo",
        _ => "jaeger-query"
    };

    public string? AuthHeaderName { get; set; }

    public string? AuthHeaderValue { get; set; }

    // The header the PublicUrl route asks for, when it is not AuthHeaderName:
    // an ingress usually authenticates on its own terms. A name and no value,
    // since the Hub never calls that route.
    public string? PublicAuthHeaderName { get; set; }

    // The header a printed command names: the public route's own, or the Hub's
    // when the public route reaches the same daemon, through a port-forward.
    public string? PublishedAuthHeaderName => PublicAuthHeaderName ?? AuthHeaderName;

    // What the relay sends on an ack write, the daemon's [daemon.ack] api_key.
    // Apart from the read pair above, which the daemon refuses on a write.
    public string? AckHeaderName { get; set; }

    public string? AckHeaderValue { get; set; }

    public bool HasAckCredential => AckHeaderName is not null;

    // The rankings a report of this source carries in its pg_stat and mysql_stat tabs,
    // scraped by the engine at render time. Null leaves the tab empty.
    public DbStatOptions? PgStat { get; set; }

    public DbStatOptions? MySqlStat { get; set; }

    // Trimmed on binding: the daemon trims its key file, so a secret mounted from a file with a
    // trailing newline must hash to the same bytes on both halves of the contract.
    public string? ImportApiKey
    {
        get;
        set => field = value?.Trim();
    }
}

/// <summary>
///     A Prometheus the engine scrapes for `pg_stat_statements` or Performance Schema
///     rankings when it renders a report, the `--pg-stat-prometheus` or
///     `--mysql-stat-prometheus` family of flags. Each value is the flag of the same name,
///     and a null one keeps the engine's default.
/// </summary>
public sealed record DbStatOptions
{
    public Uri? Prometheus { get; set; }
    public string? Metric { get; set; }
    public string? QueryLabel { get; set; }
    public string? CallsMetric { get; set; }
    public string? Unit { get; set; }

    // Label matchers such as namespace="shop",db_role="primary", for an exporter that
    // serves several tenants or a primary with its replicas. Engine 0.31.0 and later.
    public string? Selector { get; set; }

    public int? Top { get; set; }

    // Handed to the engine in an environment variable, never on its command line.
    public string? AuthHeaderName { get; set; }

    public string? AuthHeaderValue { get; set; }
}

public sealed partial class HubOptionsValidator(TimeProvider? timeProvider = null) : IValidateOptions<HubOptions>
{
    private const string UrlShape = "without credentials, query, or fragment.";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ValidateOptionsResult Validate(string? name, HubOptions options)
    {
        var errors = new List<string>();
        ValidateHubSettings(options, errors);
        ValidateAccessSettings(options, _timeProvider.GetUtcNow(), errors);
        ValidateDisclosures(options, errors);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in options.Sources)
            ValidateSource(source, ids, errors);

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateHubSettings(HubOptions options, List<string> errors)
    {
        if (!Path.IsPathFullyQualified(options.DatabasePath))
            errors.Add("Hub:DatabasePath must be absolute.");
        if (options.PollInterval <= TimeSpan.Zero)
            errors.Add("Hub:PollInterval must be positive.");
        if (options.HttpTimeout <= TimeSpan.Zero)
            errors.Add("Hub:HttpTimeout must be positive.");
        if (options.Retention <= TimeSpan.Zero)
            errors.Add("Hub:Retention must be positive.");
        if (options.ResolutionGrace <= TimeSpan.Zero || options.ResolutionGrace >= options.Retention)
            errors.Add("Hub:ResolutionGrace must be positive and below Hub:Retention.");
        if (options.MaxConcurrentPolls is < 1 or > 32)
            errors.Add("Hub:MaxConcurrentPolls must be between 1 and 32.");
        if (options.MaxReadLimit is < 1 or > 10_000)
            errors.Add("Hub:MaxReadLimit must be between 1 and 10000.");
        if (options.NewSignaturesPerHourWarning < 0)
            errors.Add("Hub:NewSignaturesPerHourWarning must be 0 or more, 0 turning the check off.");
        if (options.DefaultReadLimit < 1 || options.DefaultReadLimit > options.MaxReadLimit)
            errors.Add("Hub:DefaultReadLimit must be between 1 and MaxReadLimit.");
        if (options.Sources.Count == 0)
            errors.Add("Hub:Sources must contain at least one source.");
        // M1. Whether Kestrel listens there is known only once it has started (Program.cs).
        if (options.MetricsPort is < 1 or > 65535)
            errors.Add("Hub:MetricsPort must be between 1 and 65535.");
        ValidateAnalysisSettings(options.Analysis, errors);
        ValidateUpdateCheckSettings(options.UpdateCheck, errors);
        ValidateAuthSettings(options.Auth, errors);
    }

    private static void ValidateAuthSettings(AuthOptions auth, List<string> errors)
    {
        // Refused rather than ignored: a gate the operator believes is up would
        // leave the Hub open to every reader of the port.
        if (!auth.Enabled && auth.RequiredGroups.Count > 0)
            errors.Add("Hub:Auth:RequiredGroups requires Hub:Auth:Enabled: off, the Hub signs nobody in "
                       + "and checks no group.");
        if (!auth.Enabled) return;
        if (IsInvalidAuthEndpoint(auth.AuthorizationEndpoint) ||
            IsInvalidAuthEndpoint(auth.TokenEndpoint) ||
            IsInvalidAuthEndpoint(auth.UserInformationEndpoint))
            errors.Add("Hub:Auth endpoints must be absolute HTTPS URLs (HTTP on loopback only) "
                       + "without credentials or fragment.");
        if (string.IsNullOrWhiteSpace(auth.ClientId) || string.IsNullOrWhiteSpace(auth.ClientSecret))
            errors.Add("Hub:Auth:ClientId and Hub:Auth:ClientSecret are required.");
        if (string.IsNullOrWhiteSpace(auth.IdentityClaim))
            errors.Add("Hub:Auth:IdentityClaim must name a userinfo field.");
        if (auth.RequiredGroups.Count > 0 && string.IsNullOrWhiteSpace(auth.GroupsClaim))
            errors.Add("Hub:Auth:GroupsClaim must name a userinfo field when Hub:Auth:RequiredGroups is set.");
        if (auth.RequiredGroups.Any(string.IsNullOrWhiteSpace))
            errors.Add("Hub:Auth:RequiredGroups must not hold a blank entry.");
        foreach (var network in auth.TrustedProxyNetworks.Where(network => !IPNetwork.TryParse(network, out _)))
            errors.Add($"Hub:Auth:TrustedProxyNetworks entry '{network}' is not a CIDR network.");
    }

    private static bool IsInvalidAuthEndpoint(Uri? endpoint)
    {
        return endpoint is null ||
               !endpoint.IsAbsoluteUri ||
               !(endpoint.Scheme == Uri.UriSchemeHttps ||
                 (endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)) ||
               !string.IsNullOrEmpty(endpoint.UserInfo) ||
               !string.IsNullOrEmpty(endpoint.Fragment);
    }

    private static void ValidateAnalysisSettings(AnalysisOptions analysis, List<string> errors)
    {
        ValidateAnalysisPaths(analysis, errors);
        if (IsInvalidHeaderName(analysis.IdentityHeader))
            errors.Add("Hub:Analysis:IdentityHeader must be a header name.");
        if (analysis.Workers is < 1 or > 16)
            errors.Add("Hub:Analysis:Workers must be between 1 and 16.");
        if (analysis.MaxTracesCap is < 1 or > AnalysisOptions.EngineMaxTraces)
            errors.Add($"Hub:Analysis:MaxTracesCap must be between 1 and {AnalysisOptions.EngineMaxTraces}, "
                       + "the engine's own limit on --max-traces.");
        if (analysis.MaxTracesEmbedded is < 0 or > 10_000)
            errors.Add("Hub:Analysis:MaxTracesEmbedded must be between 0 and 10000.");
        if (analysis.Timeout <= TimeSpan.Zero || analysis.Timeout > TimeSpan.FromHours(1))
            errors.Add("Hub:Analysis:Timeout must be positive and at most one hour.");
        if (analysis.MinFreeMemoryMiB is < 0 or > 65_536)
            errors.Add("Hub:Analysis:MinFreeMemoryMiB must be between 0 and 65536, 0 turning the check off.");
        if (analysis.ReportRetention <= TimeSpan.Zero)
            errors.Add("Hub:Analysis:ReportRetention must be positive.");
        if (analysis.RunRetention <= analysis.ReportRetention)
            errors.Add("Hub:Analysis:RunRetention must be longer than Hub:Analysis:ReportRetention, "
                       + "since an expired run keeps its parameters after its report is deleted.");
    }

    private static void ValidateAnalysisPaths(AnalysisOptions analysis, List<string> errors)
    {
        if (analysis.EngineBinaryPath is { } path &&
            (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            errors.Add("Hub:Analysis:EngineBinaryPath must be absolute.");
        if (!Path.IsPathFullyQualified(analysis.ReportDirectory))
            errors.Add("Hub:Analysis:ReportDirectory must be absolute.");
    }

    private static void ValidateUpdateCheckSettings(UpdateCheckOptions update, List<string> errors)
    {
        if (!update.Enabled) return;
        if (update.Interval < TimeSpan.FromMinutes(15))
            errors.Add("Hub:UpdateCheck:Interval must be at least 15 minutes.");
        // Held to a tighter shape than a source: https only, and no query, so
        // whoever reads the configuration sees the whole destination.
        if (IsInvalidUpdateEndpoint(update.EngineEndpoint))
            errors.Add("Hub:UpdateCheck:EngineEndpoint must be an absolute HTTPS URL "
                       + UrlShape);
        if (IsInvalidUpdateEndpoint(update.HubEndpoint))
            errors.Add("Hub:UpdateCheck:HubEndpoint must be an absolute HTTPS URL "
                       + UrlShape);
    }

    private static bool IsInvalidUpdateEndpoint(Uri? endpoint)
    {
        return endpoint is null ||
               !endpoint.IsAbsoluteUri ||
               endpoint.Scheme != Uri.UriSchemeHttps ||
               !string.IsNullOrEmpty(endpoint.UserInfo) ||
               !string.IsNullOrEmpty(endpoint.Query) ||
               !string.IsNullOrEmpty(endpoint.Fragment);
    }

    private static void ValidateSource(SourceOptions source, HashSet<string> ids, List<string> errors)
    {
        if (!IsValidName(source.Id) || !ids.Add(source.Id))
            errors.Add("Source IDs must be unique and contain 1-64 ASCII letters, digits, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Environment))
            errors.Add($"Source '{source.Id}' requires a name and environment.");
        // An environment becomes a Prometheus label value and a member of the
        // closed set the read API matches a query parameter against.
        if (!string.IsNullOrWhiteSpace(source.Environment) && source.Environment.Any(char.IsControl))
            errors.Add($"Source '{source.Id}' environment must not contain control characters.");
        if (!SourceKinds.IsKnown(source.Kind))
            errors.Add($"Source '{source.Id}' kind must be 'daemon', 'tempo' or 'jaeger_query'.");
        if (source.Kind != SourceKinds.Daemon && source.ImportApiKey is not null)
            errors.Add($"Source '{source.Id}' is not a daemon and cannot carry an import API key.");
        ValidateRetentionHours(source, errors);
        ValidateBaseUrl(source, errors);
        ValidateHeaderPair(source.Id, "auth", source.AuthHeaderName, source.AuthHeaderValue, errors);
        ValidateAckHeader(source, errors);
        ValidatePublicAuthHeader(source, errors);
        if (source.ImportApiKey is { } importApiKey && IsInvalidImportApiKey(importApiKey))
            errors.Add($"Source '{source.Id}' import API key must contain at least 32 characters and no controls.");
        ValidateDbStat(source.Id, "pg_stat", source.PgStat, ["seconds", "milliseconds"],
            "seconds or milliseconds", errors);
        ValidateDbStat(source.Id, "mysql_stat", source.MySqlStat, ["seconds", "milliseconds", "picoseconds"],
            "seconds, milliseconds or picoseconds", errors);
    }

    // The engine checks the names and the selector grammar itself, and a render it
    // refuses is tried again without the tab. What it would never reach is checked here.
    private static void ValidateDbStat(
        string sourceId,
        string family,
        DbStatOptions? stats,
        string[] units,
        string unitsText,
        List<string> errors)
    {
        if (stats is null)
            return;
        if (stats.Prometheus is null)
            errors.Add($"Source '{sourceId}' {family} requires a Prometheus URL.");
        else if (IsInvalidSourceUrl(stats.Prometheus))
            errors.Add($"Source '{sourceId}' {family} Prometheus must be an absolute HTTP(S) URL " + UrlShape);
        if (stats.Unit is { } unit && !units.Contains(unit))
            errors.Add($"Source '{sourceId}' {family} unit must be {unitsText}.");
        if (stats.Top is < 1 or > 1000)
            errors.Add($"Source '{sourceId}' {family} top must be between 1 and 1000.");
        if (stats.Selector is { } selector && selector.Any(char.IsControl))
            errors.Add($"Source '{sourceId}' {family} selector must not contain control characters.");
        ValidateHeaderPair(sourceId, $"{family} auth", stats.AuthHeaderName, stats.AuthHeaderValue, errors);
    }

    private static void ValidateRetentionHours(SourceOptions source, List<string> errors)
    {
        if (source.RetentionHours is not { } retentionHours)
            return;

        // A daemon takes no window, so nothing would ever read the value. A
        // setting with no consumer is worse than a missing one: someone tunes
        // it and nothing happens.
        if (source.Kind == SourceKinds.Daemon)
            errors.Add($"Source '{source.Id}' is a daemon and takes no trace retention.");
        else if (retentionHours is < 1 or > 87_600)
            errors.Add($"Source '{source.Id}' retention must be between 1 hour and 10 years.");
    }

    private static void ValidateBaseUrl(SourceOptions source, List<string> errors)
    {
        if (IsInvalidSourceUrl(source.BaseUrl))
            errors.Add(
                $"Source '{source.Id}' requires an absolute HTTP(S) URL " +
                UrlShape);
        if (source.PublicUrl is not null && IsInvalidSourceUrl(source.PublicUrl))
            errors.Add(
                $"Source '{source.Id}' public URL must be an absolute HTTP(S) URL " +
                UrlShape);
    }

    private static bool IsInvalidSourceUrl(Uri? url)
    {
        return url is null ||
               !url.IsAbsoluteUri ||
               (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) ||
               !string.IsNullOrEmpty(url.UserInfo) ||
               !string.IsNullOrEmpty(url.Query) ||
               !string.IsNullOrEmpty(url.Fragment);
    }

    // Both credentials a source can carry: "auth" reads the daemon, "ack" writes to it.
    private static void ValidateHeaderPair(
        string sourceId,
        string label,
        string? name,
        string? value,
        List<string> errors)
    {
        if (name is null && value is null)
            return;

        if (name is null || value is null)
        {
            errors.Add($"Source '{sourceId}' must provide both {label} header name and value.");
            return;
        }

        if (name.Contains('\r', StringComparison.Ordinal) ||
            name.Contains('\n', StringComparison.Ordinal) ||
            value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            errors.Add($"Source '{sourceId}' {label} header contains a newline.");
            return;
        }

        using var request = new HttpRequestMessage();
        try
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
                errors.Add($"Source '{sourceId}' {label} header is invalid.");
        }
        catch (FormatException)
        {
            errors.Add($"Source '{sourceId}' {label} header is invalid.");
        }
    }

    private static void ValidateAckHeader(SourceOptions source, List<string> errors)
    {
        ValidateHeaderPair(source.Id, "ack", source.AckHeaderName, source.AckHeaderValue, errors);
        // A trace backend has no ack route, so nothing would ever send the credential.
        if (source.Kind != SourceKinds.Daemon && (source.AckHeaderName ?? source.AckHeaderValue) is not null)
            errors.Add($"Source '{source.Id}' is not a daemon and cannot carry an ack credential.");
        // The daemon refuses a read key equal to its ack key: every reader could then write.
        if (source is { AckHeaderValue: { } ackHeaderValue, AuthHeaderValue: { } authHeaderValue } &&
            KeyOf(ackHeaderValue) == KeyOf(authHeaderValue))
            errors.Add($"Source '{source.Id}' ack credential must differ from its read credential.");
    }

    // The key a header value carries, as the daemon reads it: bare in X-API-Key, or
    // behind a Bearer scheme of any case in Authorization.
    private static string KeyOf(string headerValue)
    {
        return headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? headerValue[7..].TrimStart(' ')
            : headerValue;
    }

    private static void ValidatePublicAuthHeader(SourceOptions source, List<string> errors)
    {
        if (source.PublicAuthHeaderName is not { } name)
            return;

        if (source.PublicUrl is null)
            errors.Add($"Source '{source.Id}' public auth header name requires a public URL.");
        if (IsInvalidHeaderName(name))
            errors.Add($"Source '{source.Id}' public auth header name is invalid.");
    }

    private static bool IsInvalidImportApiKey(string value)
    {
        return value.Length < 32 || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl);
    }

    private static bool IsInvalidHeaderName(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.Any(character => char.IsControl(character) || character == ' ');
    }

    // A source id, and an internal group, project or role name.
    private static bool IsValidName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
               name.Length <= 64 &&
               name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }
}
