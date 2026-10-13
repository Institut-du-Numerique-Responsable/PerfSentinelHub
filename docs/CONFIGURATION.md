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
| `Hub:DefaultReadLimit`             | `1000`                                                                      | 1 to `MaxReadLimit`                                                                             |
| `Hub:MaxReadLimit`                 | `10000`                                                                     | 1 to 10000                                                                                      |
| `Hub:Analysis:EngineBinaryPath`    | none                                                                        | Optional, absolute path to the perf-sentinel binary. Absent means analysis runs are unavailable |
| `Hub:Analysis:ReportDirectory`     | `/data/reports`                                                             | Absolute, writable. Rendered reports live here                                                  |
| `Hub:Analysis:IdentityHeader`      | `X-Forwarded-User`                                                          | Header a reverse proxy sets with the requester's identity. Ignored for a `Hub:Auth` session     |
| `Hub:Analysis:Workers`             | `2`                                                                         | 1 to 16                                                                                         |
| `Hub:Analysis:MaxTracesCap`        | `2000`                                                                      | 1 to 10000, the engine's own limit on `--max-traces`                                            |
| `Hub:Analysis:MaxTracesEmbedded`   | `50`                                                                        | 0 to 10000. Span trees embedded in the report. Setting it opts the sink out of size targeting   |
| `Hub:Analysis:Timeout`             | `00:05:00`                                                                  | Positive, at most one hour                                                                      |
| `Hub:Analysis:ReportRetention`     | `1.00:00:00` (24 hours)                                                     | Positive duration                                                                               |
| `Hub:Analysis:RunRetention`        | `30.00:00:00` (30 days)                                                     | Positive, longer than `ReportRetention`. When a finished run's row is deleted                   |
| `Hub:UpdateCheck:Enabled`          | `true`                                                                      | Whether the Hub asks GitHub for the newest published release                                    |
| `Hub:UpdateCheck:Interval`         | `1.00:00:00` (1 day)                                                        | At least 15 minutes                                                                             |
| `Hub:UpdateCheck:EngineEndpoint`   | GitHub releases API for `Institut-du-Numerique-Responsable/perf-sentinel`   | Absolute HTTPS, no credentials, query, or fragment                                              |
| `Hub:UpdateCheck:HubEndpoint`      | GitHub releases API for `Institut-du-Numerique-Responsable/PerfSentinelHub` | Absolute HTTPS, no credentials, query, or fragment                                              |
| `Hub:Auth:*`                       | off                                                                         | OAuth2 browser sign-in, can be limited to groups, see [AUTHENTICATION.md](AUTHENTICATION.md)    |
| `Hub:AckRelay:TrustIdentityHeader` | `false`                                                                     | Lets the ack relay take its caller from `Analysis:IdentityHeader`, see below                    |
| `Hub:Access:*`                     | off                                                                         | Access control model, refused as `Enabled` in this release, see below                           |
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
internal groups, admins and machine tokens. All of it is configuration, the database stores
no user and no membership, and under Helm the whole block is `hub.access`, passed to the Hub
as is. This release ships the model and its validation ahead of their enforcement: with
`Hub:Access:Enabled` set to `true` the Hub refuses to start, with "Hub:Access:Enabled is not
available in this build yet.", and the chart's schema refuses `hub.access.enabled: true`
before the running pod is stopped. A Hub that believed it filtered would otherwise serve
every caller everything. Off, the default, nothing under `Hub:Access` is validated or used
and the Hub answers exactly as before, but the values are still bound, so `ExpiresAt` must
already be an ISO 8601 date such as `2027-06-30T00:00:00Z` or the Hub does not start. Every
list is empty by default.

| Setting                      | Validation                                                                                                                                                    |
|------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Hub:Access:Enabled`         | `false` by default, refused as `true` in this release. Requires `Hub:Auth:Enabled`, and `Hub:AckRelay:TrustIdentityHeader` off                                |
| `Hub:Access:Admins`          | Subjects holding the built-in `admin` role on `*`: 1 to 256 characters, no control character, no `machine:` or `local:` prefix. Not empty with `AdminGroups`  |
| `Hub:Access:AdminGroups`     | Group references holding `admin` on `*`: `internal:<name>` or a provider group, see below                                                                     |
| `Hub:Access:Groups[]`        | `{ Name, Members[] }`, referenced as `internal:<Name>`. Name of 1 to 64 ASCII letters, digits, `.`, `_` or `-`, unique with case ignored; members as `Admins` |
| `Hub:Access:Projects[]`      | `{ Name, Patterns[] }`, named as a group, with at least one pattern `{ Source, Service, Attribute }` setting one part or more                                 |
| `Hub:Access:Roles[]`         | `{ Name, Permissions[] }`, named as a group but not `reader`, `member` or `admin`, case ignored; permissions below, each once, never `access.admin`           |
| `Hub:Access:Grants[]`        | `{ Group, Project, Role }`, all three set. `Project` is a declared project or `*`, `Role` built in or declared, and `admin` is granted on `*` only            |
| `Hub:Access:MachineTokens[]` | `{ Name, Group, Token, ExpiresAt }`, see below                                                                                                                |

**Permissions and roles.** The permissions are `findings.read`, `traces.read`,
`sources.read`, `analyses.run` and `acks.write`, plus `access.admin`, which only the
built-in `admin` role carries. The built-in roles are `reader` (`findings.read`,
`traces.read`, `analyses.run`), `member` (`reader` and `acks.write`) and `admin` (all six,
on `*` only). `reader` is not read-only: it starts analyses. A read-only role is a
configured one, such as `auditor` with `findings.read` and `traces.read`.

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
secretName, secretKey }`: the token is never in the values, and once enforcement ships the
chart reads it from that Secret. This release only records the reference; the chart refuses
a duplicate or malformed name, a missing Secret reference and an `expiresAt` that is not an
ISO 8601 date and time. No message ever echoes a token.

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

Every outbound request goes to a configured `Sources[].BaseUrl`, with one exception. Once
a day the Hub asks the GitHub releases API for the newest published version of the engine
and of itself, so the version chip can say that what you are running is no longer current.
The request is an unauthenticated GET and carries no identifier of your deployment.

For a deployment with no egress, set `Hub:UpdateCheck:Enabled` to `false`. The chip then
shows nothing rather than claiming you are current, which is also what it shows when the
request fails.

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
