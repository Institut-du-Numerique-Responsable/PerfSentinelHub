using PerfSentinelHub.Localization;

namespace PerfSentinelHub.Configuration;

public sealed record HubOptions
{
    public const string SectionName = "Hub";

    public string DatabasePath { get; set; } = "/data/hub.db";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxConcurrentPolls { get; set; } = 4;

    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(180);

    // Detection days, counts and ledgers: long enough for a year card and the whole year before it.
    public TimeSpan DetectionRetention { get; set; } = TimeSpan.FromDays(1100);

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
    public RecapsOptions Recaps { get; set; } = new();
    public CardsOptions Cards { get; set; } = new();

    // The one port /metrics answers on, and nothing else answers there. Unset, /metrics
    // answers on every port as it always has; access control needs one (A14).
    public int? MetricsPort { get; set; }

    public IReadOnlyList<SourceOptions> Sources { get; set; } = [];

    // Computed, so binding ignores it: the address a recap's engineering link names.
    public Uri? RecapPublicUrl => Recaps.PublicUrl ?? Disclosures.PublicUrl;
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

/// <summary>The twelve permissions, the three built-in roles and what a machine token may hold.</summary>
public static class AccessPermissions
{
    public const string FindingsRead = "findings.read";
    public const string TracesRead = "traces.read";
    public const string SourcesRead = "sources.read";
    public const string AnalysesRun = "analyses.run";
    public const string AcksWrite = "acks.write";
    public const string ActionsWrite = "actions.write";
    public const string LabelsWrite = "labels.write";
    public const string CardsWrite = "cards.write";
    public const string AccessAdmin = "access.admin";

    // Hub-level, granted on '*' only: a disclosure belongs to no source.
    public const string DisclosuresRead = "disclosures.read";
    public const string DisclosuresManage = "disclosures.manage";
    public const string RecapsRead = "recaps.read";

    private const string ReaderRole = "reader";
    private const string MemberRole = "member";
    public const string AdminRole = "admin";

    public static readonly IReadOnlyList<string> All =
    [
        FindingsRead, TracesRead, SourcesRead, AnalysesRun, AcksWrite, ActionsWrite, LabelsWrite, CardsWrite,
        AccessAdmin, DisclosuresRead, DisclosuresManage, RecapsRead
    ];

    // reader is not read-only: it starts analyses.
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> BuiltInRoles =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [ReaderRole] = [FindingsRead, TracesRead, AnalysesRun],
            [MemberRole] = [FindingsRead, TracesRead, AnalysesRun, AcksWrite, ActionsWrite, LabelsWrite, CardsWrite],
            [AdminRole] = All
        };

    public static readonly IReadOnlyList<string> MachineTokenPermissions = [FindingsRead];
}

/// <summary>
///     Who signs the share cards: one organisation per Hub, and its domain, which only the DNS line of a
///     level 2 card attaches; a level 1 card needs none. Organisation set turns cards on.
/// </summary>
public sealed record CardsOptions
{
    // Trimmed on binding, as MachineTokenOptions.Token is.
    public string? Organisation
    {
        get;
        set => field = value?.Trim();
    }

    public string? Domain
    {
        get;
        set => field = value?.Trim();
    }

    /// <summary>The equivalence country a card starts from when its request names none: fr, gb or us.</summary>
    public string? Country
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public string CountryOrDefault => Country ?? "fr";

    /// <summary>How many card renders run the engine at once; one more is refused with a 503, never queued.</summary>
    public int MaxConcurrentRenders { get; set; } = 2;

    public bool Enabled => Organisation is not null;
}

/// <summary>
///     The review recap. MovementThreshold is the share of change past which a finding reads up or down.
///     The rest declares the frozen reports: how long they are kept, who gets them and through what.
/// </summary>
public sealed record RecapsOptions
{
    public double MovementThreshold { get; set; } = 0.2;

    // Kept past a month and a day: a month's report must outlive its month as the last closed one,
    // or the next pass would generate and send it again.
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(365);

    // How long a report waits for the ledgers to seal the period's last day.
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(6);

    // The Hub's own address for the engineering link; falls back to Hub:Disclosures:PublicUrl.
    public Uri? PublicUrl { get; set; }

    // Lists default empty: a bound list appends to its default.
    public IReadOnlyList<WebhookOptions> Webhooks { get; set; } = [];
    public RecapSmtpOptions Smtp { get; set; } = new();
    public IReadOnlyList<RecapSubscriptionOptions> Subscriptions { get; set; } = [];
}

/// <summary>The relay the Hub hands each recap e-mail to. It never looks up a recipient's mail server.</summary>
public sealed record RecapSmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool StartTls { get; set; } = true;
    public string? UserName { get; set; }

    // Trimmed on binding, like a webhook secret, so a password mounted from a file compares right.
    public string? Password
    {
        get;
        set => field = value?.Trim();
    }

    public string? From { get; set; }

    public bool Enabled => Host is not null;
}

/// <summary>One scope, its cadences and views, and where the frozen report of each closed period goes.</summary>
public sealed record RecapSubscriptionOptions
{
    public string Name { get; set; } = "";
    public IReadOnlyList<string> Cadences { get; set; } = [];
    public IReadOnlyList<string> Views { get; set; } = [];
    public string? Environment { get; set; }
    public string? SourceId { get; set; }
    public string? Service { get; set; }
    public string? Grouping { get; set; }
    public IReadOnlyList<string> Webhooks { get; set; } = [];
    public IReadOnlyList<string> Emails { get; set; } = [];

    // The language of its reports, its webhook and the Emails addresses.
    public string Language { get; set; } = HubLang.En;

    public IReadOnlyList<RecapRecipientOptions> Recipients { get; set; } = [];
}

/// <summary>An address that reads its recap mail in a language of its own, else in its subscription's.</summary>
public sealed record RecapRecipientOptions
{
    public string? Address { get; set; }
    public string? Language { get; set; }
}

public static class RecapViewNames
{
    public const string Pilotage = "pilotage";
    public const string Tech = "tech";
    public static readonly string[] All = [Pilotage, Tech];
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

    // The userinfo field shown as the action owner when the provider sends it. Never used for access.
    public string DisplayNameClaim { get; set; } = "name";

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
