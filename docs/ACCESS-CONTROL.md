# Access control

Several teams share one Hub. With access control on, each caller reaches what its grants
cover and nothing else: in the browser, and for machines such as CI jobs, dashboards and
IDE plugins. It is off by default, and off the Hub answers exactly as it does without it.

Access control is configuration, under `Hub:Access` (Helm `hub.access`). The Hub stores no
user and no membership: people and their provider groups come from the identity provider
at sign-in, and everything else from the values. A change applies at the next restart,
and a chart rollout restarts the pod.

## Before turning it on

`Hub:Access:Enabled` (Helm `hub.access.enabled`) refuses to start, and the chart's schema
refuses it at `helm upgrade`, unless:

- `Hub:Auth:Enabled` is on, see [AUTHENTICATION.md](AUTHENTICATION.md);
- `Hub:MetricsPort` is set, since `/metrics` then answers on that port alone (see
  [OPERATIONS.md](OPERATIONS.md#metrics));
- `Hub:Access:Admins` or `Hub:Access:AdminGroups` holds an entry;
- `Hub:AckRelay:TrustIdentityHeader` is off: only a Hub session or token names a caller.

Check these as well, which no validator can:

- **IDE plugins.** Once access is on, `/api/findings` needs a credential. The JetBrains
  plugin sends one from the release after 0.1.0, which adds a credential per endpoint
  (robintra/perf-sentinel-jetbrains-plugin#31). An older plugin gets `401` and cannot
  recover. Install that release on every IDE that reads the Hub first.
- **Dashboards.** Grafana's Hub datasource needs a machine token, see
  [Machine tokens](#machine-tokens).
- **`Hub:Auth:RequiredGroups`.** Recommended with access on: it is the outer gate, who may
  open a session at all, and access decides what each session reaches inside it. Without
  it any account the provider signs in gets a session, which reaches nothing without a
  grant (`/api/status` answers `403`) but still loads the launcher.
- **The network.** Users must not reach the daemons and trace backends directly; access
  control covers what the Hub serves.
- **A proxy in front of the Hub** must neither inject nor forward an `Authorization`
  header. A request carrying a bearer is judged on that bearer alone, so a proxy that
  sends its own token (oauth2-proxy with `--pass-authorization-header` or
  `--set-authorization-header`, for instance) turns every browser request into a `401`.

## The model

### Subject and identity

Every caller has two strings. The **subject** is the authorization key: `Admins` and the
members of internal groups list subjects, and nothing else is compared. The **identity**
is the name people read: the topbar, `/api/status`, a run's `requested_by` and an ack's
`by`. Nothing is authorized on the identity, so a user who renames themselves at the
provider changes a label, not their rights.

| Caller           | Subject                         | Identity                         | Groups                                       |
|------------------|---------------------------------|----------------------------------|----------------------------------------------|
| Browser session  | `Hub:Auth:SubjectClaim`'s value | `Hub:Auth:IdentityClaim`'s value | Provider groups at sign-in, internal groups  |
| Machine token    | `machine:<name>`                | `machine:<name>`                 | Its configured group alone                   |
| Local account    | `local:<name>`                  | `local:<name>`                   | Internal groups alone                        |

A browser session also carries the name the provider gave the user, from
`Hub:Auth:DisplayNameClaim` (default `name`). It is shown as an action's owner and never
used for access.

`Hub:Auth:SubjectClaim` (default `sub`) accepts only `sub`, `oid`, `uuid` and
`account_id`, ids a provider never reassigns. `sub` fits Keycloak, Google, GitLab and
Entra ID; Bitbucket Cloud has no `sub` and takes `account_id`. A userinfo whose subject
field is missing, not a string, blank or longer than 256 characters fails the sign-in.
So does a subject or an identity starting with `local:` or `machine:` (case ignored):
those prefixes are reserved.

### Groups

**Provider groups** are read at sign-in from the userinfo field `Hub:Auth:GroupsClaim`
names, the one `RequiredGroups` reads, with the same rules: a list of strings or a single
string. The session keeps only the provider groups a grant or `AdminGroups` names, never a
value starting with `internal:`. The names it dropped are logged at Debug (event `1902`),
which shows the exact spelling the provider sends: `acme/shop` on GitLab, `/shop` on
Keycloak with *Full group path* on.

A provider group in a grant or in `AdminGroups` must be a name only the organisation can
create: the full path under its own group on GitLab (`acme/hub-admins`, never a bare
`hub-admins`), the full group path on Keycloak, and a `GroupsClaim` field users cannot
edit. Where users create groups of any name (gitlab.com, and self-managed GitLab by
default), anyone could create a top-level group of the right name: a provider entry in
`AdminGroups` is safe there only with `Hub:Auth:RequiredGroups` set to the organisation's
group.

**Internal groups** (`Hub:Access:Groups`) are declared in the values, referenced as
`internal:<name>`, and list subjects, `local:<name>` for a local account. Membership is
read on every request.

### Projects

A project is a list of patterns; a record is in the project when one pattern matches. A
pattern sets one or more of:

- `source`: a configured source id;
- `service`: a service name;
- `attribute`: `key=value`, split on the first `=`, matched against the grouping
  attributes a daemon captured. A key reaches the Hub only from the daemons whose
  `[detection] grouping_attributes` lists it, see [DEPLOYMENT.md](DEPLOYMENT.md).

A part is exact, or a prefix ending in one `*` (`checkout-*`). Comparisons are exact,
case included. A bare `*` is not a pattern: `*` is a grant target, which covers every
record, those no project matches included.

A finding is matched per source: its copy at each source has that source's own service
and the attributes that source reported. A finding is listed when one of its copies is in
scope, and the answer is built from in-scope copies only.

### Roles and permissions

| Permission      | Allows                                                                          |
|-----------------|---------------------------------------------------------------------------------|
| `findings.read` | Findings, incidents, the read side of an ack, a daemon run's report             |
| `traces.read`   | Trace-backend runs and their reports                                            |
| `sources.read`  | A daemon's settings view, and with `findings.read` and `analyses.run` a daemon run; source level only |
| `analyses.run`  | Starting a run                                                                  |
| `acks.write`    | Acknowledging a finding, or revoking an ack                                     |
| `actions.write` | Declaring and changing an action on a finding                                   |
| `labels.write`  | Naming an endpoint or a service in business words                               |
| `disclosures.read` | The Disclosures tab, each disclosure's detail and files                     |
| `disclosures.manage` | Share links, withdrawals, delivery retries and renders of a disclosure    |
| `recaps.read`   | Reading every recap report: the list, a report and its page (Hub level)         |

| Built-in role | Permissions                                                     |
|---------------|-----------------------------------------------------------------|
| `reader`      | `findings.read`, `traces.read`, `analyses.run`                  |
| `member`      | `reader` plus `acks.write`, `actions.write` and `labels.write`  |
| `admin`       | Everything, on `*` only, plus the access configuration          |

`reader` is not read-only: it starts analyses. A read-only role is configured, for
instance `auditor: [findings.read, traces.read]`.

`sources.read` is source level: it counts only on `*` or through a pattern that sets
`source` alone. So do `analyses.run` and `findings.read` for a daemon run.

`disclosures.read`, `disclosures.manage` and `recaps.read` are Hub level: a disclosure or a
recap report belongs to no source. The built-in `admin` role carries them, `reader` and `member` never do, and a machine token
never holds them. A configured role may carry them, granted on `*` only: the Hub refuses to
start on a grant of such a role to a project.

### Grants and admins

A grant gives a group a role on a project or on `*`. Admins hold every permission on
`*`: subjects in `Admins` (the break-glass path when a provider admin group breaks),
groups in `AdminGroups`, and groups granted the built-in `admin` role on `*`. A
[local account](#local-accounts) listed there, or in an admin group, is the path that
does not depend on the provider at all.

### What a caller sees

| Route                                                                  | Needs                                                           | Refused with            |
|------------------------------------------------------------------------|-----------------------------------------------------------------|-------------------------|
| `/` and the launcher's files                                           | any caller                                                      | redirect or `401`       |
| `GET /api/status`, `GET /api/sources`                                  | any permission anywhere                                         | `403`                   |
| `GET /api/findings`, `GET /api/findings/{traceId}`                     | `findings.read`, filtered                                       | `403` when held nowhere |
| `GET /api/recap`                                                       | `findings.read`, filtered                                       | `403` when held nowhere |
| `GET /api/recap/reports`, `GET /api/recap/reports/{id}`, `GET /recaps/{id}/{view}.html` | `recaps.read` on `*`, or the creator of an on-demand report with `findings.read` | `403` (list), `404` (one report) |
| `POST /api/recap/reports`                                              | `findings.read`, filtered, and somebody to name                 | `403`                   |
| `GET /api/incidents`, `GET /api/incidents/{id}`                        | `findings.read`, filtered; the richest copy in scope            | `403`; `404`            |
| `POST /api/incidents/refresh`                                          | `findings.read`; reads only the daemons in scope                | `403`                   |
| `GET /api/correlations`                                                | `findings.read`, filtered to the episodes whose two services the caller reads at that source | `403` when held nowhere |
| `GET /api/sources/{sourceId}/daemon`                                   | `sources.read` at that source as a whole                        | `404`                   |
| `POST /api/sources/{sourceId}/acks` and `/acks/revoke`                 | `findings.read` and `acks.write` on the finding at that source  | `404`, `403`            |
| `POST` and `PATCH /api/actions`                                        | `findings.read` and `actions.write` on one copy of the finding, and somebody to name | `404`, `403` |
| `PUT /api/labels`                                                      | `findings.read` and `labels.write` on one copy of a finding at the endpoint, and somebody to name | `404`, `403` |
| `PUT /api/labels/services`                                             | `findings.read` and `labels.write` on one copy of a finding of the service, and somebody to name | `404`, `403` |
| `POST /api/analyses`                                                   | `analyses.run` and the run's read rule, below                   | `403`                   |
| `GET /api/analyses`, `GET /api/analyses/{id}`                          | the run's read rule                                             | filtered; `404`         |
| `GET /reports/{id}.html`                                               | the run's read rule                                             | `404`                   |
| `GET /metrics`                                                         | anonymous, on `Hub:MetricsPort` only                            | `404` elsewhere         |
| `GET /api/access`                                                      | any caller, even one no grant reaches; more for an admin, below |                         |
| `GET /api/disclosures`, `GET /api/disclosures/{id}` and its files      | `disclosures.read` on `*`                                       | `403`                   |
| `POST` and `DELETE` under `/api/disclosures/{id}/`                     | `disclosures.manage` on `*`, and somebody to name               | `403`                   |
| `GET /share/{token}/{name}`                                            | anonymous, the token alone                                      | `404`                   |
| `POST /api/import/findings`, `POST /api/import/incidents`, `POST /api/import/correlations`, `POST /api/import/detections`, `POST /api/import/disclosures`, `/health/*` | unchanged                                                       |                         |

`GET /api/sources` lists only the sources where the caller holds something, each with a
`permissions` array (absent with access off). A source missing from that list is
answered as an unknown one everywhere, `400` on a filter and `404` on a route.

### Status codes

- **`401`**: no valid credential. A refused bearer also gets
  `WWW-Authenticate: Bearer error="invalid_token"`.
- **`404`**, with the body a missing record gets: a record outside the caller's read
  scope, whether the request reads or writes.
- **`403 {"detail": "…"}`**: a record the caller reads but may not write; an action
  refused for who the caller is (a machine writing, starting an analysis, no permission
  at all, a listing whose permission is held nowhere); a cross-site write.
- A listing answers `403` rather than `[]` when its permission is held nowhere, so a CI
  gate whose token has no grant fails loudly instead of passing on an empty list.

A write from a browser must carry `Sec-Fetch-Site: same-origin`, which every current
browser sends: a page on a sibling subdomain cannot post with the user's cookie. A
request with a bearer is not a browser's and is not held to it. Every response carries
`Content-Security-Policy: frame-ancestors 'self'`, so no other site can frame the
launcher and click its buttons.

### Analyses and reports

A run is read, and its report opened, under one rule:

| Run                       | Read rule                                                     | Starting also needs        |
|---------------------------|---------------------------------------------------------------|----------------------------|
| Daemon D                  | `findings.read` and `sources.read` at D as a whole            | `analyses.run` at D        |
| Trace backend, service X  | `traces.read` covering X at that backend                      | `analyses.run` the same way |
| Trace backend, trace ID   | `traces.read` at the backend as a whole                       | `analyses.run` the same way |

A service run is covered when a pattern's `source` (if any) matches the backend, its
`service` (if any) matches X, and, for an attribute pattern, every copy of X the Hub holds
in the backend's environment carries that attribute, with at least one copy. The engine
exports no grouping for a span, so those copies are the only evidence of which team owns
a service. A trace ID run needs the backend as a whole, so a service grant cannot make the
Hub fetch an arbitrary trace.

There is no requester clause: whoever started a run reads it while their grant stands.

**A trace report shows every service its traces cross**, with the `result` counts. The
engine writes the report and the Hub cannot filter it, and users must see where a problem
comes from, even in another team's service. Give `traces.read` only to those who may see
cross-team traces; the built-in `reader` and `member` carry it.

With access on, a report the Hub renders is static: a live report would ack at the
daemon directly, around `acks.write`.

### Acknowledgments

An ack is judged on the finding's copy at that source alone. A caller who cannot read the
copy gets the `404` an unknown finding gets; one who reads it but may not write it gets
`403`. A caller holding `acks.write` nowhere at the source is refused before the request
body is read.

`acks.write` through an attribute pattern needs every grouping the Hub recorded for that
copy to carry the attribute with a matching value. The daemon keys an ack on the
grouping-blind signature, so an ack also silences groupings the Hub never observed and
any that appear later: **an ack made through an attribute grant may silence the same
finding for another team's deployment at that daemon**, until it expires or is revoked.
The Hub forces no expiry. Where several teams can emit the same service name, give the
owning team a source or service grant for `acks.write` and `analyses.run`.

### Actions

An action is global to the signature, as an ack is at its daemon, so it is judged on every
copy of the finding the caller reads, at any source, and allowed when one of them is
writable. A caller who reads none gets the `404` an unknown finding gets; one who reads
some but may write none gets `403`. A caller holding `actions.write` nowhere is refused
before the request body is read, and a caller the Hub cannot name is refused first of all.
Unlike an ack, an action reaches no daemon and silences nothing: it is a note the whole Hub
shares about who works on the finding.

### Endpoint labels

A label is global to a `(service, endpoint)` pair, so it is judged on every copy of every
finding at that endpoint that the caller reads, at any source, and allowed when one of them is
writable under `labels.write`. A caller who reads none gets the `404` an unknown endpoint gets;
one who reads some but may write none gets `403`. A caller holding `labels.write` nowhere is
refused before the request body is read, and a caller the Hub cannot name is refused first of
all. A label reaches no daemon: it is a name the whole Hub shares for the recap and the cards
that quote it. A service name, `PUT /api/labels/services`, is judged the same way on every copy
of every finding of that service.

### Your access and Coverage

Two launcher screens read `GET /api/access` ([API.md](API.md#get-apiaccess)). **Your
access**, behind the identity chip, shows each caller who they are, their id (the subject
to put in `Admins` or `Members`), their groups, what their grants reach and their own
terminal tokens. A caller no grant reaches gets the same screen with a block to copy for an
administrator. **Coverage**, a tab for admins only, starts with every service, attribute
value and source no project covers within `Hub:Retention`, so a newcomer is spotted and
assigned in the configuration, with one YAML pattern per entry ready to paste. Then come
the conflicts below, every live terminal token and the whole configuration, folded by
part. Neither screen writes anything about rights: they change in the configuration (Helm
values `hub.access`) and apply at the next restart. Their one write is the **Revoke**
button of the terminal token tables, which asks for a second click.

### Reading conflicts

A conflict is a finding copy that a project's attribute pattern reads but cannot write. For
a key some project pattern uses, the copy either carries two values of it (`values` lists
them), or was seen under a grouping that lacks the key (`identities_without_key` counts
those groupings, the no-grouping observations included). Two deployments of one service
reporting under different teams, or spans emitted under another team's service name, give
one. On these copies an `acks.write` grant through the attribute is refused (`403`) while
the copy still reads. Answer it with a source or service grant for
the team that owns the service, or by fixing the grouping attribute at the daemon.

## Machine tokens

A machine token is a credential for a CI job or a dashboard. It holds `findings.read` at
most, through its group's grants: whatever the grants say, a machine never acks, starts a
run or opens a daemon view.

```yaml
hub:
  access:
    groups:
      - { name: ci-billing, members: [] }   # a token's group has no members
    roles:
      - { name: ci-reader, permissions: [findings.read] }
    grants:
      - { group: "internal:ci-billing", project: billing, role: ci-reader }
    machineTokens:
      - { name: ci-billing, group: "internal:ci-billing", expiresAt: "2027-06-30T00:00:00Z",
          secretName: perf-sentinel-hub-machine-tokens, secretKey: ci-billing }
```

The token itself lives in the Secret, 32 to 512 characters (`A-Z a-z 0-9 - . _ ~ + /`,
trailing `=` allowed), never starting with `pshub_`. The chart passes it to the pod as
`Hub__Access__MachineTokens__<index>__Token`; the Hub keeps only its SHA-256 fingerprint.

```sh
curl -fsS -H "Authorization: Bearer $HUB_TOKEN" \
  "https://hub.example.com/api/findings?environment=production&status=active"
```

A token with no grant gets `403` on `/api/findings`, so a gate built on it fails rather
than passing on nothing. An unknown or expired token gets `401`. An expired entry is
logged at startup (event `2001`) and never stops the Hub.

- **Revoke** by removing the entry: the ConfigMap changes and the pod rolls.
- **Rotate** by updating the Secret, then `kubectl rollout restart deployment/<release>`.
- **One token per team**, granted only that team's projects. A token in a shared tool
  serves its whole scope to everyone who uses the tool: a Grafana datasource holding a
  token on `*` reopens every finding to every Grafana user. A token copied into
  developers' IDEs is a shared credential tied to no account, which survives offboarding.

## Terminal tokens

A terminal token (`pshub_…`) is the per-person credential: `perf-sentinel login` obtains
one through the Hub's device flow, approved in a signed-in browser, as
[AUTHENTICATION.md](AUTHENTICATION.md#terminal-sign-in) describes. It is sent as
`Authorization: Bearer`, like a machine token, and is judged as the person who approved
it:

- **Grants.** The approving session's subject and provider groups, frozen at approval,
  with internal groups and grants read on every request. Unlike a machine token it also
  writes: acks and analyses are recorded under the person's identity.
- **Never admin.** Not even when an admin approves it; `GET /api/access` reports
  `"caller": "terminal_token"` and `"admin": false`.
- **`RequiredGroups`.** The token keeps the group that admitted the session, and is
  refused once that group leaves `Hub:Auth:RequiredGroups`, as a session is.
- **Never a browser.** A token approves no sign-in and lists or revokes no token: those
  routes read the session cookie alone.
- **Lifetime.** Until the approving session would have ended, 8 hours at most; at most 10
  per person.

Terminal sign-in is offered only with `Hub:Auth:TrustedProxyNetworks` set, since the
approval page and the per-address limits need each client's own address.

## Local accounts

`Hub:Access:LocalAccounts` declares break-glass accounts for when the provider is down,
each `{ Name, PasswordHash }`, the hash delivered from a Secret. Declaring one puts the
Hub's own sign-in page in front of single sign-on, so a page request never lands on a
provider that does not answer. The account signs in as `local:<name>`, reaches what the
internal groups listing that subject and `Admins` give it, and is not subject to
`Hub:Auth:RequiredGroups`. The Hub refuses to start when an account is listed nowhere, or
when `Hub:Auth:TrustedProxyNetworks` is empty: the throttle keys on the client address.
The page, the throttle, the hash and the logging are in
[AUTHENTICATION.md](AUTHENTICATION.md#local-accounts); creating and rotating a password
in [OPERATIONS.md](OPERATIONS.md#local-accounts-break-glass).

## Sessions

With access on, a session ends 8 hours after sign-in, however it is used, as it does under
`RequiredGroups`. A local session does too, and a new hash for its account ends it at the
restart that loads the hash. Provider groups are read at sign-in: a group a user joins or leaves at
the provider applies at their next sign-in, 8 hours later at most. Everything else,
internal groups, admins, grants and `RequiredGroups`, is read on every request, so a
change applies at the restart that deploys it, for open sessions too. A session opened
before access was turned on carries no subject and signs in again.

## Known limits

- An N+1 or redundant group spanning services is listed under the first span's service:
  another service's project does not see it.
- A finding's newest envelope may describe another grouping of the same service and
  endpoint than the one a project matched.
- Anyone who can send OTLP to a daemon can set an attribute or a service name. Matching
  is per copy, so a planted value exposes only copies at the source it was planted on,
  but spans under another team's service name with another value deny that team's
  attribute writes (acks and runs) for as long as they keep coming plus `Hub:Retention`.
- A copy recorded before schema version 9 matches no attribute and no service part until
  its source reports it again.
- `max_confidence` and `lineage` in an envelope stay fleet-wide.
- A daemon's own `/metrics`, and any dashboard reading a daemon or a backend with its own
  credential, are outside the model.

See [LIMITATIONS.md](LIMITATIONS.md#what-access-control-does-not-cover) for each of these in
full, with trace reports, freshness and what stays outside the model.

## Rolling back

With access on, never roll the image back below the release that enforces it. Earlier
builds ignore `Hub:Access` and **fail open**: `/api/findings` is anonymous again and every
session sees every record (`RequiredGroups` still holds, back to the release that added
it). Set `hub.access.enabled: false` first, as a decision, then roll back. See
[OPERATIONS.md](OPERATIONS.md#rolling-back-with-access-control-on).

## A complete example

```yaml
hub:
  metricsPort: 9464
  auth:
    enabled: true
    # Endpoints and client as in AUTHENTICATION.md.
    identityClaim: email
    subjectClaim: sub
    groupsClaim: groups                       # Keycloak mapper with "Full group path" on
    requiredGroups: ["/perf-sentinel-hub"]
    trustedProxyNetworks: ["10.42.200.0/28"]  # the ingress controller's pods alone, for perf-sentinel login
  access:
    enabled: true
    admins: ["2f6d0c1e-8a4b-4e2f-9c71-0d3b5a6e7f81"]   # a sub: break-glass
    adminGroups: ["/hub-admins", "internal:break-glass"]
    groups:
      - { name: break-glass, members: ["local:ops"] }   # a local account, when the provider is down
    localAccounts:
      - { name: ops, secretName: perf-sentinel-hub-break-glass, secretKey: ops }
    projects:
      - { name: shop,    patterns: [ { attribute: "team=shop" } ] }
      - { name: billing, patterns: [ { service: "invoice-*" } ] }
      - { name: lab,     patterns: [ { source: lab-daemon } ] }
    roles:
      - { name: auditor, permissions: [findings.read, traces.read] }
    grants:
      - { group: /teams/shop,    project: shop,    role: member }
      - { group: /teams/billing, project: billing, role: member }
      - { group: /teams/lab,     project: lab,     role: member }
      - { group: /leads,         project: "*",     role: auditor }
```

Quote `"*"`, `"internal:…"` and `"team=…"` in YAML flow mappings.
