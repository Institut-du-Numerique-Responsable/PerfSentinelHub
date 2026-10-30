# Limitations

Everything here is stated somewhere else in these documents, next to the feature
it constrains. This page gathers it so you can read the boundaries in one pass
before deploying, rather than discovering one at a time.

## What the Hub declares rather than measures

`Environment` and `RetentionHours` are taken from configuration as written and
never checked against anything. A misconfigured deployment can label production
as staging, and nothing contradicts it. The launcher marks the declared half of
a row with a dashed outline. See [CONFIGURATION.md](CONFIGURATION.md).

A trace backend is never polled. Only a daemon is, so a Tempo or Jaeger query
source shows no producer version and no last success. That is the design, not a
fault.

## What a poll can and cannot tell you

The perf-sentinel 0.11.x daemon caps `/api/findings` at 1,000 rows. The Hub uses
that exact cap and warns when it is reached, because the snapshot may be
incomplete, unless the daemon pushes to the Hub and a findings push landed in the last
two hours (see DEPLOYMENT.md). High-volume coverage needs the bounded push exporter, not the poll.

A poll that omits a finding does not resolve it. The daemon's ring buffer may
have evicted it, so missing is not the same as gone. Only retention removes a
row.

The Hub records which days each source observed a finding, and that record
starts the day the version that writes it is deployed. Nothing is backfilled:
for an earlier day the Hub holds only a finding's `first_seen` and `last_seen`,
not the days in between. A source's own copy of a finding starts the same way,
at its first observation after the upgrade.

A `from` and `to` window on `/api/findings` matches whole days, cut at midnight
UTC on the Hub's clock, so a window narrower than a day still lists whatever a
source observed at any hour of that day. Presence is assumed where nothing was
recorded: between a source's `first_seen` of a finding and the first day the Hub
recorded for it, and up to its `last_seen` for a pair with no recorded day at
all, which is every pair of a database that predates the record. Without that
rule an upgraded Hub would read as empty over its whole past. With it, a finding
that went away and came back before its first recorded day reads as continuous.

Retention (`Hub:Retention`) removes a recorded day while the finding itself can
stay, and the first recorded day does not move. A window that falls wholly on
removed days after that first one therefore lists nothing, even for a finding
present all along.

The Hub mirrors each daemon's active acknowledgments and never owns them. The
mirror is as fresh as the last poll of that daemon, unless an ack relayed
through the Hub refreshed it since, so an ack taken or revoked at the daemon
itself shows at the next poll. A daemon below 0.24.0 is never asked, because it
cannot list its CI baseline, and its findings are judged on their envelope
alone, as before.

A daemon listing 1,000 acks or more has reached its own cap, and the Hub files
that read `truncated`. The acks it did list appear in `acks`, but a listing with
a missing tail cannot say a finding is not acknowledged, so it does not decide
`include_acked=false` and that source falls back to the envelope.

