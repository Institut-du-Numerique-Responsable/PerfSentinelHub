# How the Hub fits together

Five diagrams, each answering a question the README states in prose but does not draw.
Sources live in [`diagrams/mmd/`](diagrams/mmd), rendered SVGs in
[`diagrams/svg/`](diagrams/svg). Editing a diagram means editing the `.mmd` and
re-exporting both themes, never touching the SVG by hand.

## The whole thing at once

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/hub-integration_dark.svg">
  <img alt="How the Hub, the fleet, the browser and the engine fit together" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/hub-integration.svg">
</picture>

One Hub serves two audiences that never overlap. The browser gets the launcher and
nothing else. An IDE plugin or a CI job gets `/api/findings` and never opens a screen.
That split is not enforced by authentication, it is simply what each client asks for,
and it is worth knowing before reading either surface. `Hub:Auth` puts the browser's side
behind a sign-in and leaves `/api/findings` open, see [AUTHENTICATION.md](AUTHENTICATION.md).
`Hub:Access` closes it too: a machine then sends a bearer machine token, and a
developer's terminal, signed in by `perf-sentinel login`, a terminal token, see
[Access control](#access-control).

The engine appears twice on this board and it is the same binary both times: once as a
subprocess the Hub spawns to produce a report, and once as the daemon it collects from.
Nothing about the Hub is resident in the engine, and nothing about the engine is
resident in the Hub.

## Push and poll, and which one owns reachability

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/push-and-poll_dark.svg">
  <img alt="The push path and the poll path, and the fact only one of them owns" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/push-and-poll.svg">
</picture>

Both paths write findings. Only one writes reachability.

Incidents follow the same split from perf-sentinel 0.29.0: the daemon pushes each one when it is
recorded, closed or completed, and the hourly poll still reads them as a safety net. Both write
through one merge rule, so neither path can undo the other.

Correlation episodes take the push alone from perf-sentinel 0.30.0: the daemon cuts each episode and
pushes it when it opens, when a peak rises, when its last co-occurrence moves on by a minute and
when it closes. Nothing polls them, so a daemon that does not push leaves the Hub without them.

Detection ledgers take both paths from perf-sentinel 0.37.0: the daemon pushes the rows that
changed since its last accepted push, and the poll reads the ledger the daemon's status
names. Both write absolute values per UTC day, merged by their maximum, so the two paths, a replay
and an overlap all land on the same rows. Each daemon start opens a new ledger, and the Hub adds a
source's ledgers up rather than letting the next one replace the last. The answer to a ledger push
is the one answer that carries data back to a daemon: `watch`, the fixed findings of a threshold
type this source has not yet confirmed fixed, whose passes below the threshold the daemon then
counts. A polled daemon never gets it.

A daemon pushing successfully proves that the daemon can reach the Hub. It proves
nothing about whether the Hub can reach the daemon, which is a different route through
a different set of firewalls, and that is the direction an operator needs when a source
goes quiet. So the import handler touches no reachability column: it may
refresh an existing row's `producer_version` and nothing more. A source whose push
lands while its poll fails keeps reporting `unreachable_since`, and that is correct
rather than stale.

The two paths also differ in back-pressure, deliberately. The poll blocks on the write
lock because it is the Hub's own scheduled work and can wait. An import gives up after
five seconds with `503 Retry-After: 1`, because a slow uploader must not be able to
stall the whole fleet's collection.

A poll reads up to five things from one daemon, in order: its status, its findings, its
incidents, its active acknowledgments and its detection ledger. Only the first two decide
reachability. The other three are filed apart, in `incident_reads`, in `ack_reads` and on
`perf_sentinel_hub_detection_reads_total`, because a refused key or a missing route on any
of them says nothing about whether the daemon answers.

The ack read mirrors and never writes to the daemon. It asks
`GET /api/acks?include_toml=true` with the source's read credential and replaces that
source's rows in `source_acks` with the answer, in one transaction, so an ack revoked at
the daemon leaves with the next poll. Each row keeps where the ack came from, `daemon`
for one taken at runtime and `toml` for one of the CI baseline. When a signature carries
both, the baseline row is kept, as the daemon's own lookup does. A daemon before 0.24.0
is never asked: it ignores the parameter and lists its runtime acks alone, an answer that
cannot be told from a daemon with no baseline, so its read is filed `absent`. A listing
at the daemon's cap of a thousand rows is mirrored and filed `truncated`, since its tail,
which is where the baseline sits, may be missing.

The ledger read starts from the ledger's cursor, or from its oldest day the first time, and
pages to the end, 64 pages at most. A pass that reads every page seals the days it read, from
the later of the cursor and the daemon's oldest day up to the `sealed_through` of its first
page, and moves the cursor past them, so a day the daemon sealed while the pass ran is read
whole again by the next one. A day below that, pruned by the daemon after a partial read, stays
open. A pass that stops early, at the page cap or because the daemon opened a new ledger
meanwhile, keeps what it merged and seals nothing. A daemon whose status names no ledger, older
than 0.37.0 or with the ledger off, is never asked.

## What a launched analysis actually does

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/analysis-run_dark.svg">
  <img alt="A run from submission to the embedded report" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/analysis-run.svg">
</picture>

Two things on this sequence are easy to miss.

The first is that validation happens **before** the run is queued. An impossible pair,
a three-hour window against a daemon that keeps ten minutes, is refused while the
operator is still looking at the form, not discovered three minutes later as a failed
run.

The second is that a run is two spawns of the engine, not one. The query subcommands
emit text, JSON or SARIF, and only `report` writes HTML, so the source is read into a
report JSON and that JSON is then rendered. A daemon source skips the first spawn
entirely, because its own `/api/export/report` already returns exactly that JSON.

## Three retention clocks

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/retention-clocks_dark.svg">
  <img alt="Findings, status window and rendered reports each expire on their own clock" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/retention-clocks.svg">
</picture>

None of the three is derivable from the others, and they are enforced by three
different mechanisms. The daily worker, the first of them, keeps several durations, one
per kind of row.

Findings expire on a worker that runs once a day and purges in chunks, so a long purge
cannot reject imports for its whole duration. Incidents copied from a daemon ride that
same clock, purged on the Hub's own `last_seen_ms` and never on the alerting's `at_ms`.
Correlation episodes ride it as well, purged on their own `last_seen_ms`, the daemon's stamp of
the pair's last co-occurrence: an episode is history once it closes, and the Hub's clock only says
when a copy arrived.
Observation days, the record of which days each source saw a finding, ride it too, on
the Hub clock and by whole day: a day leaves once it lies entirely before the cutoff, so
the day the cutoff falls in is kept. They carry no foreign key, because a cascade from
one purge chunk would hold the write lock far longer than the chunk itself, so a purged
finding can leave an observation row behind. That orphan is invisible, a read reaches an
observation only through its source's row, and it leaves with its day. The groupings each
source saw a finding in ride the same clock on their own `last_seen_ms`, without a foreign
key for the same reason, and an orphan is as invisible: a read reaches one only through a
finding. The grouping attributes each source captured on a finding ride it the same way, on
their own `last_seen_ms`. The pairs an incident's frozen findings share leave with their
incident.

Detection counts and days ride the daily worker on a clock of their own,
`Hub:DetectionRetention`, 1,100 days by default, by whole UTC day like observation days, and a
ledger's own row with them, on the last time the Hub read it. A year card compares a year with
the whole year before it, which the 180 days of a finding cannot hold, and the validator keeps
this clock at least `Hub:Retention`, since the recap reads the detection days of every finding
it holds. A replay of a purged day is harmless, since every value is absolute, and a daemon
keeps 31 days at most anyway.

The ack mirror rides that clock as well, on `read_at_ms`, the time of the read that wrote
the row. It does not age an ack: every read that succeeds rewrites its source's rows, so
a row only grows old once the Hub has stopped mirroring that source, because it was
retired or because its listing failed for the whole retention. `ack_reads` and
`incident_reads` hold one row per source and are never purged.

Recap reports ride the daily worker as well, on a clock of their own: `generated_at_ms` against
`Hub:Recaps:Retention`, the way finished runs ride `Hub:Analysis:RunRetention`. A report and its
deliveries leave together, in one pass of that worker.

The status window is not a worker at all, it is a `CASE` evaluated at read time, which
is why a finding's status can change without anything having been written. Rendered
reports expire on a sweep that runs every sixty seconds, finer than the lifetime it
enforces, so the countdown a reader sees never outlives the file it counts down to.

The trap the diagram exists to prevent: a poll that omits a finding does not resolve
it. The daemon's ring buffer may simply have evicted it, and missing is not the same as
resolved. Only retention removes a row.

## What the database holds

Every table is created in `Storage/Schema.cs`. A finding is held twice over: once
fleet-wide, keyed on its grouping-blind signature, and once per source that reported it,
because the fleet-wide row keeps the newest envelope from any source and nothing else.

| Table                          | One row per                                                    | Leaves                                     |
|--------------------------------|----------------------------------------------------------------|--------------------------------------------|
| `findings`                     | signature, with the newest envelope from any source            | retention, on `last_seen_ms`               |
| `finding_sources`              | signature and source, with that source's envelope and service  | retention, on `last_seen_ms`               |
| `finding_observations`         | signature, source and day                                      | retention, by whole day                    |
| `finding_groupings`            | signature, source and effective grouping value                 | retention, on `last_seen_ms`               |
| `finding_attributes`           | signature, source, grouping identity and captured pair         | retention, on `last_seen_ms`               |
| `finding_lineage`              | successor and predecessor signature                            | with its successor                         |
| `detection_ledgers`            | source and daemon ledger, with its start and the poll's cursor | retention, on `last_read_ms`               |
| `detection_days`               | source, ledger and UTC day, with what the day says of itself and the thresholds it applied | retention, by whole day                    |
| `detection_counts`             | signature, source, ledger and UTC day, with its detections and passes below the threshold | retention, by whole day                    |
| `endpoint_heartbeats`          | source, service and endpoint                                   | retention, on `last_seen_any_ms`           |
| `source_state`                 | source, the poll's reachability                                | retention, on `last_attempt_ms`            |
| `source_imports`               | source, its last push                                          | never                                      |
| `source_finding_imports`       | source, its last findings push                                 | never                                      |
| `incidents`                    | incident and source                                            | retention, on `last_seen_ms`               |
| `incident_attributes`          | incident, source and pair every frozen finding carries         | with its incident                          |
| `correlation_episodes`         | episode and source                                             | retention, on `last_seen_ms`               |
| `source_acks`                  | source and signature                                           | retention, on `read_at_ms`                 |
| `finding_actions`              | signature, with its status and owner, and the finding's envelope once done | never                          |
| `finding_action_events`        | status or owner change of an action                            | never                                      |
| `endpoint_labels`              | service and endpoint, with its business name                   | never                                      |
| `service_labels`               | service, with its business name                                | never                                      |
| `incident_reads`, `ack_reads`  | source                                                         | never                                      |
| `analysis_runs`                | run                                                            | `Hub:Analysis:RunRetention`                |
| `terminal_tokens`              | terminal token, by the SHA-256 of its value                    | revocation, or retention past `expires_at_ms` |
| `disclosures`                  | published disclosure, its files in BLOBs                       | never, a withdrawal keeps it               |
| `disclosure_share_links`       | share link, by the SHA-256 of its token                        | never, revocation and expiry only mark it  |
| `disclosure_deliveries`        | webhook event of a disclosure                                  | never                                      |
| `recap_reports`                | frozen recap of one period, generated or on demand             | `Hub:Recaps:Retention`, on `generated_at_ms` |
| `recap_deliveries`             | webhook or e-mail delivery of a report                         | with its report                            |

`finding_attributes` keeps every grouping attribute a daemon captured, up to the engine's
eight, each under the grouping identity it came with: the first pair of that observation,
or four empty strings when the observation has no well-formed first pair, in which case no
other pair is recorded. `finding_sources.service` is the service that source reported,
which `findings.service` is not once two sources disagree. `incident_attributes` holds the
pairs common to every frozen finding of one source's copy of an incident, or two empty
strings when they share none, derived from the stored copy whenever a read changes it. A
read that leaves the copy as it was, the same findings or a poorer recapture, only stamps
its rows with the copy's new `last_seen_ms`. Nothing reads these three yet. They are
recorded ahead of access control, which will match projects on them, so that turning it on
does not start from an empty history.

The three detection tables, added by schema V15, hold the daemons' ledgers as read. A row of
`detection_days` carries what that day says of its own completeness: sealed or still open, a
restart, its sampling rate, shed traces, rejected ingest and the detections past the ledger's
signature cap. When the daemon scores carbon, the row also carries the day's avoidable grams
(mid and low), avoidable I/O and total I/O, and the thresholds it applied. A row of
`detection_counts` may carry the passes below the threshold of a watched signature with zero
detections, which no read counts. A windowed read of `/api/findings` folds them, per source, into the count it
serves and the reasons it falls short, see
[API.md](API.md#what-the-hub-adds-to-a-finding). Nothing is backfilled: a day before the
upgrade has no row and reads as unknown.

The recap, `GET /api/recap`, writes nothing and keeps nothing of its own. `RecapService` makes
up to four windowed reads of the findings, each capped at 5,000: the period with its acknowledged
findings, the period without them, the closed days of a period in progress, and the previous
window, skipped on a period's first day. Then it makes a coverage read of the sources' days and a carbon
read of their avoidable grams, and joins the actions and the labels last. The folding is pure
and lives in `Recaps/`: the period, the ranking, the trends, the moves, the coverage judgment,
the carbon pricing and the sentence per type. Share cards add quarter, semester and year
periods, and the recap API still takes only a day, a week or a month. The service is a plain
class on purpose: the reports a subscription sends reuse it as it is. `RecapGenerator` freezes the last closed period of
each subscription into `recap_reports` once its ledgers have sealed, `RecapReportRenderer` writes
the self-contained HTML of a stored snapshot, `RecapDeliveryWorker` sends the due rows of
`recap_deliveries` to the webhooks and, through `RecapMailer`, to the SMTP relay, and `RecapLoop`
runs the generator and the worker every 15 seconds.

Each start fills in what an older build left out: the attribute rows of a copy whose newest
observation has none, the service of a copy that has none, both from that copy's own
envelope, and the pairs of every incident copy with no row or with a stamp other than its
`last_seen_ms`, which only an older build's write leaves. An image rolled back below this
version and forward again is therefore covered at the next start. A copy written before the
per-source envelope existed has neither until its source reports it again. The cost of that
start is measured in [BENCHMARKS.md](BENCHMARKS.md#start-on-a-filled-database).

## Access control

`Hub:Access` is compiled once, at the first request for it, into `AccessPolicy`
(`Access/AccessPolicy.cs`): each project's patterns, each role's permissions, the grants, the
internal groups by member subject, the admin sets, and a SHA-256 fingerprint of each machine
token, never the token. `Program.cs` registers it whatever the setting, because a handler
shared by both modes takes it as a service, and the Request Delegate Generator would read a
type the container lacks as a JSON body, which a `GET` refuses. With access off it builds
nothing and every scope it computes is unrestricted, so each handler keeps today's SQL.

For one caller and one permission, `AccessPolicy.Scope` returns an `AccessScope`
(`Access/AccessScope.cs`): unrestricted, nothing, or the union of the granted projects'
patterns. An `AccessPattern` matches a finding copy, one signature at one source, on three
things: the source, the copy's own service in `finding_sources.service` and never the
fleet-wide `findings.service` an import rewrites, and the copy's rows in
`finding_attributes`. A read needs one row to match, whatever grouping it came with; a write,
for `acks.write`, `actions.write` and `analyses.run`, needs every grouping identity of the
copy, the no-grouping marker included, to carry the key and every value of it to match. A trace run
on a service is judged on the Hub's copies of that service in the backend's environment,
every one of which must write-match; `CoversSubject` matches the copies it is given, and
the caller enforcement adds selects them by environment. Incident copies match on their
pairs in `incident_attributes`.

`Storage/HubDatabase.Access.cs` holds the same read match in SQL, for the reads that are
filtered. A statement opens with the `ap` CTE, which decodes the one bound parameter
`$access_patterns` once instead of once per row and pattern, then tests `visible`, the
copies in scope, or `IncidentAccess`. Parts compare with `=` or `substr`, never `LIKE` or
`GLOB`, which fold case and give `%` and `_` a meaning. `AccessScopeParityTests` runs one
table of patterns through both over the rows H1's own backfill writes, and an explicit test
times the CTE, see [BENCHMARKS.md](BENCHMARKS.md#access-scope-over-100000-copies).

**Who is calling.** With access on, `HubAuthentication` registers a policy scheme, `hub`,
which forwards a request carrying `Authorization: Bearer` to `HubTokenHandler`
(`Access/HubTokenHandler.cs`) and every other one to the cookie scheme, whose challenge still
goes to the provider. The token handler compares the bearer's fingerprint with every
machine token's through `AccessPolicy.MachineTokenName`, and a match becomes a principal
whose subject is `machine:<name>`. The provider sign-in adds the subject, the provider
groups a grant names, `hub:access` and `hub:caller` to the cookie. `AccessCaller.Of` reads
those claims once per request. After authorization, a gate refuses a machine's write and
a browser write without `Sec-Fetch-Site: same-origin`, and a first middleware sets
`frame-ancestors 'self'` on every response.

**Terminal sign-in.** `ApiEndpoints.MapTerminalAccess` (`Api/ApiEndpoints.Terminal.cs`)
maps the device flow only with `Hub:Auth:TrustedProxyNetworks` set, since the client
address it records is the one `ForwardedHeaders` takes from those networks alone,
normalized by `ClientAddress` (an IPv6 client by its /64). `DeviceAuthorizations`
(`Access/DeviceAuthorizations.cs`) holds the pending codes in memory, with their caps, the
polling interval and each person's failed codes. The approval routes read the cookie
ticket, never `HttpContext.User`, so a bearer approves nothing. A collected approval
becomes a `terminal_tokens` row through `HubDatabase.Tokens.cs`, holding the SHA-256 of the
token, the approving subject and the provider groups it held. `HubTokenHandler.TerminalAsync`
reads a `pshub_` bearer back into a principal of kind `terminal_token`, with those frozen
groups, step 1's `RequiredGroups` check, and never admin.

**Where the scope applies.** `FindingQuery.Access` and `IncidentQuery.Access` carry the
`findings.read` scope into SQL: with patterns, the findings read always takes its scoped
branch and every place it tested a source tests the `(signature, source_id)` pair against
`visible`, before paging. The ack relay reads the copy at that source with
`ReadAckTargetAsync` and matches it in C#. A run's visibility, `RunRule` in
`ApiEndpoints.Analysis.cs`, reads the copies of the requested service in the run's
environment through `ReadServiceCopiesAsync`, once per environment and service for a
request, and the listing walks `analysis_runs` newest first until it has its limit.

## Disclosures

A periodic disclosure is made and signed outside the Hub: a scheduled job, in practice a CI
workflow, runs `perf-sentinel disclose` over the production daemons' archives and cosign over
its output, then publishes it to the Hub. The Hub adds no figure to it. Its code lives in
`Disclosures/` and in three route files of `Api/`.

**Publishing.** `ApiEndpoints.Disclosures.cs` admits a request on the import key, its size, its
media type and a gate of one, reads the three parts, and has `DisclosureEnvelope` read the
report's identity, period, confidentiality and headline figures. `DisclosureRenderer` writes
the files to a private temporary directory and runs the embedded engine's `disclose-pdf` on
them, reading only its exit code and its one reason line, never its stderr. Exit 0 stores the
PDF, the content hash checked and the binding too when an attestation came. Exit 1 and 3
refuse the publication and store nothing, save `output_unwritable`, the Hub's own trouble.
That one, exit 5, a timeout and an engine without `disclose-pdf` store the files without a
PDF. The publication's
`published` events are queued in the same request.

**Storage.** Schema V13 (`Storage/Schema.cs`) adds the three tables above. The files sit in
BLOB columns exactly as received, so the backup's `VACUUM INTO` carries them, and a listing
never selects those columns. A share link is stored as the SHA-256 of its token. A delivery row
holds the webhook's name, never its URL.

**Reading and sharing.** `ApiEndpoints.DisclosureReads.cs` serves the list, the detail, the
files and the manage actions behind `disclosures.read` and `disclosures.manage`.
`DisclosureDownloads` names the five files both it and `ApiEndpoints.Share.cs` serve, and
computes `SHA256SUMS`. `/share/` stands outside the access policy and answers one bare `404`
for everything it does not serve. `ShareDownloadCounter` counts downloads in memory, so an
anonymous request never writes to SQLite, and `ShareDownloadFlusher` adds them to the links
once a minute.

**Webhooks.** `DeliveryLoop` runs `DeliveryWorker` every 15 seconds over the due rows of
`disclosure_deliveries`. `WebhookDelivery` writes the body and its HMAC. The worker posts
through a named HTTP client of its own, which follows no redirect, keeps no cookie and logs
nothing, so a webhook URL never reaches a log line.

## A run always reaches a terminal state

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/run-states_dark.svg">
  <img alt="The states a run passes through and who moves each edge" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/run-states.svg">
</picture>

Whatever happens, a row does not stay `running`. A process that dies mid-run leaves an
orphaned row, and the next startup marks it `interrupted` rather than leaving it to be
wondered about.

Nothing is retried. A silent replay would fire a second heavy query at a backend nobody
asked to query twice, so an interrupted run waits for a human to decide. `expired` is
not a failure either: the report was deleted on schedule and the run keeps its
parameters, so it can be launched again as it was.

## Editing a diagram

The `.mmd` sources are the source of truth. They carry no `%%{init}%%` theme block on
purpose: the theme is an export argument, which is what lets one source produce both
the light and the dark SVG.

The export is manual, through [mermaid.live](https://mermaid.live): paste the source,
pick the theme, use the SVG export button. That is how the perf-sentinel family was
produced and the two match, which matters because the SVGs sit side by side in the same
kind of document.

`mermaid-cli` is not a drop-in replacement for that. Measured on this repository's own
sources, `mmdc` with no background flag writes `hsl(80, 100%, 96.2%)` where the existing
family has `rgb(255, 255, 255)`, and `-t dark -b '#232030'` still writes
`hsl(20, 1.6%, 12.4%)` rather than the family's `rgb(35, 32, 48)`, because the theme's
own background wins over the flag. It is useful for checking that a source parses at
all, which is worth doing before opening a browser:

```bash
npx -y @mermaid-js/mermaid-cli@latest -i docs/diagrams/mmd/<name>.mmd -o /tmp/check.svg
```

That invocation needs a Chrome or Chromium that Puppeteer can find, and says so plainly
when it cannot.

Both files of a pair must exist. A diagram shipped with only the light variant is a
white slab for every reader in dark mode, which is exactly the drift the naming
convention exists to make visible.

## Every arrow, against the code

The first diagram claims a topology. This is where that claim is checked: each arrow on
it corresponds to a real call. Named by symbol rather than by line, because a line number
is wrong the first time anyone edits above it.

| Arrow                                     | Where it lives                                                                                               |
|-------------------------------------------|--------------------------------------------------------------------------------------------------------------|
| Browser to Hub, the launcher reads        | `Api/ApiEndpoints.Analysis.cs`, the four routes it maps                                                      |
| Plugin or CI to Hub                       | `Api/ApiEndpoints.cs`, `GetFindingsAsync`                                                                    |
| Daemon to Hub, push                       | `Api/ApiEndpoints.cs`, `ImportFindingsAsync`, storing via `TryUpsertBatchAsync`                              |
| Hub to daemon, poll                       | `Collection/DaemonClient.cs`, `FetchStatusAsync` and `FetchFindingsAsync`                                    |
| Hub to daemon, export for a run           | `Collection/DaemonClient.cs`, `FetchReportSnapshotAsync`                                                     |
| Hub to daemon, config for an unfolded row | `Collection/DaemonClient.cs`, `FetchConfigAsync`                                                             |
| Hub to daemon, incidents                  | `Collection/DaemonClient.cs`, `FetchIncidentsPageAsync`, paged by `SourcePoller` into `UpsertIncidentsAsync` |
| Hub to daemon, acks                       | `Collection/DaemonClient.cs`, `FetchAcksAsync`, mirrored by `AckReader` into `ReplaceSourceAcksAsync`        |
| Hub to daemon, detection ledger           | `Collection/DaemonClient.cs`, `FetchDetectionsPageAsync`, paged by `DetectionReader`                         |
| Daemon to Hub, detection push             | `Api/ApiEndpoints.cs`, `ImportDetectionsAsync`, storing via `TryMergeDetectionPageAsync`                     |
| Hub to daemon, ack relay                  | `Api/ApiEndpoints.Acks.cs`, `RelayAckAsync`, written by `AckRelay` through `SendAckAsync`                    |
| Reachability set, and cleared             | `Collection/SourcePoller.cs`, the two `MarkSource` calls and `UpsertBatchAsync`                              |
| Hub to SQLite                             | `Storage/Schema.cs`                                                                                          |
| Hub spawns the engine                     | `Analysis/AnalysisRunner.cs`, twice per run                                                                  |
| Engine writes the report                  | `Analysis/AnalysisRunner.cs`                                                                                 |
| Report served into the iframe             | `Api/ApiEndpoints.Analysis.cs`, `GetReportAsync`                                                             |
| Hub to api.github.com                     | `Collection/UpdateChecker.cs`, `ReadAsync`, the only outbound call that is not a configured source           |
| CLI to Hub, terminal sign-in              | `Api/ApiEndpoints.Terminal.cs`, `StartDeviceAsync`, `PollDeviceAsync`, `RevokeTokenAsync`                    |
| Browser to Hub, approving a terminal      | `Api/ApiEndpoints.Terminal.cs`, `DevicePageAsync`, `VerifyDeviceAsync`, `ApproveDeviceAsync`                 |
| CLI to Hub, with a terminal token         | `Access/HubTokenHandler.cs`, `TerminalAsync`, over `FindTerminalTokenAsync`                                  |
