# Configuration

Environment variables use the .NET `Hub__...` form. Helm exposes the same settings under
`hub` and `sources`.

[`examples/appsettings.reference.json`](../examples/appsettings.reference.json) sets every
setting to the value the Hub already uses, annotated. Copying it whole changes nothing, it
is the inventory rather than a starting point. A test keeps it exhaustive, which matters
here because .NET ignores an unrecognised configuration key in silence: a mistyped name
produces no error and reads like a bug in the Hub rather than a typo in your file.

## Settings

| Setting                            | Default                                                                     | Validation                                                                                      |
|------------------------------------|-----------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------|
| `Hub:DatabasePath`                 | `/data/hub.db`                                                              | Absolute path                                                                                   |
| `Hub:PollInterval`                 | `01:00:00`                                                                  | Positive duration                                                                               |
| `Hub:HttpTimeout`                  | `00:00:10`                                                                  | Positive duration                                                                               |
| `Hub:MaxConcurrentPolls`           | `4`                                                                         | 1 to 32                                                                                         |
| `Hub:Retention`                    | `180.00:00:00` (180 days)                                                   | Positive duration                                                                               |
| `Hub:ResolutionGrace`              | `7.00:00:00` (7 days)                                                       | Positive, below `Retention`                                                                     |
| `Hub:NewSignaturesPerHourWarning`  | `1000`                                                                      | 0 or more, 0 turning it off. A daemon past it is flagged on `/api/sources` and logged           |
| `Hub:DefaultReadLimit`             | `1000`                                                                      | 1 to `MaxReadLimit`                                                                             |
| `Hub:MaxReadLimit`                 | `10000`                                                                     | 1 to 10000                                                                                      |
| `Hub:Recaps:MovementThreshold`     | `0.2`                                                                       | Above 0, at most 10. A finding moves up or down on the recap past this share of change          |
| `Hub:MetricsPort`                  | none                                                                        | 1 to 65535. The only port `/metrics` answers on, see [OPERATIONS.md](OPERATIONS.md#metrics)     |
| `Hub:Analysis:EngineBinaryPath`    | none                                                                        | Optional, absolute path to the perf-sentinel binary. Absent means analysis runs are unavailable |
| `Hub:Analysis:ReportDirectory`     | `/data/reports`                                                             | Absolute, writable. Rendered reports live here                                                  |
| `Hub:Analysis:IdentityHeader`      | `X-Forwarded-User`                                                          | Header a reverse proxy sets with the requester's identity. Ignored for a `Hub:Auth` session     |
| `Hub:Analysis:Workers`             | `2`                                                                         | 1 to 16                                                                                         |
| `Hub:Analysis:MaxTracesCap`        | `2000`                                                                      | 1 to 10000, the engine's own limit on `--max-traces`                                            |
| `Hub:Analysis:MaxTracesEmbedded`   | `50`                                                                        | 0 to 10000. Span trees embedded in the report. Setting it opts the sink out of size targeting   |
| `Hub:Analysis:Timeout`             | `00:05:00`                                                                  | Positive, at most one hour                                                                      |
| `Hub:Analysis:MinFreeMemoryMiB`    | `256`                                                                       | 0 to 65536, 0 turning it off. Free memory the container needs for a run to start the engine     |
| `Hub:Analysis:ReportRetention`     | `1.00:00:00` (24 hours)                                                     | Positive duration                                                                               |
| `Hub:Analysis:RunRetention`        | `30.00:00:00` (30 days)                                                     | Positive, longer than `ReportRetention`. When a finished run's row is deleted                   |
| `Hub:UpdateCheck:Enabled`          | `true`                                                                      | Whether the Hub asks GitHub for the newest published release                                    |
| `Hub:UpdateCheck:Interval`         | `1.00:00:00` (1 day)                                                        | At least 15 minutes                                                                             |
| `Hub:UpdateCheck:EngineEndpoint`   | GitHub releases API for `Institut-du-Numerique-Responsable/perf-sentinel`   | Absolute HTTPS, no credentials, query, or fragment                                              |
| `Hub:UpdateCheck:HubEndpoint`      | GitHub releases API for `Institut-du-Numerique-Responsable/PerfSentinelHub` | Absolute HTTPS, no credentials, query, or fragment                                              |
| `Hub:Auth:*`                       | off                                                                         | OAuth2 browser sign-in, can be limited to groups, see [AUTHENTICATION.md](AUTHENTICATION.md)    |
| `Hub:AckRelay:TrustIdentityHeader` | `false`                                                                     | Lets the ack relay take its caller from `Analysis:IdentityHeader`, see below                    |
| `Hub:Access:*`                     | off                                                                         | Access control, see below and [ACCESS-CONTROL.md](ACCESS-CONTROL.md)                            |
| `Hub:Disclosures:*`                | off                                                                         | Periodic disclosures a scheduled job publishes, see [below](#disclosures)                       |
| `Hub:Sources`                      | none                                                                        | At least one source                                                                             |

## Per-source settings

| Setting                          | Default  | Validation                                                                                                   |
|----------------------------------|----------|--------------------------------------------------------------------------------------------------------------|
| `Sources[].Id`                   | none     | Unique, 1 to 64 ASCII letters, digits, `.`, `_` or `-`                                                       |
| `Sources[].Name`                 | none     | Non-empty                                                                                                    |
| `Sources[].Environment`          | none     | Non-empty                                                                                                    |
| `Sources[].Kind`                 | `daemon` | One of `daemon`, `tempo`, `jaeger_query`. Only a daemon is polled, and only a daemon may carry an import key |
| `Sources[].RetentionHours`       | none     | Trace backends only, 1 hour to 10 years                                                                      |
| `Sources[].BaseUrl`              | none     | Required. Absolute HTTP(S), no credentials, query, or fragment                                               |
| `Sources[].PublicUrl`            | none     | Optional, same shape as `BaseUrl`. What printed commands and live reports target                             |
| `Sources[].AuthHeaderName/Value` | none     | Both absent or both present, no newlines. A daemon's `[daemon] read_api_key` goes here as `X-API-Key`        |
| `Sources[].PublicAuthHeaderName` | none     | Requires `PublicUrl`, no spaces or controls. The header printed commands name instead of `AuthHeaderName`    |
| `Sources[].AckHeaderName/Value`  | none     | Daemons only, both or neither. The daemon's `[daemon.ack] api_key`, sent on relayed acks alone, see below    |
| `Sources[].ImportApiKey`         | none     | Optional push credential, at least 32 characters, supplied through a Secret                                  |
| `Sources[].PgStat`, `MySqlStat`  | none     | A Prometheus the engine scrapes for a report's `pg_stat` and `mysql_stat` tabs, see below                   |

`Hub:DatabasePath` and `Hub:Analysis:ReportDirectory` default to `/data/hub.db` and
`/data/reports`, which are the container's paths. Both are validated with
`Path.IsPathFullyQualified`, which rejects a leading slash with no drive on Windows, so a
Windows or macOS host must set both or the Hub refuses to start and names the offending key.

`RetentionHours` is declared rather than measured, because no backend API exposes it. It
carries the same caveat as `Environment`: it keeps a stale claim until someone edits it.

`BaseUrl` keeps a path prefix, so `https://gw/perf-sentinel/` polls
`https://gw/perf-sentinel/api/status`.

`BaseUrl` is where the Hub reaches a source. A Hub inside the cluster reads a Service name
that nothing outside the cluster resolves, so a command printed from it would not run on a
workstation, and a live report would point the viewer's browser at it. `PublicUrl` is the
address from outside: an Ingress host, or `http://localhost:14318` for readers who forward
the port themselves. Printed commands and live reports use it. The Hub's own polls, reads
and runs keep `BaseUrl`, so they stay on the cluster network. Without `PublicUrl`, both use
`BaseUrl`.

Printed commands name the header `AuthHeaderName` names, which fits a port-forward: it
reaches the same daemon. An Ingress that authenticates on its own terms takes
`PublicAuthHeaderName` instead. It is a name without a value, since the Hub never calls the
public route and the reader supplies their own credential.

A Hub served over HTTPS needs an HTTPS `PublicUrl` for its reports to go live: browsers
block HTTP calls from an HTTPS page, with an exception for `localhost` in most of them.

## Database statistics

A report's `pg_stat` and `mysql_stat` tabs rank the statements a database spent its time on,
and cross-link them with the findings. The Hub fills them when a source carries `PgStat` or
`MySqlStat`: at render time the engine scrapes that Prometheus, with the keys passed as the
`report` flags of the same name (`--pg-stat-prometheus`, `--pg-stat-metric`, and so on).

| Key             | Engine flag                                    | Notes                                                    |
|-----------------|------------------------------------------------|----------------------------------------------------------|
| `Prometheus`    | `--pg-stat-prometheus`, `--mysql-stat-prometheus` | Required, an HTTP(S) URL                              |
| `Metric`        | `-metric`                                      | The time series, when the exporter names its own         |
| `QueryLabel`    | `-query-label`                                 | The label carrying the statement text                    |
| `CallsMetric`   | `-calls-metric`                                | The call counter series, empty to skip it               |
| `Unit`          | `-unit`                                        | `seconds`, `milliseconds`, and `picoseconds` for MySQL   |
| `Selector`      | `-selector`                                    | Label matchers, engine 0.31.0 and later                  |
| `Top`           | `-top`                                         | 1 to 1000 rows per ranking                               |
| `AuthHeaderName/Value` | none                                    | Both or neither, for an authenticated Prometheus        |

`Selector` keeps one tenant, or the primary alone, when the exporter serves several:
`namespace="shop",db_role="primary"`. Without it, their statements are ranked together. The
auth header reaches the engine in `PERF_SENTINEL_PGSTAT_AUTH_HEADER` or
`PERF_SENTINEL_MYSQLSTAT_AUTH_HEADER`, never on its command line, and its value belongs in a
Secret: under Helm, `pgStat.authSecretName` and `pgStat.authSecretKey`.

The engine fails a whole render when it cannot scrape, so the Hub then renders the report
again without the tab and logs event 1603: a Prometheus that is down costs the tab, not the
analysis. The counters are cumulated since the last `pg_stat_statements_reset()`, so the
ranking covers that span and not the window of the analysis.

## The ack credential

`AckHeaderName` and `AckHeaderValue` are a second credential, apart from the read pair. The
Hub sends it on one kind of request only, the ack or the revoke it relays to that daemon,
and keeps the read pair for everything else. It is the daemon's `[daemon.ack] api_key`,
sent as `X-API-Key`, or as `Authorization` with a `Bearer` value. Without it the Hub never
writes to that daemon, and `/api/sources` reports `ack_relay: false` for the source.
Leaving both unset is how a source is kept out of the ack page, where its row then offers
nothing, a CI daemon whose acks only change by pull request for instance, see
[LAUNCHER.md](LAUNCHER.md#pull-requests-only-in-an-environment). Like `AuthHeaderValue`,
the value belongs in a Secret: under Helm that is `ackSecretName` and `ackSecretKey`, and
only the header name reaches the ConfigMap.

The Hub refuses to start when the pair sits on a trace backend, which has no ack route, or
when it carries the same key as the source's `AuthHeaderValue`, with a `Bearer` scheme
ignored on either side. The daemon refuses a `read_api_key` equal to its ack key for the
same reason: a read key that can write is a write key. A Hub that reads a daemon with its
ack key therefore needs a `[daemon] read_api_key` on that daemon before it can relay.

The relay has to know who acks. A `Hub:Auth` session always says. The header a reverse
proxy sets, `Hub:Analysis:IdentityHeader`, is a claim the Hub cannot verify, so it names the
caller only once `Hub:AckRelay:TrustIdentityHeader` is `true`, `hub.ackRelay.trustIdentityHeader`
under Helm. Set it only behind a proxy that writes the header itself and strips the one a
client sent. With neither, the relay can identify nobody and refuses every caller, and the
Hub logs one warning at startup that names the sources whose credential will never be sent.

## Access control

`Hub:Access` says who reaches which findings, sources and runs: projects, roles, grants,
internal groups, admins, machine tokens and local accounts. All of it is configuration, the database stores
no user and no membership, and under Helm the whole block is `hub.access`, passed to the Hub
as is. How it applies to each route, machine tokens and sessions are in
[ACCESS-CONTROL.md](ACCESS-CONTROL.md). Off, the default, nothing under `Hub:Access` is
validated or used and the Hub answers exactly as before, but the values are still bound, so
`ExpiresAt` must already be an ISO 8601 date such as `2027-06-30T00:00:00Z` or the Hub does
not start. Every list is empty by default. With `Enabled` on, each rule below is checked at
startup, and each message names its key and index and never echoes a token; the chart's
schema checks the cross-key rules at `helm upgrade`, before `Recreate` stops the running pod.

| Setting                      | Validation                                                                                                                                                    |
|------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Hub:Access:Enabled`         | `false` by default. Requires `Hub:Auth:Enabled`, `Hub:MetricsPort`, an entry in `Admins` or `AdminGroups`, and `Hub:AckRelay:TrustIdentityHeader` off        |
| `Hub:Auth:SubjectClaim`      | `sub` by default, read only with `Enabled`: `sub`, `oid`, `uuid` or `account_id`, ids a provider never reassigns                                             |
| `Hub:Auth:TrustedProxyNetworks` | Empty by default. CIDR networks whose `X-Forwarded-For` and `X-Forwarded-Proto` are trusted, one hop deep; each entry must parse. Terminal sign-in (`perf-sentinel login`) is offered only when it is set, see [AUTHENTICATION.md](AUTHENTICATION.md#terminal-sign-in), and `LocalAccounts` requires it |
| `Hub:Access:Admins`          | Subjects holding the built-in `admin` role on `*`: 1 to 256 characters, no control character, no `machine:` prefix, and `local:<name>` only for a declared local account. Not empty with `AdminGroups` |
| `Hub:Access:AdminGroups`     | Group references holding `admin` on `*`: `internal:<name>` or a provider group, see below                                                                     |
| `Hub:Access:Groups[]`        | `{ Name, Members[] }`, referenced as `internal:<Name>`. Name of 1 to 64 ASCII letters, digits, `.`, `_` or `-`, unique with case ignored; members as `Admins` |
| `Hub:Access:Projects[]`      | `{ Name, Patterns[] }`, named as a group, with at least one pattern `{ Source, Service, Attribute }` setting one part or more                                 |
| `Hub:Access:Roles[]`         | `{ Name, Permissions[] }`, named as a group but not `reader`, `member` or `admin`, case ignored; permissions below, each once, never `access.admin`           |
| `Hub:Access:Grants[]`        | `{ Group, Project, Role }`, all three set. `Project` is a declared project or `*`, `Role` built in or declared, and `admin` is granted on `*` only            |
| `Hub:Access:MachineTokens[]` | `{ Name, Group, Token, ExpiresAt }`, see below                                                                                                                |
| `Hub:Access:LocalAccounts[]` | `{ Name, PasswordHash }`, see below                                                                                                                           |

**Permissions and roles.** The permissions are `findings.read`, `traces.read`,
`sources.read`, `analyses.run`, `acks.write`, `actions.write`, `labels.write`, `disclosures.read`,
`disclosures.manage` and `recaps.read`, plus `access.admin`, which only the built-in
`admin` role carries. The two disclosure permissions and `recaps.read` apply to the whole
Hub, so a role carrying any of them is granted on `*` only. The built-in roles are
`reader` (`findings.read`, `traces.read`, `analyses.run`), `member` (`reader`, `acks.write`,
`actions.write` and `labels.write`) and `admin` (all eleven, on `*` only). `reader` is not read-only: it starts analyses. A read-only role
is a configured one, such as `auditor` with `findings.read` and `traces.read`.

**Patterns.** A record is in a project when one of its patterns matches. A part is exact,
or a prefix: at least one character, then a single `*` at the end. A bare `*` is refused in
every part: `*` exists only as a grant's project, which also covers the records no project
matches. `Source` takes a source id's characters and must name or prefix a configured
source. `Service` is 1 to 256 characters with no control character. `Attribute` is
`key=value`, split on the first `=`, so `team=a=b` has the key `team`: the key is exact, 1
to 256 characters with no `*`, and the value a part of 1 to 256. An attribute key matches
only on the daemons that capture it in `[detection] grouping_attributes`, see
[DEPLOYMENT.md](DEPLOYMENT.md#grouping-attributes-are-chosen-at-each-daemon). Every
comparison is ordinal: case matters, and `%` or `_` mean nothing.

**Group references.** `internal:<name>`, with the prefix in lower case, names a declared
internal group. Anything else is a provider group, spelled as the userinfo field
`Hub:Auth:GroupsClaim` sends it, which must then name a field. A provider reference is
refused when it is `*`, blank, longer than 256 characters, holds a control character, or
starts with `internal:` in another case.

**Provider groups in `AdminGroups` and in grants** must be names only the organisation can
create: the full group path on Keycloak (`/teams/shop-admin`, "Full group path" on in the
mapper), the full path under the organisation's own group on GitLab (`acme/hub-admins`),
never a bare top-level name. `Hub:Auth:GroupsClaim` must name a field the provider manages
and users cannot edit. Where users create groups of any name, gitlab.com and self-managed
GitLab by default, anyone the provider signs in can create a top-level `platform-admins` and
find it in their groups claim: there a provider admin group is unsafe unless
`Hub:Auth:RequiredGroups` also limits sign-in to the organisation's members, see
[AUTHENTICATION.md](AUTHENTICATION.md). `Admins`, by subject, stays the break-glass path
when a provider admin group is misconfigured.

**Machine tokens** are credentials for CI jobs and dashboards, and hold `findings.read` at
most. `Name` matches `^[a-z0-9][a-z0-9._-]{0,63}$` and is unique. `Group` is an `internal:`
group with no members, since its grants are sized for a machine and a person added later
would silently gain them. `Token` is 32 to 512 characters of the RFC 6750 `b64token`
alphabet, `A-Z`, `a-z`, `0-9`, `-`, `.`, `_`, `~`, `+` and `/` with trailing `=` allowed,
does not start with `pshub_`, differs from every other machine token and every import key,
and is trimmed like an import key. `ExpiresAt` is set, at most 365 days ahead; a token that
has already expired does not stop the Hub. Under Helm an item is `{ name, group, expiresAt,
secretName, secretKey }`: the token is never in the values, and with `enabled` the chart
reads it from that Secret into `Hub__Access__MachineTokens__<index>__Token`. The chart
refuses a duplicate or malformed name, a missing Secret reference and an `expiresAt` that
is not an ISO 8601 date and time. No message ever echoes a token. An expired entry is
logged at startup (event `2001`) and refused at each request.

**Local accounts** are break-glass accounts for when the provider is down, signed in on
the Hub's own page as `local:<name>`, see
[AUTHENTICATION.md](AUTHENTICATION.md#local-accounts). `Name` matches
`^[a-z0-9][a-z0-9._-]{0,63}$` and is unique. `PasswordHash` is the line
`PerfSentinelHub hash-password` prints, `$pbkdf2-sha256$i=600000$<salt>$<key>`, trimmed
like a token; any other shape or iteration count is refused. `Admins` or a group's
`Members` must list `local:<name>`, and `Hub:Auth:TrustedProxyNetworks` must not be empty.
Under Helm an item is `{ name, secretName, secretKey }`: with `enabled` the chart reads the
hash from that Secret into `Hub__Access__LocalAccounts__<index>__PasswordHash`, and refuses
a duplicate or malformed name and a missing Secret reference; the schema refuses local
accounts without `auth.trustedProxyNetworks`. No message ever echoes a hash.

## Disclosures

`Hub:Disclosures` turns on the periodic disclosures a scheduled job publishes, see
[API.md](API.md#disclosures). It is on when `ImportKey` is set, and off, the default, every
disclosure route answers `404`, the launcher shows no Disclosures tab, and no webhook is
called. Each rule below is checked at startup and its message names the key.

| Setting                                  | Default | Validation                                                                                                                         |
|------------------------------------------|---------|------------------------------------------------------------------------------------------------------------------------------------|
| `Hub:Disclosures:ImportKey`              | none    | At least 32 characters with no control character, distinct from every source `ImportApiKey` and machine token. Trimmed, supplied through a Secret |
| `Hub:Disclosures:AllowInternal`          | `false` | Takes reports whose confidentiality is `internal`. Requires `Hub:Access:Enabled`                                                   |
| `Hub:Disclosures:ExpectedIdentity`       | none    | With `ExpectedIssuer`, both or neither. The signer passed to the engine and printed in the verify command                         |
| `Hub:Disclosures:ExpectedIssuer`         | none    | With `ExpectedIdentity`                                                                                                            |
| `Hub:Disclosures:ShareLinkDays`          | `7`     | 1 to 30. A share link's life when its creator names none                                                                          |
| `Hub:Disclosures:PublicUrl`              | none    | Absolute HTTP(S) URL with no path. The Hub's own address as a webhook receiver reaches it. Required by a webhook with `IncludeShareLink` |
| `Hub:Disclosures:Webhooks[]`             | none    | `{ Name, Url, Secret, Confidentiality, IncludeShareLink, ShareLinkDays }`, see below                                               |

**The import key** opens one door only. A key equal to a source's import key or to a machine
token would be leaked by either, so the Hub refuses it. A CI sends it as `X-API-Key`.

**An internal report** carries detail meant for the organisation alone. With
`AllowInternal`, the Hub keeps it for the holders of `disclosures.read`, and never shares it
nor sends it to a webhook. Without access control, anyone who reaches the Hub would read it,
so the Hub refuses the setting there. Off, the import answers `422 internal_refused`.

**The expected signer.** `ExpectedIdentity` and `ExpectedIssuer` are the identity and issuer a
reader passes to `perf-sentinel verify-hash`. The Hub hands them to `disclose-pdf`, which
prints them in the PDF's verify command, and the launcher prints them in its own. The Hub
never checks a signature.

**Webhooks.** `Name` is 1 to 64 ASCII letters, digits, `.`, `_` or `-`, unique, and it is the
only thing the Hub shows or logs about a webhook. `Url` is an absolute `https` URL, or `http`
to a loopback address, with no credentials and no fragment, and is never stored nor shown,
since a chat webhook URL carries its own secret. `Secret` is at least 32 characters with no
control character, the HMAC key the receiver checks. `Confidentiality` is `public`, the only
value taken: an internal report never leaves the Hub. `IncludeShareLink` adds a share link to
each `published` event, valid `ShareLinkDays`, 1 to 30, 7 by default, and needs
`Hub:Disclosures:PublicUrl`, since the link has to name the Hub. The payload and the retries
are in [API.md](API.md#webhooks).

Under Helm the block is `hub.disclosures`: `importSecretName` and `importSecretKey` name the
Secret holding the import key, and the chart writes the rest of the section only once that is
set. `allowInternal`, `expectedIdentity`, `expectedIssuer`, `shareLinkDays` and `publicUrl`
follow the settings above. A webhook is `{ name, secretName, urlKey, secretKey,
includeShareLink, shareLinkDays }`: its URL and its secret both come from that Secret.

## Recap reports

A recap of a closed period can be frozen into a stored report, served as self-contained HTML in a
management and an engineering version, and delivered by signed webhook and by e-mail. Nothing here
is on by default: a Hub with no `Hub:Recaps:Subscriptions` generates no report on its own and sends
nothing. Each rule below is checked at startup and its message names the key. `Hub:Recaps` also
holds `MovementThreshold`, in the settings table above.

| Setting                      | Default                      | Validation                                                                                                                                     |
|------------------------------|------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------|
| `Hub:Recaps:Retention`       | `365.00:00:00`               | At least 32 days, so a month's report outlives its month as the last closed one. How long a stored report and its deliveries are kept          |
| `Hub:Recaps:MaxDelay`        | `06:00:00`                   | Above zero, at most 7 days. How long a report waits for the ledgers to seal its last day                                                       |
| `Hub:Recaps:PublicUrl`       | `Hub:Disclosures:PublicUrl`  | As that one: an absolute HTTP(S) URL with no path. The address the engineering link names                                                      |
| `Hub:Recaps:Webhooks[]`      | none                         | `{ Name, Url, Secret, Confidentiality, IncludeShareLink, ShareLinkDays }`, the disclosure shape, see below                                      |
| `Hub:Recaps:Smtp:*`          | off                          | `Host`, `Port`, `StartTls`, `UserName`, `Password`, `From`, see below                                                                          |
| `Hub:Recaps:Subscriptions[]` | none                         | At most 100, see below                                                                                                                         |

**Webhooks** follow the rules of [disclosure webhooks](#disclosures): `Name`, `Url`, `Secret` and
`Confidentiality` mean the same, and the signature is the same. `IncludeShareLink` and
`ShareLinkDays` apply to disclosures only, and the Hub refuses a recap webhook that sets
`IncludeShareLink`. A recap webhook carries the management snapshot only.

**The SMTP relay.** `Host` turns the e-mail on, and with it `From`, which is required. The Hub
hands each message to this relay and never looks up a recipient's mail server, so it needs no DNS
record of its own. Use a `From` the relay already sends for, whose SPF and DKIM then cover the
Hub's mail. `Port` is 587 with `StartTls` on, the default: the Hub speaks STARTTLS, never implicit
TLS, so 465 is refused. `UserName` and `Password` go together, authenticate with `AUTH LOGIN`, and
need `StartTls` unless the relay is on loopback, since a password never travels in clear. A
Microsoft 365 or Gmail account restricted to OAuth does not offer `AUTH LOGIN`: point the Hub at
the organisation's relay instead. `From` is one address with no display name. The password comes
through a Secret (`Hub__Recaps__Smtp__Password`).

**Subscriptions.** Each one is `{ Name, Cadences, Views, Environment, SourceId, Service, Grouping,
Webhooks, Emails }`. `Name` is 1 to 64 ASCII letters, digits, `.`, `_` or `-`, and unique.
`Cadences` lists `day`, `week` or `month`, each once, at least one. `Views` lists `pilotage`,
`tech` or both, each once. `Environment` and `SourceId` must match a declared source, and a
`SourceId` must belong to the `Environment` when both are set. `Service` and `Grouping` are 1 to
256 characters with no control character. `Webhooks` names entries of `Hub:Recaps:Webhooks`, and
`Emails` holds at most 50 distinct addresses without a display name, which needs
`Hub:Recaps:Smtp:Host`. A subscription with no destination is valid: it archives its reports and
delivers none.

Under Helm, the chart does not carry the `Hub:Recaps` block yet. Pass it as `Hub__Recaps__*`
environment variables until it does.

## What a source is, and what is measured

The list is configuration, never discovery. Nothing is auto-detected, the launcher cannot
add a source, and the Hub refuses to start with none.

That splits every row on the fleet screen in two. `Id`, `Name`, `Environment`, `Kind`,
`BaseUrl`, `PublicUrl` and `RetentionHours` are declared: taken from this file as written and never
checked against anything. `reachable`, `last_success`, `unreachable_since`,
`producer_version` and `last_error` are observed, written by the poll. A dashed outline in
the launcher marks the declared half, which is why a misconfigured deployment can label
production as staging and nothing will contradict it.

Only a daemon is polled. Its `api/status` supplies `producer_version`, and its
`api/findings` is the safety net behind the push.

Where it goes: `Hub:Sources` in `appsettings.json`, the `Hub__Sources__N__*` environment
variables, or `sources[]` in the Helm values. The three are the same setting, and `Kind`
is what decides which half of the screen a row lands in.

```yaml
sources:
  - id: checkout-prod
    name: Checkout production
    environment: production
    kind: daemon
    baseUrl: http://perf-sentinel.observability:4318
    importSecretName: hub-import-keys    # the push credential, never inline
    importSecretKey: checkout-prod
    ackHeaderName: X-API-Key             # optional, lets the Hub relay acks to this daemon
    ackSecretName: hub-ack-keys          # the daemon's [daemon.ack] api_key, never inline
    ackSecretKey: checkout-prod
  - id: victoria-eu
    name: Victoria Traces EU
    environment: staging
    kind: jaeger_query                   # Victoria Traces speaks the Jaeger query API
    baseUrl: http://victoria-traces.observability:10428
    retentionHours: 72
```

The same pair as environment variables, one index per source:

```bash
Hub__Sources__0__Id=checkout-prod
Hub__Sources__0__Kind=daemon
Hub__Sources__0__BaseUrl=http://perf-sentinel.observability:4318
Hub__Sources__0__AckHeaderName=X-API-Key
Hub__Sources__0__AckHeaderValue="$ACK_API_KEY"   # from your secret store, never a literal
Hub__Sources__1__Id=victoria-eu
Hub__Sources__1__Kind=jaeger_query
Hub__Sources__1__BaseUrl=http://victoria-traces.observability:10428
Hub__Sources__1__RetentionHours=72
```

A trace backend is never contacted at all until someone runs an analysis: no route in the
Hub reads a Tempo. The Hub only launches the engine against it, with the subcommand its
kind implies, `tempo` for `tempo` and `jaeger-query` otherwise. That is why such a source
shows no producer version and no last success, and it is not a fault.

## Where the Hub connects

Every outbound request goes to a configured `Sources[].BaseUrl`, with one exception on by default. Once
a day the Hub asks the GitHub releases API for the newest published version of the engine
and of itself, so the version chip can say that what you are running is no longer current.
The request is an unauthenticated GET and carries no identifier of your deployment.

For a deployment with no egress, set `Hub:UpdateCheck:Enabled` to `false`. The chip then
shows nothing rather than claiming you are current, which is also what it shows when the
request fails.

Recap reports add two more destinations, both declared by you and both off until you declare
them: each `Hub:Recaps:Webhooks[].Url` receives the signed report event, and `Hub:Recaps:Smtp:Host`
receives each e-mail. The Hub reaches no other mail server.

## An https source with a private CA

The Hub validates a source's certificate against the container's trust store. An
in-cluster daemon with a self-signed or internally-issued certificate is refused with
`PartialChain` until its CA is in there.

The runtime image is chiseled and has no shell, so `update-ca-certificates` cannot run in
it. Point `SSL_CERT_FILE` at a bundle mounted from a ConfigMap instead:

```yaml
env:
  - name: SSL_CERT_FILE
    value: /etc/perf-sentinel-hub/certs/bundle.crt
```

The bundle must be the public roots and your CA concatenated, in that order, not the CA
alone. The variable replaces the default file rather than adding to it, and a Hub that
trusts only your CA cannot reach anything on the public internet.

Verified against the runtime this image is built on: with no variable the private
certificate is refused and public TLS works, with the concatenated bundle both work.

The analysis engine the launcher starts inherits this variable. From perf-sentinel
0.25.5 it trusts the certificates of `SSL_CERT_FILE` next to its own bundled roots, so the
same bundle lets the launcher analyze a Tempo or Jaeger source served with a private
certificate. The engine also reads `HTTPS_PROXY`, `ALL_PROXY` and `NO_PROXY` for its
`https://` calls: a Hub that carries `HTTPS_PROXY` sends them through that proxy, so an
in-cluster `https://` source belongs in `NO_PROXY`.
