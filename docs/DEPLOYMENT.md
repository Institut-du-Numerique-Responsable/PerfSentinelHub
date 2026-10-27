# Deployment shapes

One question the rest of this documentation does not answer: when the
environments are walled off from one another, does each cluster get its own
Hub, or does one central Hub collect them all. This page states the flows each
shape needs and what the cheapest one gives up, so the answer can be read
before any of it is deployed.

## Every flow, and which way it goes

| Flow                          | Direction | What it carries                                                                                                |
|-------------------------------|-----------|----------------------------------------------------------------------------------------------------------------|
| daemon to Hub                 | inbound   | the primary findings path, `POST /api/import/findings` with `X-API-Key`                                        |
| Hub to daemon                 | outbound  | the poll, its read of the `api/acks` listing included, reachability, and `api/export/report` when a run starts |
| Hub to daemon, ack relay      | outbound  | an ack or a revoke, sent with the source's `AckHeaderName/Value` and never when that credential is absent      |
| Hub to trace backend          | outbound  | during a run, and never otherwise                                                                              |
| browser to Hub                | inbound   | the launcher and the reports it opens                                                                          |
| IDE plugin or CI job to Hub   | inbound   | `GET /api/findings`, nothing else                                                                              |
| CI job to Hub, disclosures    | inbound   | `POST /api/import/disclosures` with its own `X-API-Key`, once a period, when disclosures are on               |
| Hub to webhook receivers      | outbound  | a signed `POST` per disclosure published or withdrawn, to each `Hub:Disclosures:Webhooks` entry                |
| anyone to Hub, share links    | inbound   | `GET /share/{token}/{name}`, a disclosure's files under an expiring token                                      |
| Hub to api.github.com         | outbound  | the version check, which `Hub:UpdateCheck:Enabled` turns off                                                   |

The Hub never initiates anything toward a CI system. A build runs the engine in
batch, there is no daemon in it, so there is nothing to poll. The same topology
is drawn in [ARCHITECTURE.md](ARCHITECTURE.md).

## Three shapes, and what each one costs

| Shape                       | Network to open       | What works                                                                                           | URL for machine clients |
|-----------------------------|-----------------------|------------------------------------------------------------------------------------------------------|-------------------------|
| one Hub per cluster         | nothing               | everything                                                                                           | one per environment     |
| central Hub, both ways      | two flows per cluster | everything                                                                                           | a single one            |
| central Hub, push only      | one flow per cluster  | findings yes, reachability and daemon row unfolding and runs on a daemon source and the ack relay no | a single one            |

A Hub deployed in the cluster it collects reaches its daemons and its trace
backends over ClusterIP, so the first shape opens no firewall rule at all. It
pays for that in URLs: every machine client has to know one address per
environment, and there is no screen that shows the whole fleet at once.

A central Hub inverts that trade. One address, one fleet screen, and one set of
firewall rules per cluster to maintain. Opening both directions keeps every
function. Opening only the inbound one is cheaper and costs the functions
listed below, which is a real choice rather than a degraded install.

## What a poll catches up, and what it cannot

The Hub polls `api/findings?limit=1000&include_acked=true` immediately at
startup and then on every `Hub:PollInterval`. That resynchronises the current
contents of the daemon's buffer. It is not a journal. Whatever the buffer
evicted while the Hub was away is gone, and no limit brings it back. A poll
that returns exactly the cap is logged as possibly truncated (event 1102), because the
snapshot may be short of what the daemon holds, unless the daemon's `api/config`
confirms that it pushes to the Hub and the Hub received a findings push from it within the
last two hours: the push then delivered what the cap cut. A daemon before
perf-sentinel 0.31.2 is not asked for its config and always gets the warning. Against a daemon on perf-sentinel
0.31.2 or later whose pushes do not reach the Hub (export off, or on with no findings
push landed in the last two hours), the Hub also counts the findings that left the
ring between two polls before it read them: it logs them (event 1104), publishes
`perf_sentinel_hub_source_missed_findings_total` and adds a hint to the daemon's own
in the fleet screen. Raise `[daemon] max_retained_findings`, shorten
`Hub:PollInterval`, or turn on the daemon's Hub export. See
[LIMITATIONS.md](LIMITATIONS.md).

## What push alone gives up

Five things need the Hub to reach the daemon rather than the reverse.
Reachability is written by the poll and by nothing else, so a push-only source
reports no last success. Unfolding a daemon row in the fleet screen reads that
source on demand. Launching a run against a daemon source starts by fetching
`api/export/report` from it. Relaying an ack or a revoke writes to the daemon,
whatever ack credential the source carries. And a finding that stopped
recurring during an outage is never pushed again, because only recurrence
pushes it.

One thing bounds how much any of that matters. The daemon's exporter pushes a
signature the moment it is discovered or its severity worsens, then refreshes a
still-active one at most once an hour, so a problem that persists comes back on
its own within the hour, poll or no poll. What the poll
would have recovered is the finding that went quiet, and the window between a
restart and the next natural refresh.