The ack relay writes a runtime ack at one daemon, the one behind the source
named in the route. A finding three sources carry takes three acks, and nothing
fans one out. An ack held by a daemon's CI baseline cannot be revoked through
the relay, nor through the daemon's own API: the daemon answers `404`, and the
baseline changes by editing its file under review. A revoke is relayed for a
finding the Hub holds at that source, or for an ack it mirrors from it. Once
retention has removed the finding, its ack can be revoked from the Hub only
after a poll has listed it, which never happens with a daemon below 0.24.0. See
[API.md](API.md#ack-relay).

Reachability is one-directional. A daemon pushing successfully proves it can
reach the Hub, not that the Hub can reach it, and only a successful poll clears
`unreachable_since_ms`. A source whose push arrives while its poll fails still
reports `unreachable_since`. See [OPERATIONS.md](OPERATIONS.md).

## What `status` and `lineage` claim

`status` is a presumption, not a verdict. It is derived at read time from data
the Hub already holds, and `likely_resolved` means the endpoint still heartbeats
without the finding, not that someone fixed it.

Lineage links a mutated signature to its predecessor only when exactly one
stored finding matches. Ambiguity records nothing, because naming one of several
candidates would be a guess. See [API.md](API.md).

`last_seen` is the source daemon's last detection, cut to the Hub's clock when the
daemon's runs ahead. A daemon clock running behind is not corrected, so its findings
age early by the same amount. A producer that omits `stored_at_ms` is dated by the
poll that read it, which keeps such a finding `active` for as long as its ring holds
it.

## What a detection count claims

A read of `/api/findings` with `from` or `to` serves how many times the daemons
detected each finding, from the detection ledgers of perf-sentinel 0.37.0 and later.
The count is exact only when `detections_complete` is true. Otherwise it is a lower
bound, the sum of what is known, with its reasons in `detections_gaps`. Null means
nothing is known, never zero. See
[API.md](API.md#what-the-hub-adds-to-a-finding).

A detection is a finding the daemon emits, counted before its ring, its acks or its
push see it. An acknowledged finding still counts, as its waste still counts in
`io_waste_ratio`. One trace can yield several findings of one signature, a redundant
call with different parameters, a fan-out or serialized group per parent, several
grouping values, and each counts. Slow findings across traces count with 0 avoidable
I/O. A trace fragment analyzed again after its window evicted it counts as the
detector emitted it. `seen_count` keeps its meaning, the detections the daemon's ring
still retains, and is not a volume.

A daemon keeps its ledger in memory only, so every stop, clean or not, is a gap: the
day a ledger starts and the last day of a stopped one read `restart`. The Hub adds a
source's successive ledgers up. A daemon read by poll alone loses what it counted
between the Hub's last read and its stop. A daemon that pushes sends a last round as
it drains.

Nothing is extrapolated. Under sampling or shedding the count is what the daemon
analyzed, and the Hub serves the lowest sampling rate of the counted days as
`detections_sampling_rate`. A consumer that extrapolates from it should say so. Some
losses have no signal at the daemon: an ingest refused with a `413`, an oversized
gRPC message, the JSON socket and the per-trace truncation. A day can therefore read
complete while traces never reached the detector.

A poll reaches one pod per request, so several pods behind one Service cannot all be
read. Two polled ledgers of one source alive at once read `multi_instance`, and that
source is never complete. Give each pod its own source, or let each pod push: pushed
ledgers add up without the reason. The overlap compares the daemon's `started_ms`
with the Hub's read time, so a daemon clock running behind the Hub's can make a
restart read as `multi_instance`.

The window counts whole UTC days on the Hub's clock, from the day of `from`, never
before the day retention keeps, to the day of `to`, never after the Hub's day. Days
before the upgrade have no row and read as unknown. With access patterns, a source
that counted a finding without holding a copy of it is listed only to a caller who
reads that whole source, and a scoped read reaches only findings with at least one
copy in scope. A source removed from `Hub:Sources` drops out of the count, since the
Hub has no name to serve for it.

## What a recap claims

The carbon figure of the recap is an order of magnitude, never a measurement. It prices
the extra I/O of the ranked findings at each day's own grams per avoidable I/O, and that
coefficient comes from the operational tier of the daemon's carbon model. The extra I/O
is the findings' `extra_io_ops`, which is not deduplicated the way a window's
`avoidable_io_ops` is, so it can exceed what the window counted. The day's coefficient
also prices the I/O of the batches whose region the daemon could not resolve. The figure
is rounded to one significant figure for that reason, and a source that sends no
coefficient leaves it a partial minimum, never extrapolated.

`gone` is a presumption, like `likely_resolved`: a finding the period no longer holds may
have been fixed, or the traffic that produced it may have stopped. `gone_with_confirmed_action`
counts the ones backed by a done action the Hub confirmed from the sealed ledger days the
engine sends (see [API.md](API.md#recap), Actions and confirmation), which is the only
evidence the recap holds.

A period reads incomplete, and its comparison is not exact, for four reasons:
`uncovered_days` (days the ledgers do not cover), `open_day` (a past day not sealed),
`restart` (a daemon restart interrupted its counts, as above) and `overflow`. Four notes
leave the comparison standing: `sampling` and `shed` (the count is what the daemon
analyzed), `ingest_rejected` (the daemon refused part of the ingest) and `multi_instance`
(two polled ledgers of one source alive at once). See
[API.md](API.md#recap). Months of different lengths compare as they
are, 31 days against 28, so a monthly trend is a ratio of volumes, not of daily rates. A
period in progress compares its closed days with the same number of first days of the
previous period. Today counts in the tiles and the ranking only, and on the first day of
a period nothing is compared, which the recap says instead of reading nothing moved.

An action is global to the signature, as an ack is, whichever source holds the finding.
Its confirmation is judged within the request's filters, so the same action can read
differently in a filtered and in an unfiltered recap. Any detection after the day an
action was marked done contradicts it, a straggling trace or a slow deployment included,
so mark it done again once the fix has actually shipped. Actions and their events are
never purged, not with their finding and not by retention, so the tables only grow. Labels
are Hub-wide as well, and are not purged either: an endpoint's name and a service's name both
outlive the findings they named.

A recap reads the findings in up to four reads: the period with its acknowledged findings,
the period without them, the closed days of a period in progress, and the previous window.
Each read stops at 5,000 findings and keeps the first 5,000; any read that overflows sets
`truncated`, and the figures built on it cover those alone. Narrow the request with a filter.

A report is frozen when it is generated and never follows later data: a finding detected or
acknowledged afterwards, an action marked done or a label renamed changes the screen and leaves
the report as it was. A report generated past `Hub:Recaps:MaxDelay` says which days were still
open, and those days stay open in it. Periods missed while the Hub was down are not generated
afterwards, except the last closed one. An on-demand report reflects its creator's scope at the
moment it was made, and beyond `recaps.read` holders only its creator reads it.

The HTML uses the system fonts, so it opens offline and looks a little different on each machine.
A frozen report carries no ack link: it outlives the routes it would call, and acks are taken on
the Hub's screen. E-mail goes through a relay that offers STARTTLS and, when credentials are set,
`AUTH LOGIN`. The Hub does not speak implicit TLS and does not do OAuth, so an account restricted
to OAuth needs the organisation's relay in front of it.

## What a share card claims

A card proves its figures, not who made them. At level 1 the check page says the figures are
intact since their signature, by a key whose holder nobody has verified. Only level 2, a key the
organisation's domain publishes in its DNS TXT record and its `/.well-known` file, names a domain,
and even then it names the domain that published the key, not the people who used it.

The signing date on a card is written by whoever holds the key, and no third-party timestamp
backs it. So revoking a key makes every card it signed read as revoked, honest ones included,
since nothing tells a card signed before the leak from one dated back after it. A key that is
only rotated out keeps vouching for the cards dated inside its window, a window a stolen key
could also date a card into.

An original file the Hub downloaded is checked byte for byte, so any edit reads as modified.
LinkedIn strips a file's metadata, an editor that saves an SVG again usually drops its
`<metadata>`, and a converter that re-saves a JPEG XL usually drops its `pscd` box, so a reposted
or edited image keeps only its QR code. The QR code proves the figures, not the pixels around it:
the check page shows those figures, to compare with the image. LinkedIn does not accept JPEG XL
at all, so the PNG is the file to post.

The figures are as the Hub held them. A story cites the last sample the Hub held before the
action was marked done, the newest copy winning. Its fanout counts calls, not systems, its slow
figure is the p99 of the calls above the threshold, and its pool figure is a peak of simultaneous
calls. The before-fix period a card prints is the period its volume was summed over, or, for a
threshold story, the period of the trace it cites. The volume before covers N+1 and redundant
findings only, and its CO2 is the low tier of the estimate, a partial minimum.

"No longer triggers the alert, set at X" needs a daemon that pushes and a readable trace date. The
daemon learns which signatures to watch from the answer to its push: it loses that list on a
restart until its next push, a polled-only daemon never gets one, and a fanout signature matches
only part of its parents. A fix is confirmed as of the card's generation, and a later detection
does not change a card already made. Actions marked done before this release have no frozen
finding, so no card tells them, and a collective card for a period holding one is refused
(`unkept_fixes`) rather than signing a count short of it.

The curve is daemon-wide: only the environment and the source narrow it, and acknowledged
findings count in it. A daemon restart in the period is tolerated and printed on the card. The
signed scope lists only the sources that recorded a ledger in the period, so a daemon added after
it is left out. A collective card prints no comparison with a period holding a fix marked done
before this release, since its fixes could not all be counted, nor with an incomplete one, which also drops its
volume before the fixes. Detection days are kept 1,100 days by default (`Hub:DetectionRetention`),
so a year card can compare with the whole year before. A lower setting refuses year cards with
`previous_uncovered_days` for most of the year, and one below `Hub:Retention` reads as
`Hub:Retention`.

## What a finding is attributed to

The engine files an N+1 or redundant-call group that spans several services
under the service and the grouping of its first span. A second calling service
appears only in `pattern.occurrences_by_service`. The Hub keeps that
attribution: `/api/findings?service=` matches the first service only, and the
grouping attributes it records for the finding are the first span's. Fan-out and
serialized calls are attributed to the parent span.

The Hub records every grouping attribute a source captured on a finding, and the
service that source reported, per source. A source's copy written by an earlier
version without its own envelope gets neither until that source reports the
finding again.

Adding an attribute to a daemon's `[detection] grouping_attributes` changes more
than what the Hub records. On spans that carry none of the earlier attributes,
the new one becomes the grouping identity: a detection group splits into one per
value, and each can fall below its threshold. See
[DEPLOYMENT.md](DEPLOYMENT.md).

## What a run does not promise

Reports are deleted after `Hub:Analysis:ReportRetention`, 24 hours by default.
This is not an audit trail, and a link shared yesterday is already dead. The run
keeps its parameters, so it can be launched again as it stands.

A run still going when the service stops comes back `interrupted` and is never
replayed on its own. A silent retry would fire a second heavy query at a backend
nobody asked to query twice.

Report size is not a knob. The sink's 5 MiB target is a private constant with no
flag, no environment variable and no config key. When it drops findings to fit,
the run records how many survived, read back from the rendered file.

The eight failure codes come from a heuristic on a bounded set of markers in the
engine's stderr, not from a contract. Raw stderr never leaves the process.

Counts from runs with different detection thresholds are not comparable. Raising
a threshold does not make a run lighter, it stops the detector from reporting
the smaller cases.

## What the Hub does not authenticate

This section describes the Hub with `Hub:Auth:Enabled` off, the default. On, the
Hub signs browser users in itself and only the machine routes stay open, see
[AUTHENTICATION.md](AUTHENTICATION.md). With `Hub:Access` on as well, those routes need a
machine token too and every read is trimmed to the caller's grants, see
[ACCESS-CONTROL.md](ACCESS-CONTROL.md) and
[What access control does not cover](#what-access-control-does-not-cover).

Five endpoints ask for a credential, `POST /api/import/findings`,
`POST /api/import/incidents`, `POST /api/import/correlations`, `POST /api/import/detections`
and `POST /api/import/disclosures`, whose `X-API-Key` is compared by fingerprint, and
`/share/` serves a disclosure's files to whoever holds a live token. Every other one asks for
nothing: `/api/status`, `/api/sources`, `/api/findings`, `/api/correlations`, `/api/analyses`
for both the listing and the request that starts a run, `/reports/`, `/metrics`, and the
launcher itself. Whoever reaches the port reads every finding of every tenant, SQL query
shapes and endpoint names included.

The `by` and `reason` of a mirrored ack are readable on the open read route,
`/api/findings`, which stays open to machines with `Hub:Auth` on unless `Hub:Access` is
on as well. That is who
acknowledged a finding and why, in the words typed at the daemon, as
`acknowledged_by` already was.

The identity header attributes, it does not authenticate.
`Hub:Analysis:IdentityHeader` is recorded on a run as a claim some proxy made,
and the Hub verifies nothing about it. That is the right behaviour behind an
authenticating proxy and no defence at all without one. If the network is not
the boundary, put that proxy in front or turn on `Hub:Auth`. See
[DEPLOYMENT.md](DEPLOYMENT.md).

Unless `Hub:Access` is on, the ack relay knows who asks and has no roles. Any caller the Hub can name, a
signed-in user or, once `Hub:AckRelay:TrustIdentityHeader` is set, whoever the
proxy header names, may ack or revoke on every source that carries an ack
credential. The daemon has no per-user roles either: its ack key is one key, and
the Hub holds it. Restrict who signs in, on the provider side or with
`Hub:Auth:RequiredGroups`, and leave the credential off a source nobody should
ack from the Hub. The name sent along feeds the daemon's audit trail and
authorizes nothing there. The daemon keeps 256 bytes of it, so a long name that
had to be percent-encoded may be cut.

The relay has no antiforgery token and no CORS policy, so it reads
`Sec-Fetch-Site` and refuses every value but `same-origin`, `cross-site` and
`same-site` and the `none` a typed address carries alike. A caller that sends no
such header at all passes, since curl, a CI job and an IDE plugin do not set it.
What defends that case is the content type alone: the Hub takes a JSON content
type and nothing else, which a cross-origin form cannot produce and a
cross-origin fetch cannot send without a preflight the Hub never answers. That
is a floor and not a boundary, and the floor is the relay's alone:
`POST /api/analyses` and `POST /api/incidents/refresh` judge neither the site
nor the content type. A Hub reachable from a browser session therefore belongs
behind a proxy that does not forward arbitrary origins.

## What access control does not cover

With `Hub:Access` on, a caller reads and writes only what a grant gives it, see
[ACCESS-CONTROL.md](ACCESS-CONTROL.md). These are the limits of that model.

Who a finding is attributed to:

- An N+1 or redundant group spanning several services is listed under the first span's
  service and grouping. A second calling service appears only in
  `pattern.occurrences_by_service`, so its project does not see the finding. Fan-out and
  serialized calls are attributed to the parent span.
- One signature folds every grouping. A project that matches one grouping of a copy reads
  the copy's newest envelope, which may describe another grouping of the same service and
  endpoint.
- An attribute or a service name is set by whoever deploys the application, by anyone who
  can send OTLP to a daemon, and by whoever holds a source's import key, for that source
  only. Matching is per copy, so a planted value exposes only copies at the source it was
  planted on. Within that source:
  - spans under another team's service and endpoint with `team=evil` let evil's project
    read that copy's newest envelope, which may be the other team's;
  - the same spans with no grouping attribute, or another value, deny the owning team's
    attribute grant: `403` on every ack and revoke of that finding at that source, and on
    a trace-backend run of that service, for as long as the spans keep coming plus
    `Hub:Retention`;
  - an ack is keyed on the grouping-blind signature. One written through an attribute
    grant also silences groupings of that signature the Hub has not seen at that daemon,
    and any that appear later, so it may silence the same finding for another team's
    deployment there. The Hub forces no expiry on it.

  Where several teams can emit the same service name, give the owning team a source or
  service grant for `acks.write` and `analyses.run`.
- Starting a run at a trace backend needs every copy of the service in the backend's
  environment to agree. Name a trace backend's environment like the daemons that report
  its services.
- A copy recorded before schema version 9 matches no attribute pattern and no service part
  until its source reports it again.
- Adding a grouping attribute changes the identity of spans that carried none of the
  earlier ones.

A trace-backend report shows every service its traces cross. A report is read on its
run's subject, `traces.read` on the requested service or at the backend as a whole for a
trace ID, and the Hub does not filter the HTML the engine wrote. Whoever holds
`traces.read` on a service reads, in its reports, the spans, findings and counts of every
other team's service those traces reach. That is intended, a problem often starts
elsewhere. Give `traces.read` only to those who may see cross-team traces: the built-in
`reader` and `member` roles carry it, a role with `findings.read` alone does not.

`max_confidence` and `lineage` in an envelope are not trimmed to the caller's copies, and
a source row written before the per-source copy existed serves the shared envelope until
that source reports the finding again.

How fresh a decision is:

- provider groups are read at sign-in, so they are at most 8 hours old, and a terminal
  token keeps those of the session that approved it, for no longer than that session;
- `Hub:Auth:RequiredGroups`, internal groups, admins, grants and machine tokens apply at
  the restart that deploys them;
- a rotated machine token needs a rollout restart, a removed one goes with the ConfigMap
  change that rolls the pod.
- a revoked terminal token is refused from its next request;
- a local account's new hash needs a rollout restart, which ends that account's browser
  sessions; the terminal tokens it approved live until their expiry, 8 hours at most,
  unless an admin revokes them.

Terminal sign-in is not offered while `Hub:Auth:TrustedProxyNetworks` is empty. Behind a
proxy listed there, the address the approval page shows and the per-address limits count
are the client's own, as the proxy reports it: a proxy that forwards a client-supplied
`X-Forwarded-For` unchanged lets a client choose the address it shows.

Local accounts:

- The throttle lives in memory, and a restart clears it.
- There is no sign-out, for a local session as for any other.
- One address hashes at most 5 times a minute, and one hash at a time runs. An attacker
  with many addresses, two dozen IPv4 addresses or IPv6 /64s at a few hundred milliseconds
  per 600,000-iteration hash, can still keep the hashing slot busy: the operator then
  waits up to 10 seconds for it, and gets `503` beyond that. An ingress rate limit on
  `/auth/sign-in/local` raises that bar.
- Online guessing is bounded by that slot, the per-address budget, the backoff and the
  15-character minimum, not stopped. Pick a long random password.

Outside the model:

- A daemon's own `/metrics` stays open. With per-service or per-grouping labels it names
  services and groupings, though it carries no finding.
- A dashboard reading a daemon or a trace backend with its own credential shows whatever
  that credential reads, to whoever the tool lets in. So does a machine token in a shared
  tool: a Grafana datasource serves the token's whole scope to every Grafana user, and a
  token in developers' IDEs is a shared credential that survives offboarding. One token per
  team, granted only that team's projects, limits both.
- The identity shown in `requested_by` and an ack's `by` is whatever the provider's
  `Hub:Auth:IdentityClaim` holds. A user who can rename themselves there changes that
  label, never their rights, which follow `Hub:Auth:SubjectClaim`.
- A proxy in front of the Hub that injects or forwards `Authorization` breaks every browser
  request once access is on: the Hub judges the bearer before the cookie.
- Roles combine the Hub's permissions only. A new kind of action needs a new permission in
  the Hub.

## What sits outside the Hub

A daemon source with an ack credential needs none of what follows: with engine 0.31.0 or
later its report acks through the Hub. Otherwise, a live report needs two things the Hub
does not control: the daemon's
`[daemon.cors] allowed_origins` must carry the origin the Hub serves reports
from, and the viewer must be able to reach that daemon directly, at its `PublicUrl`
when the Hub reads it by a name only the cluster resolves. A daemon behind
a path-based ingress gets a static report instead, because the engine's
`--daemon-url` takes an origin and nothing else. See [LAUNCHER.md](LAUNCHER.md).

The Hub has the cousin of that constraint and must itself be served at the root
of an origin. The launcher calls `/api/status`, `/api/sources`, `/api/analyses`
and `/reports/` as absolute paths, nothing rewrites them, and a path-based
ingress leaves the browser asking for a prefix the Hub never answers. See
[DEPLOYMENT.md](DEPLOYMENT.md).

## Scale and observability

One replica, and not a knob. SQLite has a single writer and the volume is
`ReadWriteOnce`, so the chart sets `replicas: 1`.

`GET /metrics` covers reachability, the analysis queue, run counts and the
stored findings of each environment, and nothing else. Findings are counted per
environment, type, severity and status, never per service or per endpoint, and
the counts are up to 15 seconds old. There is no series for retention purge
duration or for import throughput, so an alert on those, or on one service's
findings, has to read `/api/findings` or the logs. See
[OPERATIONS.md](OPERATIONS.md#metrics).