## When a central Hub cannot reach a backend

Nothing becomes unanalysable. The launcher prints every run as an engine
command line, so a backend the central Hub cannot reach stays analysable from a
terminal that can reach it. See [LAUNCHER.md](LAUNCHER.md).

## What has to be true of the address

The Hub must be served at the root of an origin, and by default it
authenticates none of its readers. Both constraints shape the reverse proxy in
front of it rather than the Hub itself, so read them before writing the
ingress. See [LIMITATIONS.md](LIMITATIONS.md). `Hub:Auth` lifts the second one
for the browser without a proxy, see [AUTHENTICATION.md](AUTHENTICATION.md). With
local accounts declared, the Hub's own `/auth/sign-in` page fronts the provider and
`Hub:Auth:TrustedProxyNetworks` must name the ingress controller's pods: the sign-in
throttle keys on each client's own address. An ingress rate limit on
`/auth/sign-in/local` adds a bar the Hub cannot raise alone.

The chart writes that ingress when `ingress.enabled` is set. It publishes the
Service at the root of `ingress.host`, never under a path, and with
`ingress.tlsSecretName` it serves that host over TLS, ended at the controller.
The Hub reads `X-Forwarded-Proto`, so the redirect URI it sends the OAuth
provider, `https://<host>/auth/callback`, reads `https`. `ingress.className`
and `ingress.annotations` reach the controller as given.

The Ingress publishes every route. With `Hub:Auth` off, the default, none of
them asks who calls, the launcher and `POST /api/analyses` included. With it on,
the machine routes stay open: `/api/findings`, which IDE plugins and CI jobs read and
which returns every finding of every source, the incident and correlation reads
dashboards use, `/metrics` and `/health`. Publish the
host only on a network allowed to read them, or restrict who reaches it with
the controller's source allow-list annotations. With `Hub:Access` on as well,
those reads need a session or a machine token and answers each caller with
its own scope, and `/metrics` answers only on `hub.metricsPort`, which the
Ingress does not route, see [ACCESS-CONTROL.md](ACCESS-CONTROL.md). A proxy or
controller in front of the Hub must then neither inject nor forward an
`Authorization` header: a request carrying a bearer is judged on it alone, so
every browser request would get `401`. Outside Helm, a Hub given
`Hub__MetricsPort=9464` also needs `ASPNETCORE_HTTP_PORTS="8080;9464"`, or it stops
at startup.

The controller passes on the headers a client sends, `X-Forwarded-User`
included. Leave `hub.ackRelay.trustIdentityHeader` off behind this Ingress
unless the controller sets that header itself from an authenticating proxy, or
anyone who reaches the host acks in a name of their choosing. `Hub:Auth` needs
TLS in front of the Hub, from `ingress.tlsSecretName` or the controller: its
cookies are `Secure`, a browser drops them over plain HTTP, and every sign-in
is refused. The import accepts bodies up to 2 MiB, above the 1 MiB ingress-nginx
lets through by default, so a daemon that pushes through this host needs
`nginx.ingress.kubernetes.io/proxy-body-size` raised to match. A disclosure import takes up
to 8 MiB, so the job that publishes disclosures through this host needs `8m`.

## The CI reads, it does not feed

A build produces SARIF and JSON, and neither belongs in the Hub. `last_seen` is
the Hub's own observation clock, `status` is derived from an endpoint that
still heartbeats, and a build is a synthetic shot against a branch. Importing
one would make `status` false and would let a branch's findings read as
production's. A CI job that wants the fleet's state calls `GET /api/findings`
like any other reader. See [API.md](API.md).

A periodic disclosure is not a build output either, and it is not a finding. It is a report
for a closed period, built by `perf-sentinel disclose` from the production daemons' archives
and signed by a scheduled job, often a CI workflow for its OIDC identity. The Hub keeps and
shows it as it arrived, see [API.md](API.md#disclosures).

## Grouping attributes are chosen at each daemon

The Hub adds no grouping setting of its own. It records, per source, every
attribute a daemon captured on a finding, and a daemon captures only the
attributes listed in its own `[detection] grouping_attributes`. A key such as
`team` reaches the Hub only from the daemons that list it, so list it at every
daemon whose findings should carry it, and set it on the spans, as a resource or
span attribute, in the applications.

An explicit list replaces the engine's default rather than extending it. Keep
the defaults first:

```toml
[detection]
grouping_attributes = ["k8s.namespace.name", "service.namespace", "team"]
```

The first attribute present on a span is the finding's grouping identity. On
spans that carry neither default, from Compose or a VM for instance, the added
key becomes that identity, which changes three things at that daemon: one
detection group splits into one per value, so a count can fall below its
threshold, the daemon lists a row per value, and the `grouping` label of its
metrics follows. See [LIMITATIONS.md](LIMITATIONS.md).
