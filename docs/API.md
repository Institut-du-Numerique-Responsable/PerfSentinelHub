# HTTP API

Four surfaces, and they do not overlap. A daemon pushes into the import API. An IDE
plugin or a CI job reads the read API. The browser uses the analysis API and the ack relay,
reads `/api/incidents` for its incidents screen, and reads `/api/findings` only on the ack
page, by signature, once without a window and once over the last 7 days for the detection
volume. Periodic disclosures sit apart from all four: a scheduled job publishes them to a
route of their own, and the launcher, a share link or a webhook carries them on, see
[Disclosures](#disclosures).

## Import API

`POST /api/import/findings?source_id=<id>` accepts the daemon's envelope
`{"producer_version":"…","findings":[…]}` with `X-API-Key`. A request carries 1 to 100
findings and at most 2 MiB. The response is sent only after the idempotent signature
upsert commits. A body that repeats a property name within one object is a `400`, as the
Hub reads a finding with two parsers that would keep different copies of it.

`POST /api/import/incidents?source_id=<id>` accepts a daemon's incidents, pushed by perf-sentinel
0.29.0 and later beside its findings: `{"producer_version":"…","incidents":[…]}`, each incident in
the shape `GET /api/incidents` serves on the daemon. It takes the same key, the same four-slot gate
and the same five-second write wait, at most 10 incidents and 4 MiB per request, since one incident
carries up to a thousand frozen findings. A pushed incident is merged as a polled one is: the copy
with more frozen findings wins and an end is kept once seen, so push and poll converge in any
order. Refusals count on `perf_sentinel_hub_incident_import_rejected_total`.

`POST /api/import/correlations?source_id=<id>` accepts the correlation episodes a daemon on
perf-sentinel 0.30.0 or later pushes when `[daemon.correlation]` is on beside
`[daemon.hub_export]`: `{"producer_version":"…","episodes":[…]}`, each episode one period during
which a pair of findings kept following each other. It takes the same key, the same four-slot gate
and the same five-second write wait, 1 to 100 episodes and at most 1 MiB per request. Each episode
is checked on its own: a 32-character hex `id` and `pair_key`, a known finding type on both sides,
`started_at_ms` no later than `last_seen_ms`, an `ended_at_ms` absent or no earlier than the start,
confidences between 0 and 1, a positive `gap_ms`, co-occurrence counts of 0 or more, and sample trace
ids of at most 64 characters when present. An invalid one is counted in `rejected` while the others land, and a
batch with none valid is a `400`. Copies of one episode merge in any order: the earliest start, the
latest `last_seen_ms` and the highest peaks win, the rolling values come from the newer copy, and
an end is kept once seen. Refusals count on `perf_sentinel_hub_correlation_import_rejected_total`.

`POST /api/import/detections?source_id=<id>` accepts the detection ledger a daemon on
perf-sentinel 0.37.0 or later pushes beside its findings, one page in the shape
`GET /api/detections` serves on the daemon: `ledger_id`, `counts` and `days`. It takes the same
key, the same four-slot gate and the same five-second write wait, and at most 8 MiB per request.
The page is checked whole, with the bounds the poll applies: a GUID `ledger_id`, days from 400
before the Hub's day to 2 after it, signatures of at most 1,024 characters with no control
character, integers from 0 to 2^53, a sampling rate from 0 to 1. One value out of bounds is a
`400`, and more than 5,000 count rows or 31 days a `413`. A page with no count row is accepted,
since the daemon seals a day by pushing its day object alone. A day pushed `sealed` stays
sealed, and every value merges by its maximum, a sampling rate by its minimum, so a push, a poll
and a replay land on the same rows in any order. The push writes neither reachability nor the
time of the source's last findings push. Each day object may also carry `avoidable_gco2` and
`avoidable_gco2_low` (grams of CO2eq, from 0 to 2^53), `avoidable_io_ops` and `total_io_ops`
(integers), which a daemon that scores carbon sends together. They merge by maximum, an absent
value never lowering a stored one, and are absent on engines before the recap release. From the
card release, a count row may carry `below_threshold_passes` (an integer), the passes below the
threshold of a signature the Hub asks the daemon to watch, and such a row may carry `detections: 0`,
which no read counts as a detection. A day object may carry `thresholds`, an object with the
integer keys `slow_query_threshold_ms`, `slow_http_threshold_ms`, `max_fanout`, `chatty_threshold`,
`pool_saturation_threshold` and `serialized_min_calls`, the thresholds the daemon applied that
day; a key the Hub does not know is ignored. Both are optional and merge by maximum, an absent
value never lowering a stored one. Refusals count on
`perf_sentinel_hub_detection_import_rejected_total`.

A stored page answers `{"accepted":…,"rejected":0,"watch":[…]}`, `accepted` the number of count
rows. `watch` lists the signatures the Hub asks that daemon to count passes below the threshold
for: those whose action is `done`, whose type (the signature's prefix before the first `:`) is
`slow_sql`, `slow_http`, `slow_messaging`, `excessive_fanout`, `chatty_service`, `pool_saturation`
or `serialized_calls`, that this source detected at least once, and that this source has not yet
confirmed fixed. A day confirms once it is sealed after the day the action was done, with passes
below the threshold and no detection of the signature at this source, whatever its ledger. The
newest done come first, at most 10,000. The list is always sent, `[]` when empty, so the daemon
replaces its own on every answer. Only a push gets it: a polled daemon never does.

Four imports run at a time, which bounds request memory independently of how many daemons
there are. Writes are serialized against the poll and retention paths. An import that
cannot take the write lock within five seconds gets `503 Retry-After: 1`, and daemon
exporters retain and retry their coalesced batches. Retention purges in bounded chunks, so
a long purge does not reject imports for its whole duration.

A push updates findings, incidents, detection ledgers and per-source observations only. It
never clears the poll path's `unreachable_since_ms`. A source the Hub cannot reach still
reports `unreachable_since` while its daemon pushes successfully, and that is correct:
reachability is a fact about the Hub's route to the daemon, which a push does not exercise.

## Read API

| Endpoint                             | Returns                                                                                                                                                                                    |
|--------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `GET /api/status`                    | The Hub's version, the engine version it would run (`engine_version`, null when none is configured), and what a run costs: worker count, queue depth, trace cap, timeout, report retention. `disclosures: true` only while disclosures are on and the caller reads them. `cards`: `true` when the caller may make cards, `false` when the engine has no `card` subcommand, absent while cards are off or the caller holds `cards.write` nowhere |
| `GET /api/sources`                   | Every configured source with its kind and last known collection state                                                                                                                      |
| `GET /api/findings`                  | Findings, filtered by `service`, `finding_type`, `severity`, `status`, `signature`, `grouping`, `environment`, `source_id`, `from`, `to`, `offset`, `limit`, `include_acked`               |
| `GET /api/findings/{traceId}`        | Findings for a sample trace                                                                                                                                                                |
| `GET /api/sources/{sourceId}/daemon` | One daemon's applied settings and its own account of its state. See below                                                                                                                  |
| `GET /api/incidents`                 | The incidents the polled daemons recorded, newest first, filtered by `service`, `kind`, `namespace`, `environment`, `source_id`, `offset`, `limit`. Without their findings, see below      |
| `GET /api/incidents/{id}`            | One incident whole, the frozen findings included                                                                                                                                           |
| `POST /api/incidents/refresh`        | Reads every daemon's incidents ring now, then answers exactly as `GET /api/incidents`. Same parameters. See below                                                                          |
| `GET /api/correlations`              | The correlation episodes the daemons pushed, newest first, filtered by `service`, `source_id`, `environment`, `pair_key`, `from`, `to` and `active`. See below |
| `GET /api/access`                    | With `Hub:Access:Enabled` only: who the caller is and what they reach, plus the configuration for an admin. See [below](#get-apiaccess)                                                    |
| `GET /metrics`                       | Prometheus text format, see [OPERATIONS.md](OPERATIONS.md#metrics)                                                                                                                         |
| `GET /health/live`                   | Whether the process is up                                                                                                                                                                  |
| `GET /health/ready`                  | Successful after SQLite initialization                                                                                                                                                     |

On `/api/sources`, timestamps are null for a source never observed, which a reader must
not confuse with the epoch. `producer_version` is null for a trace backend, because a
backend stores traces and detects nothing.

`new_signatures_last_hour` counts the signatures a daemon first saw in the last hour,
null while the Hub has not judged it: before the first check, five minutes at most after
startup, and while every finding it holds dates from that hour, as on a first contact.
`signature_rate_warning` is true past `Hub:NewSignaturesPerHourWarning`, see
[OPERATIONS.md](OPERATIONS.md#metrics). `last_error_code` reads `database_full` when the
Hub's own volume is full: the daemon answered and the Hub could not store what it read.

`acks_state` and `acks_read_ms` say what the last read of a daemon's acknowledgments came to
and when it was taken, both null when none has run. The states are those of
`incidents_state`, where `absent` also covers a daemon below 0.24.0, which is never asked,
plus `truncated`: the listing reached the daemon's cap of a thousand acks, so its tail may
be missing.

`ack_relay` is true when the Hub holds a credential to write acks to that daemon, see
[CONFIGURATION.md](CONFIGURATION.md#the-ack-credential). Neither its header nor its value is
ever published.

On `/api/findings`, `include_acked` defaults to `true`. Set to `false`, it lists a finding
only while at least one source in scope holds it un-acknowledged, every source counting when
the read has no scope, see
[How `include_acked` judges a finding](#how-include_acked-judges-a-finding). `signature` is
an exact match. The Hub bounds it and leaves its shape to the daemon: past 1,024 characters,
or with a control character, it is a `400`. A `service`, `finding_type`, `severity`,
`status`, `signature` or `grouping` given empty or as whitespace only reads as absent, which
is what a dashboard sends for its "All" choice. The rows come in a total order, `last_seen` descending
then `signature`, and `offset` skips that many of them before `limit` applies. `offset` runs
from 0 to 1,000,000, and a value outside that range is a `400` rather than a clamped page.

`grouping` is the daemon's own filter of that name, an exact match on a finding's effective
grouping value, the first `[detection] grouping_attributes` its spans carried, by default
`k8s.namespace.name`. A free string like `service`, so an unknown one is an empty page. The
signature is grouping-blind, so one finding can be seen in several namespaces while its
envelope names only the last: the Hub keeps every grouping a source reported it in, and the
filter matches any of them, from a source in scope. A daemon's push coalesces a signature
across groupings, while its listing has one row per grouping, so the poll is what records
each of them. It does not narrow `from` and `to`: a
windowed read lists a finding seen in the window and seen in that grouping, not necessarily
both at once.

`environment` and `source_id` scope the read, to the sources of one environment or to one
source. Both are closed sets, the Hub's configured sources, and a value outside them is a
`400` rather than an empty page, because a typo must not read as "no findings". Given empty
or as whitespace only, either reads as absent like the filters above, so it is neither a
`400` nor an empty page. `environment` resolves to every source configured with it. Given
together with `source_id` the two intersect, so a source outside the named environment
lists nothing, the answer a pair of filters that exclude each other has. Without either, the
read covers the whole fleet as it always has. A scoped answer describes its scope rather
than the fleet, see [What the Hub adds to a finding](#what-the-hub-adds-to-a-finding).

`from` and `to` bound the read to an observation window, in epoch milliseconds, each
optional and both inclusive. A finding is listed when a source in scope observed it inside
the window, or counted it there (see the detection volume below), so the window honours
`environment` and `source_id`. It selects rows and does
not rewrite them: `first_seen`, `last_seen` and `status` still describe the finding as it
stands now. A value that is not a plain non-negative integer, a sign included, is a `400`,
and so is a `from` greater than `to`. Given empty or as whitespace only, either reads as
absent and leaves its side open.

The window has the granularity of a day. The Hub records the days each source observed a
finding, whole UTC days on its own clock, so a bound that falls inside a day takes that
whole day. Before the first day the Hub recorded for a source, that source is assumed to
have carried the finding from its `first_seen` to the start of that day, and up to its
`last_seen` when no day was ever recorded, which is the case of a database written before
the record existed. [LIMITATIONS.md](LIMITATIONS.md) says what that assumption costs.

### The daemon view

`GET /api/sources/{sourceId}/daemon` reads on demand rather than from the poll. Settings
never change without a restart the Hub has no signal for, and the gauges are the point of
the view.

**Failure is an observation, not a fault.** An unknown source answers `404` and a trace
backend `400`, since it runs no daemon. A daemon that does not answer is reported as
`state: "unreachable"` with an error code, never as a `502`: the Hub relays a source's
health, it does not fail itself.

**What it relays verbatim.** `config` is the daemon's `[daemon]` section. It is null with
`config_unavailable_reason: "api_disabled"` when that daemon serves no query API, which is
a configuration statement rather than a fault. `detection_config`, `scoring_config` and
`energy_model` come from the daemon's export, where `/api/config` does not carry them.
`warnings` is the daemon's own tuning advisor: the Hub relays those sentences and writes
none of its own.

**What it bounds.** A hint past two thousand characters is cut with a visible ellipsis.
Anything past a hundred hints is counted in `warnings_dropped` rather than silently gone.
A failed export read is named in `hints_unavailable_reason` instead of reading as a clean
bill.

**What it derives.** Two things. `state`, from whether a gauge crossed 90 % of its cap,
the same line the daemon's own monitor draws. And `energy_source`, where the snapshot's
energy figure comes from, worded as the engine words it since 0.26.0 (`source scaphandre_rapl
on 1 of 2 services · rest modeled from I/O counts`, null when no energy was computed). It
reads per-service coverage, never `energy_model`, which names the carbon intensity source or
tier rather than an energy backend. Since 0.32.0 a database or broker host figure that the
daemon counts inside its totals adds its own model (`· plus database hosts,
database_specpower`), and a figure beside the totals adds nothing. It also carries `daemon_defaults`,
`detection_defaults` and `defaults_engine_version`, so a reader can mark what a daemon
actually changed. Those defaults belong to the binary this Hub embeds, so the version is
named rather than assumed and a daemon on another minor is flagged rather than judged.

**How often it reads.** An open row re-reads on the interval its reader picks.
`?refresh=status` makes that tick a single status read instead of the three a full view
takes, because the export is the heavy one and runs at most once a minute. A row whose
read failed re-reads with that same cheap request, and the first one that answers is
followed straight away by a full read, so a row left open recovers by itself.

This polling cannot starve the daemon. The engine's 32-concurrent-request cap is scoped to
its OTLP ingest route precisely so `/api` and `/health` stay responsive, status ticks take
no Hub read slot, and full reads are bounded at two at a time over pooled connections. The
daemon's query surface is HTTP(S) only by design, its gRPC port being OTLP ingest, so the
Hub speaks no RPC to it.

### Incidents

A perf-sentinel daemon from 0.20.0 with `[daemon.incidents]` enabled records an incident
when the operator's alerting posts one, and freezes the findings of the minutes before
it. The Hub copies that record on every poll of the daemon and re-derives nothing: the
findings inside an incident are the daemon's, frozen at capture and settled once, and the
Hub's own finding of the same signature may since have moved on. The copy outlives the
daemon's ring, which dies with the daemon. A copy is one daemon's capture, keyed on the
incident's `id` and the `source_id` together: the id hashes the service, the kind and the
alerting's stamp and nothing of the daemon, so two daemons fed the same alert list the same
id and each keeps its own frozen findings. Of two captures of the same incident by one
daemon the richer one is kept, since Alertmanager repeats a firing alert and a daemon
restarted in between freezes a window its ring no longer reaches. Copies expire with the
findings retention, on the Hub's own clock and never on the alerting's `at_ms`.

The poll reads the daemon's ring a page at a time under a 4 MiB body cap. A page that
overflows it is re-read at half the size from the same offset, down to a single incident,
since the daemon embeds up to a thousand findings per incident and a full page of a busy
daemon never fits while one incident always does. An incident that overflows the cap on
its own is filed as `response_too_large`, and a page that is not a JSON array as
`invalid_incidents`.

The poll reads the route with the source's `AuthHeaderName` and `AuthHeaderValue`, so a
read key is `AuthHeaderName=X-API-Key` with the daemon's `[daemon] read_api_key` as the
value. What the read came to is `incidents_state` on `/api/sources`: `ok`, `absent` (a
daemon before 0.20.0 answers 404 and one with the store disabled 503, neither a failure),
`unauthorized`, `error`, or null when no poll has run yet. None of these touches the
source's reachability: the findings were collected on the same poll, and a refused key
must not demote every finding that daemon reported.

`GET /api/incidents` lists the copies newest first, one row per `(id, source_id)`, without
their findings, which can run to a thousand per incident and are never read for the
listing. Each carries the daemon's own fields plus `source_id`, `source_name`,
`environment`, `first_seen`, `last_seen` (Hub clock), `finding_count` and `capture`:
`complete` when `oldest_finding_ms` is at or below `window_from_ms`, `partial` when the
ring had already evicted part of the window, `empty` when it held nothing. Among the
daemon's fields, `namespace` is the alert label a 0.20.0 daemon carries when the alert
named one, relayed as the daemon wrote it and absent when it did not: a tag for reading
and filtering, never a key.

The listing filters on `service`, `kind`, `namespace`, `environment` and `source_id`, and
pages with `offset` and `limit`. `service` and `namespace` are free strings matched
exactly, and an unknown one is an empty page. `kind`, `environment` and `source_id` are
closed sets, the daemon's five kinds and the Hub's configured sources, and a value outside
them is a `400` rather than an empty page, because a typo must not read as "no incidents".
Any of these five filters given empty or as whitespace only reads as absent, closed sets
included, so it is neither a `400` nor an empty page. `environment` resolves to every
source configured with it. Given together with `source_id` the two intersect, so a source
outside the named environment lists nothing, the answer a pair of filters that exclude
each other has.

`GET /api/incidents/{id}` returns one incident whole, findings included, the richest copy
when several sources hold the id. A finding whose `first_seen_ms` is past `at_ms` fired
only after the restart. Like `/api/findings`, both
answer whoever reaches the Hub's port: a daemon's read key protects the daemon, and the
Hub re-exposes the frozen findings behind whatever fronts the Hub.

`POST /api/incidents/refresh` reads the fleet on demand, the way the daemon view does, and
the incidents screen calls it every time it is opened. The poll is the floor rather than
the only path: an operator paged about an OOM kill opens the screen within the minute and
`Hub:PollInterval` is an hour, so a poll-only screen would show nothing until the next one.
A POST because it writes to the store, and because a GET would be cached and prefetched,
which a fleet read must never be. It fans out over every source of kind `daemon`, bounded
by `Hub:MaxConcurrentPolls` exactly as the poll worker is, and answers the same body as
`GET /api/incidents` with the same parameters, validated identically, so a screen needs
one round trip. The filters narrow the answer, never the read: the fan-out covers the
whole fleet whatever the query asks for. It shares the poll's failure isolation: a
401, a 404, a 503 or an oversized page files its own `incidents_state` and never touches
the source's reachability, and one refusing daemon costs the others nothing.

What it guarantees the daemons: a source whose last read is younger than ten seconds is
skipped and its stored copy served instead, so a reload loop, or five people watching the
same screen, cannot storm the fleet. That floor is a constant and not a setting, because it
protects the daemons from this Hub rather than expressing a preference. A second guarantee
sits in front of it, a gate of two concurrent refreshes, which answers `503` with
`Retry-After: 1` past that, since each refresh buffers a page per daemon under the 4 MiB
body cap. `incidents_read_ms` on `/api/sources` carries when each copy was taken, null
alongside a null `incidents_state` when none ever was: without it a quiet fleet and a stale
copy read the same.

### Correlations

A perf-sentinel daemon from 0.30.0 with `[daemon.correlation]` on and `[daemon.hub_export]` set
pushes each period during which a pair of findings keeps following each other as an episode, and
closes it once the pair has not co-occurred for its gap, `min(window, 30 min)` and 10 minutes by
default. The daemon cuts the episodes and the Hub stores them as pushed, keyed on the episode `id`
and the `source_id`. Nothing is polled: a daemon without `[daemon.hub_export]` leaves no
correlation history in the Hub.

`GET /api/correlations` answers a JSON array of episodes, newest first by `started_at_ms`, in the
shape the daemon pushes them: `id`, `pair_key`, `source` and `target` (each with `finding_type`,
`service`, `template`, `grouping_key` and `grouping_value`, the last two null for a finding with
no grouping), `started_at_ms`, `last_seen_ms`, `ended_at_ms`, `gap_ms`, the peaks
`peak_confidence` and `peak_co_occurrences`, the latest `last_confidence`, `last_co_occurrences`
and `median_lag_ms`, and the two sample traces `source_sample_trace_id` and `sample_trace_id`.
The Hub adds `source_id`, `source_name`, `ended_by`, and `first_seen_ms` and `last_seen_hub_ms`
on its own clock. `ended_at_ms` is null while the episode runs. An open episode whose
`last_seen_ms` is older than its `gap_ms` plus 15 minutes is served as ended at its
`last_seen_ms` with `ended_by` set to `stale`, most likely a daemon stopped before it could close
it, and `ended_by` is null on every other episode. The stored row keeps no end, so a late close
still lands. The same pair seen under two groupings has two `pair_key` values.

The listing filters on `service`, matched on either side, `pair_key`, `source_id`,
`environment`, `from` and `to`, which keep the episodes whose activity overlaps the span, bounds
included and a running episode included, and `active=true`, which keeps the running ones. A
filter given empty or as whitespace only is no filter, which is what a Grafana variable set to
All sends. It pages with `offset` and `limit`, 200 by default and 1000 at most. `source_id` and
`environment` are closed sets as on `/api/incidents`, and `from` and `to` are epoch
milliseconds, so a value the route cannot read is a `400`. The route asks for `findings.read`: with access control on,
an episode is listed only when the caller reads both its services at its source.

### What the Hub adds to a finding

Each daemon finding is preserved as an opaque, additive JSON document. The Hub adds
`first_seen`, `last_seen`, `max_confidence`, `status`, an optional `lineage`, `sources`, one
entry per source that reported the finding, and an optional `acks`. An entry of `sources`
carries the source's `id`, the one `/api/sources` lists and `source_id` filters on, its
`name`, `environment` and `producer_version`, and how fresh its observation is. IDE clients
should ignore unknown fields, as they do with the daemon API.

`acks` lists the active acknowledgments the Hub mirrored from the daemons, one entry per
source of `sources` whose daemon holds one on this signature, in the same order. It is
absent when no source holds any, so a finding nobody acknowledged reads as it always has. An
entry carries the `source_id`, the `source` of the ack, `daemon` for one taken at runtime
and `toml` for one of the CI baseline, then `by`, `reason` when the daemon gave one, `at`,
and `expires_at` when the ack expires. `at` and `expires_at` are the daemon's text, relayed
as it came. An ack is active while it has no expiry or its expiry is still ahead on the
Hub's clock, and only a source whose last ack read came to `ok` or `truncated` is listed,
since the rows a failed read leaves behind prove nothing. The name is reserved like the
Hub's other fields: an `acks` property a daemon sends is dropped, and the daemon's own
`acknowledged_by` is relayed verbatim beside the Hub's list.

`first_seen` comes from the daemon envelope (`first_seen_ms`), clamped to the Hub's
observation time and to a Unix-ms sanity floor. Neither a daemon clock running ahead nor a
seconds-unit bug can distort it, and it falls back to the observation time when a producer
omits the field.

`last_seen` is the source daemon's last detection (`stored_at_ms`), clamped to the Hub's
observation time, and the observation time when a producer omits the field. A finding the
daemon still holds but no longer detects keeps its `last_seen` across polls and ages into
`not_observed` or `likely_resolved`. The clamp keeps a daemon clock running ahead from
holding a finding fresh, so retention, ordering and freshness comparisons never see a
time past the Hub's own. The observation day and the endpoint heartbeat follow the same
time.

Read with `environment` or `source_id`, an envelope describes that scope and not the fleet.
The daemon's document is the copy of the source in scope that saw the finding last, so
`severity` judges that copy. `first_seen` is the earliest and `last_seen` the latest over
the sources in scope, `status` is derived from that `last_seen` and from the heartbeats of
those sources alone, and `sources` and `acks` list only them. `max_confidence` stays
fleet-wide on purpose, the highest confidence any source ever reported. A source's own copy
starts at its first observation after the upgrade that records it. Until then its row
serves the copy the fleet shares, the freshest one from any source.

A read with `from` or `to` also serves how many times the daemons detected the finding in
the window, from the detection ledgers the Hub polls or receives. A read with neither carries
none of these fields. The finding and each entry of `sources` get five fields:

- `detections`: the count, an integer or null.
- `detections_complete`: true when no reason below applies, null with `detections`.
- `avoidable_io_ops`: the extra I/O operations of those detections, null when a counted day
  was read with green scoring off.
- `detections_gaps`: the sorted reasons the count falls short, empty when it is complete.
- `detections_sampling_rate`: the lowest sampling rate of the counted days. The Hub never
  extrapolates, a consumer that does should say so.

The finding also gets `detections_from` and `detections_to`, the UTC days counted, as
`YYYY-MM-DD`. They run from the day of `from`, never before the day the retention purge
keeps, to the day of `to`, never after the Hub's current day. A window wholly ahead of the
Hub's day counts nothing, and its `detections_from` is then the day after `detections_to`.
A window wholly before the retention cutoff serves null counts, and its `detections_from`
is likewise after its `detections_to`.

A count reads in one of three states:

- null when no day of the window is known for the source, whatever the ledger. This is the
  case of a daemon older than 0.37.0, of a disabled ledger and of history before the
  upgrade.
- complete, `detections_complete: true`, when every day of the window is known and no
  reason applies.
- incomplete, `detections_complete: false`, the sum of the known days with its reasons in
  `detections_gaps`. A known day with no row for the signature counts 0.

The reasons form a closed set:

- `uncovered_days`: a day of the window the source has no row for.
- `open_day`: a day not yet sealed, the Hub's current day included, or a day a ledger's
  counts reached the Hub before that ledger's own row for the day. Those counts wait for it.
- `restart`: a day the daemon restarted on.
- `sampling`: a day read at a sampling rate below 1.
- `shed`: a day the daemon shed traces under memory pressure.
- `ingest_rejected`: a day the daemon rejected ingest.
- `overflow`: a day a ledger overflowed its signature cap while this signature has no row
  in that same ledger that day. Another pod's row for it does not count.
- `multi_instance`: two polled ledgers of the source alive on the same window day, several
  pods behind one Service that a poll cannot all read. Ledgers the Hub only received by push
  add up without it.

At the finding level, `detections` and `avoidable_io_ops` are the sums over the sources
that are not null, and null only when every source is. `detections_complete` is true only
when every source is known and complete, `detections_gaps` is the union of the reasons, and
`detections_sampling_rate` the lowest rate of the known sources.

A source that counted the finding in the window without holding a copy of it joins
`sources`, named as the configuration names it. Its `producer_version` and `age_seconds`
are null, since both describe a copy. With access patterns, such a source is listed only
to a caller who reads the whole source. A finding the daemons counted in the window is
listed even when no poll observed it there, the ring having turned between two polls, as
long as the Hub already holds its body. A source that is no longer in `Hub:Sources`, a
retired daemon, has no name to serve: it is left out of `sources` and of the totals. A
scoped read, by `environment`, `source_id` or access patterns, only reaches findings with
at least one copy in scope. The seven names are reserved like the Hub's other fields: a
daemon property of the same name is dropped.

### How `include_acked` judges a finding

`include_acked=false` judges each source in scope that carries the finding, by whichever of
its two views the Hub read last. When the last read of that source's acks came to `ok` and
is no older than the source's last observation of the finding, the mirror decides: the
finding is acknowledged there when the mirror holds an active ack on its signature.
Otherwise the source's own copy of the envelope decides, by a non-null `acknowledged_by`,
which is all the Hub judged before it mirrored acks. A source whose acks were never read, a
failed read and a `truncated` listing all fall to the envelope, and a source with no copy of
its own yet is judged on the copy the fleet shares, in a scoped read too. The finding is
listed while at least one such source holds it un-acknowledged, so a finding acknowledged in
production stays listed for staging, and for the fleet that includes staging. The filter
applies before the page limit.

### How `status` is derived

Derived at read time, never stored, from data the Hub already keeps:

| Value             | Meaning                                                               |
|-------------------|-----------------------------------------------------------------------|
| `active`          | Seen within `Hub:ResolutionGrace`, 7 days by default                  |
| `likely_resolved` | Gone quiet, but its endpoint still heartbeats from a reachable source |
| `not_observed`    | Nothing proves anything: a silent endpoint, or an unreachable fleet   |

It is a presumption, not a verdict. A finding leaving by retention still leaves silently,
but a reader can now tell "the endpoint runs without the finding" apart from "nobody is
looking". `?status=<value>` filters, and the filter applies before the page limit. In a
scoped read the status is the scope's own: a finding production still reports can be
`not_observed` for staging, and only a heartbeat from a source in scope makes it
`likely_resolved` there.

### Lineage

`first_seen` is per signature, so a finding whose normalized template changes gets a new
signature and a new `first_seen`. Since schema v2 the Hub links such a mutation to its
predecessor at import time, when exactly one stored finding:

- shares the service, detector and endpoint,
- has a different template hash,
- was seen within the last 30 days and strictly before the incoming batch,
- is not a finding of the incoming batch, whose findings are all current problems,
- and is not itself already superseded.

A signature also changes when the **endpoint** changes, and that path is not linked. The
rule above holds the endpoint fixed and looks for a moved template, so the reverse, a
stable template on a renamed endpoint, finds no candidate and the successor carries no
`lineage` block. An engine upgrade that teaches the daemon to resolve an endpoint it used
to report as `unknown` is the case that produces this.

Ambiguity records nothing, because naming one of several candidates would be a guess.

A linked envelope carries a `lineage` object with `original_first_seen`, the earliest birth
along the chain, and `predecessors`, the chain length. Both are denormalized onto the newest
link, so a finding's full lineage survives the retention purge of every earlier hop. The
heuristic is conservative and non-destructive: the two rows stay separate findings, and the
predecessor ages out through normal retention.

## Ack relay

Two routes write at the daemon behind one source, with that daemon's own ack key. Both are
a `POST` with a JSON body, and neither is ever open: under `Hub:Auth` they need a session.

| Endpoint                                   | Body                                              | Does                                    |
|--------------------------------------------|---------------------------------------------------|-----------------------------------------|
| `POST /api/sources/{sourceId}/acks`        | `{"signature":"…","reason":"…","expires_at":"…"}` | Acknowledges the finding at that daemon |
| `POST /api/sources/{sourceId}/acks/revoke` | `{"signature":"…"}`                               | Revokes the ack that daemon holds       |

`expires_at` is optional, and an ack without one is permanent. A `by` in the body is
ignored: the Hub names the caller itself.

`GET /api/sources/{sourceId}/acks` lists the daemon acks the Hub mirrors at that source,
in the shape the daemon's own `GET /api/acks` lists them
(`[{"signature","by","reason","at","expires_at"}]`), unexpired, without the CI baseline, and
1000 at most. A report the Hub renders with `--hub-relay` reads its acknowledgments there,
and the mirror is read again after each write the relay makes, so the list follows at
once. The source is judged as for a write, the same `404` included. With access control
on, an ack shows when the caller reads its finding at that source, or reads the source as a
whole when the Hub no longer holds the finding, the rule a revoke of it follows.

The Hub judges a request in this order, and a request it refuses never reaches the daemon.
With access control on, steps of its own join these, see
[With access control](#with-access-control).

1. **The caller.** The signed-in user under `Hub:Auth`. Otherwise the value of
   `Hub:Analysis:IdentityHeader`, and only when `Hub:AckRelay:TrustIdentityHeader` is set,
   see [AUTHENTICATION.md](AUTHENTICATION.md#how-it-works). With nobody to name, a `403`.
   It comes first so that an unnamed caller learns nothing about the sources.
2. **The source.** An unknown id, a trace backend and a daemon with no ack credential all
   answer the same `404`. `ack_relay` on `/api/sources` says which sources relay.
3. **Where the request comes from.** A `Sec-Fetch-Site` header that is present and is not
   `same-origin` is a `403`, and a content type the framework does not read as JSON
   (`application/json` and the `+json` suffixed types) is a `415`.
   The Hub has no antiforgery token and no CORS policy, so these two are its defence
   against a page on another origin: a browser sets the first by itself, and the second
   cannot be sent across origins without a preflight nothing here answers.
4. **Room.** Two relays run at a time, and one more gets `503` with `Retry-After: 1`. A
   body over 8 KiB is a `413`.
5. **The body.** `signature` is required, at most 1,024 characters, free of control
   characters, and neither `.` nor `..`, which would leave the daemon's ack path. Its shape
   stays the daemon's rule. `reason` is required on an ack, not blank, and held to the same
   bounds. `expires_at` is an RFC 3339 time in the future. Anything else is a `400`.
6. **The pair.** The Hub must hold that finding at that source. A revoke passes on that
   too, and also on an ack of it the Hub mirrors from that source, which can outlive the
   finding. Otherwise a `404`. A daemon acknowledges any canonical signature, so this is
   what bounds the relay to findings the Hub knows.

The Hub then writes the daemon's request itself, and nothing the caller sent travels as
bytes. It is a `POST`, or a `DELETE` for a revoke, on `api/findings/{signature}/ack`, the
signature percent-encoded so that one holding a slash stays one path segment. It carries the
source's ack credential and never the read one. The body of an ack is
`{"by":"…","reason":"…","expires_at":"…"}`, `expires_at` written back in UTC to the second.
The caller travels as `by` on an ack and as `X-User-Id` on a revoke, unchanged when it is
printable ASCII with no space at either end, percent-encoded otherwise, and in the same
form on both so the two compare equal at the daemon. The whole exchange has three times
`Hub:HttpTimeout`.

| The daemon answers | The Hub answers                                                             |
|--------------------|-----------------------------------------------------------------------------|
| any `2xx`          | `204`                                                                       |
| `400`              | `400`, the daemon refused the signature as not canonical                    |
| `401`              | `502`, the daemon refused the Hub's ack credential                          |
| `404` on a revoke  | `404`, not acked at this daemon, or only by its CI baseline                 |
| `409`              | `409`, already acked at the daemon or by its CI baseline                    |
| `503`              | `503`, acknowledgments are disabled on this daemon                          |
| `507`              | `507`, the daemon's ack store is full                                       |
| nothing in time    | `504`                                                                       |
| anything else      | `502`, a `404` on an ack included, which means the daemon has no such route |

A `401` from the daemon is never relayed as one. It says the key the Hub holds is wrong,
which no sign-in mends, and the launcher answers a `401` by reloading into the sign-in.
Every refusal the Hub words itself carries `{"detail":"…"}`. The `413`, the `415` and the
`503` of a full gate have no body.

After a `2xx` the Hub reads that daemon's ack listing again, so `acks[]` and
`include_acked=false` follow at once instead of at the next poll. It does so for a source
the poll has reached, since the read is decided by the daemon's version, and a failure of
that read never turns the `204` into an error: the daemon took the write. Each relay logs
one line, event `1310` when the daemon answered and `1311` when it did not, with the
source, the action, the caller and the status. The reason and the credential are never
logged.

## Recap

`GET /api/recap` answers a review recap of one period: the findings worth a look first, what
moved since the period before, what the teams did about them, and what the avoidable I/O
may cost in carbon. It is computed from the same reads as `/api/findings`, so the scope,
the filters, access control and the detection volume are the ones of that list. It is never
open: under `Hub:Auth` it needs a session or a token, and a caller with `findings.read`
nowhere gets the `403` the findings list gives.

| Parameter                   | Default           | Does                                                             |
|-----------------------------|-------------------|------------------------------------------------------------------|
| `period`                    | `week`            | `day`, `week` (ISO, Monday to Sunday, UTC) or `month` (calendar) |
| `at`                        | today, UTC        | Any day of the period, `yyyy-MM-dd`; a day after today is a `400` |
| `view`                      | `tech`            | `tech` (Engineering) or `pilotage` (Management)                  |
| `environment`, `source_id`  | the whole fleet   | As on `/api/findings`                                            |
| `service`, `grouping`       | none              | As on `/api/findings`                                            |

A repeated parameter, a value the list does not know or invalid UTF-8 is a `400`.

The answer is a snapshot, `schema_version` `1`, with `view`, `generated_at_ms`, `period` and
`previous` (each `kind`, `from`, `to`, `in_progress`, `incomplete[]`, `notes[]`,
`sampling_rate`, `truncated_to_days`), `filters` (as asked), `comparison_exact`, `tiles`,
`priorities[]`, `more_priorities`, `movements`, `nothing_moved`, `sources_without_volume[]`
and `truncated`.

- **Comparing.** A period in progress is compared on its closed days: `FromDay` to
  yesterday against the same number of first days of the previous period, reported in
  `previous.truncated_to_days`. Today is partial and would read as a drop against a full
  day. On the first day of a period there is no closed day: every trend is `unknown`, the
  `new`, `gone`, `up` and `down` lists are empty, the previous period is not read and
  `truncated_to_days` is `0`. The tiles and the ranking keep today's partial volume and
  are labelled in progress by `period.in_progress`.
- **Ranking.** Unacknowledged findings only. First those with avoidable I/O on the period,
  by that volume, then the others by severity (critical, warning, info); ties go to the
  finding detected more often, then to the signature. `rank` starts at `1`. The `tech` view
  lists every ranked finding, `pilotage` the first five and `more_priorities` counts the rest.
- **Trends.** Each ranked finding is `new` (absent from the previous period), `up`, `down`
  or `flat` against its own volume in the previous period, `change` being the relative
  difference. The metric is the avoidable I/O when either side has it, the detections
  otherwise. `Hub:Recaps:MovementThreshold` (default `0.2`, above `0` and at most `10`)
  is the change past which a finding reads `up` or `down`. A finding the data cannot
  compare, or seen today only, reads `unknown`, never `down`.
- **Movements.** `new`, `up` and `down` follow the trends, in ranking order. `gone` lists the
  unacknowledged findings of the previous period that the current period does not hold at
  all, an acknowledged finding not being gone, in their previous ranking, with the trend
  `gone` and no change; `gone_with_confirmed_action`
  counts those whose fix was confirmed. `came_back` and `done_still_detected` list the
  findings of the period whose `done` action came back or was contradicted.
  `nothing_moved` is true when none of those lists holds anything and no finding of ranks 1
  to 10 went `up` or `down`.
- **Incomplete and notes.** `incomplete` names what forbids an exact comparison:
  `uncovered_days`, `open_day` (a past day not sealed), `restart` (a daemon restart
  interrupted its counts) and `overflow`. `notes` names what is worth a line and leaves the
  comparison standing: `sampling` (with `sampling_rate`, the lowest over the period), `shed`,
  `ingest_rejected` and `multi_instance`. `comparison_exact` is true when neither period is
  incomplete. Today alone never makes a period incomplete.
- **Sources without volume.** `sources_without_volume` names the sources holding a finding of
  either period and no detection ledger (a daemon that predates the ledger, or one that
  sends no day). Their findings are ranked by severity and carry `volume_known: false`.
- **Tiles.** `avoidable_io_ops` is the sum over the ranked findings, `null` when none has a
  known volume, next to `previous_avoidable_io_ops`, `null` when the previous period was
  not read. `active_findings` counts the ranked findings, `acked_findings` the
  acknowledged ones of the period, and `actions` counts the actions of the period's
  findings by status (`todo`, `in_progress`, `done`) and, among the `done` ones, the
  confirmations `confirmed`, `contradicted` and `came_back`.
- **Carbon.** `co2_grams` is context, never a sort key: an estimate, rounded to one
  significant figure, of the extra I/O priced at each day's own grams per avoidable I/O, as the
  engine sent them with its ledger days. It is a partial minimum when some sources send
  no coefficient: `co2_sources_without_coefficient` counts them and the figure is never
  extrapolated. With none, `co2_grams` is `null` and `co2_missing_reason` is
  `no_coefficient`, or `no_avoidable_io` when there is nothing to price. A priority carries
  its own `co2_grams` and `co2_partial`.
- **Actions and confirmation.** A priority carries its `action` (status, owner, `owner_display`,
  times and, in `tech`, the `events`). A `done` action is judged on the days after the one it was
  marked done: `confirmed` once a day sealed at a reachable source of the finding holds no
  detection, `contradicted` when it is detected before any clean day, `came_back` when it
  is detected after one, `awaiting` while no day is sealed yet. A source that is
  unreachable never confirms. The judgment stays within the filters of the request.
- **Labels.** A priority and every movement item carry `label`, the business name someone gave
  to the endpoint they come from (see [Endpoint labels](#endpoint-labels)), or `null`, and
  `service_label`, the business name of their service, or `null`. Both keys are always there,
  in every view, and `service` stays beside `service_label`, so a receiver can still group by
  the technical service. `filters.service_label` is the name of the service filter, or `null`,
  and `null` too when the caller reads no finding of that service in either period: a typed
  filter never reveals the name of a service outside the caller's scope.
- **Two views.** `pilotage` is the `tech` snapshot with `view: "pilotage"`, the top five
  priorities, a fixed plain-language sentence per finding type in `plain`, and none of the
  technical keys: `signature`, `template`, `suggestion`, `suggested_fix`, `code_location`,
  `confidence` and the action `events` are absent, not `null`. So is `endpoint`, except on a
  priority or a movement item whose `label` is `null`: two rows of the same type stay apart
  by the one technical value left. A frozen report and a recap webhook drop `endpoint` on those
  rows too (see [Recap reports](#recap-reports)).
- **Cap.** A read of more than 5,000 findings keeps the first 5,000 and sets
  `truncated: true`; narrow the request with a filter.

### Recap webhooks

Each webhook named by a subscription, declared in `Hub:Recaps:Webhooks`, receives a `POST` with a JSON
body when the subscription's report of a closed period is frozen. A period in which nothing moved is
archived and sent nowhere. The body carries the management view only: the `recap` object is the
`pilotage` snapshot described above, so it holds none of the engineering keys, and no `endpoint` at
all: a row that has no label keeps its `null` `label` and its `service`, nothing more. An action has
a `null` `owner` and carries the owner's name only, in `owner_display`, as the attached page shows it:
no e-mail or account reaches a receiver. The engineering view reaches a receiver as a link, never as
content.

```json
{"event":"recap","report_id":"…","subscription":"weekly-shop",
 "period":{"kind":"week","from":"2026-10-19","to":"2026-10-25"},"complete":true,
 "pilotage_url":"https://hub.example/recaps/<id>/pilotage.html","tech_url":null,
 "recap":{"schema_version":1,"view":"pilotage"}}
```

`pilotage_url` and `tech_url` name the frozen page when the report holds that view and
`Hub:Recaps:PublicUrl` (or `Hub:Disclosures:PublicUrl` when unset) is set, and are `null`
otherwise. `recap` is shortened above. The signature headers, the retries and the 8 attempts are those of
[disclosure webhooks](#webhooks): `X-PerfSentinel-Timestamp`, `X-PerfSentinel-Signature` under the
webhook's `Secret`, no redirect followed, a `2xx` settles a delivery, anything else is tried again
after 30 seconds then a doubling wait capped at one hour. A recap webhook with `IncludeShareLink`
set is rejected at startup. `Confidentiality` (which must stay `public`) and `ShareLinkDays` (1 to 30)
are validated but unused here.

Deliveries run every 15 seconds from the `recap_deliveries` table, so a restart resumes them. A
delivery to a webhook no longer configured, or whose report was purged meanwhile, fails without a
request.

## Recap reports

A recap report is the recap of one closed period, frozen: the tech snapshot exactly as it was computed,
stored once, with the deliveries that carried it out. A subscription in `Hub:Recaps:Subscriptions`
produces one per cadence and period; a caller can also ask for one. Reading a report never recomputes
it, so a link keeps showing what the team was told on the day.

| Route                                   | Does                                                                    |
|-----------------------------------------|-------------------------------------------------------------------------|
| `GET /api/recap/reports`                | The newest 200 reports, without snapshots, each with its deliveries     |
| `POST /api/recap/reports`               | Generates a report on demand, `201` with the same item                  |
| `GET /api/recap/reports/{id}`           | `{"report": …, "snapshot": …}`, the snapshot served as it was stored    |
| `GET /recaps/{id}/{view}.html`          | The frozen page of one view, `pilotage` or `tech`                       |

A report item holds `id` (32 lowercase hex), `subscription` (`null` for an on-demand report),
`period` (`kind`, `from`, `to`), `views[]`, `complete` (no `incomplete` reason in the snapshot),
`nothing_moved`, `created_by` (`null` for a subscription report), `generated_at_ms` and
`deliveries[]`: `channel` (`webhook` or `email`), `target`, `state` (`pending`, `delivered` or
`failed`), `attempts`, `last_attempt_ms`, `next_attempt_ms` and `last_status`. The HTML page lives
outside `/api`, so a browser that follows a mailed link and holds no session is sent to sign in.

**On demand.** The body is a JSON object, at most 4 KiB, with the same-origin rule and the
JSON content type of the other writes:

| Field                       | Does                                                                                   |
|-----------------------------|----------------------------------------------------------------------------------------|
| `period`                    | Required: `day`, `week` or `month`                                                     |
| `at`                        | Any day of the period, `yyyy-MM-dd`, from 1900; default: the last closed period        |
| `environment`, `source_id`  | As on `/api/recap`; an unknown one is `400 Unknown environment or source_id.`          |
| `service`, `grouping`       | As on `/api/recap`                                                                     |

Each string is 1 to 256 characters with no control character. A period that has not ended before
today is `400 A report covers a closed period; pick a date in an earlier one.` The report is built
from the copies the caller reads under `findings.read`, as `GET /api/recap` is, stored in the
caller's name with both views and no delivery. A caller the Hub cannot name gets `403`, a
cross-site request `403`, a wrong content type `415`, a body over 4 KiB `413`.

**Who reads what.** A holder of `recaps.read` reads every report. Anyone else with `findings.read`
reads the reports they generated, matched on the account's immutable subject
(`Hub:Auth:SubjectClaim`), never on the name shown in `created_by`, and nothing else; a caller
without a subject owns none, and a subscription report has no creator, so no one matches it. A report the caller may not read
answers the `404` an unknown id gets, with the same body, on the three routes that name one; the
list answers `403 {"detail":"You may not read recap reports."}` to a caller holding neither
permission, and only the caller's own reports otherwise.

**The page.** `Content-Type: text/html; charset=utf-8`,
`Content-Disposition: inline; filename="perf-sentinel-recap-<from>-<view>.html"`,
`Cache-Control: no-store`, `X-Content-Type-Options: nosniff` and a `Content-Security-Policy` that
allows nothing but the two inline scripts of the page, by hash. `pilotage` is the management
projection of the stored snapshot, made when the page is served, with two cuts the live screen does
not make. A row with no label shows no endpoint: its title reads "An unnamed step in" and the
service, with "Name it in the Hub to see it here." under it. Wherever a management row names its
service, it uses the service's business name when someone gave one (`An unnamed step in Payment`,
`in Payment`, and `Scope: Payment` for a service filter). An action owner is the name a person reads
(`Alice Martin` for `alice.martin@acme.com` or `Alice.Martin@Acme.com`, `Alice` for
`Alice <alice@acme.com>`), and the identity behind it (an e-mail, a `local:` name, a subject) is in
neither the text, a `title` nor the data island. An owner that leaves no name once the e-mail is
gone shows no owner. The `tech` page keeps that identity on hover. An id that is not 32 lowercase
hex, an unknown id and a view the report does not hold are all `404`. Reports are purged after
`Hub:Recaps:Retention`.

## Share cards

A share card is an image or a PDF of a closed period's results that the engine draws and signs,
for a reader outside the Hub to check. This section starts with the signing key; see
[OPERATIONS.md](OPERATIONS.md#share-card-keys) for where it lives and how to publish it. The routes
exist only while `Hub:Cards:Organisation` is set: otherwise every one answers `404`.

| Route                            | Does                                                                 |
|----------------------------------|----------------------------------------------------------------------|
| `GET /api/cards/keys`            | Every signing key, oldest first, and the lines to publish            |
| `POST /api/cards/keys/rotate`    | Retires the current key and has the engine make a new one            |
| `POST /api/cards/keys/revoke`    | Revokes a key from the first day it may have leaked                  |

The three routes answer the same object:

```json
{"record_name": "_perf-sentinel.example.com",
 "keys": [{"fingerprint": "ps1:…", "public_key": "…", "from": "2026-10-30", "to": null,
           "revoked": null, "current": true, "dns": "v=ps1; k=ps1:…; from=2026-10-30"}],
 "well_known": "{\n  \"keys\": [\n …"}
```

`from` is the UTC day the key was made, `to` the day it was retired (`null` while current),
`revoked` the day given at revocation. `dns` is the TXT record of that key under `record_name`,
which is `null` when `Hub:Cards:Domain` is not set. `well_known` is the whole
`/.well-known/perf-sentinel-key` file, indented with `\n` line ends, to serve as it is. Reading the
keys never makes one: an empty `keys` says the first card makes it.

`POST /api/cards/keys/revoke` takes `{"fingerprint": "ps1:…", "revoked": "yyyy-MM-dd"}`, at most
1 KiB. `revoked` has no default: give the first day the key may have leaked, since a day too late
leaves a card forged before it valid. Missing or not a `yyyy-MM-dd` day, it is
`400 Give the first day the key may have leaked.`; a day before the key was made or after today
(UTC) is `400 The revocation date must lie between the key's creation and today.`; an unknown
fingerprint is `404`. A retired key can be revoked, and keeps its `to`.

The Hub judges a request in this order: cards off `404`; a caller the Hub cannot name `403`; on
the two `POST`s, the same-origin rule and the JSON content type of the other writes (`403`,
`415`); a caller without `access.admin` `403 You may not manage the card signing key.`. A rotation
with an engine that has no `card` subcommand is `503 This Hub's engine cannot make cards yet.`, and
one whose engine answers out of contract or past 30 seconds is `502`.

### Making a card

| Route                            | Does                                                                 |
|----------------------------------|----------------------------------------------------------------------|
| `GET /api/cards/options`         | What the screen shows before anyone signs: the period's refusal, the signed scope, the fixes |
| `POST /api/cards/render`         | Computes the figures, has the engine sign and draw the card, answers the file |
| `POST /api/cards/attachment`     | Renders the card as a PNG and checks its DNS attachment              |

**Options.** The query takes `period` (required: `week`, `month`, `quarter`, `semester` or `year`),
`at` (any day of the period, `yyyy-MM-dd`, default the last closed period), `environment` and
`source_id`. A period still running is read, not refused: the answer carries its refusal so the
screen says why before anyone asks for a card. An invalid query is `400`. The answer:

```json
{"period": {"kind": "month", "from": "2026-10-01", "to": "2026-10-31"}, "period_name": "October 2026",
 "refusal": null, "refusal_text": null, "restart_days": 0, "sampled_permille": 1000,
 "environment": "prod", "sources": ["prod-eu"], "previous_complete": true,
 "fixes": [{"signature": "…", "finding_type": "n_plus_one_sql", "service": "order-svc",
            "endpoint": "GET /orders", "plain": "…", "done_at_ms": 1792058400000,
            "before_fix_ops": 1900, "before_period_name": "September 2026", "label": "Order list"}],
 "default_signature": "…", "org": {"name": "Acme", "domain": "acme.example"}}
```

`refusal` is the period's own reason (below) and `refusal_text` its sentence, the one a render
would answer. `sources` is the signed scope: the sources asked for that recorded something in the
period. `previous_complete` is `false` when a collective card leaves out its comparison with the
period before. `fixes` are the confirmed fixes a story can tell, with the endpoint's label when one
is set.

**Render and attachment.** The body is a JSON object, at most 4 KiB, with the same-origin rule and
the JSON content type of the other writes:

| Field                       | Does                                                                                   |
|-----------------------------|----------------------------------------------------------------------------------------|
| `angle`                     | Required: `story`, `curve` or `collective`                                             |
| `period`                    | Required: `week`, `month`, `quarter`, `semester` or `year`                             |
| `at`                        | Any day of the period, `yyyy-MM-dd`, from 1900; default: the last closed period        |
| `environment`, `source_id`  | The scope; an unknown one is `400 Unknown environment or source_id.`                   |
| `signature`                 | Story only: the fix to tell; default the fix with the largest volume before            |
| `title`, `team`             | Free text, 1 to 48 characters                                                          |
| `label`                     | Story only, free text, 1 to 80 characters                                              |
| `lang`                      | `en` (default) or `fr`                                                                 |
| `format`                    | `png` (default) or `pdf` (the carousel); the attachment check always renders a PNG    |

Free fields are trimmed, and a blank one is left out. One longer than its bound, or holding a
control or format character (a direction override, a zero-width space), is `400`. Any other key,
`figures`, `org`, `scope` or `generated_at_ms` for instance, is
`400 Only angle, period, at, environment, source_id, signature, title, team, label, lang and format may be sent: the figures come from the Hub.`
A period that has not ended before today is `400 A card covers a closed period. Pick an earlier one.`,
`signature` or `label` on another angle `400 signature and label belong to a story card.`

**Refusals.** A card the figures cannot support is `422` with a sentence as `detail`, the same one
the options carry as `refusal_text`. The reasons: `open_period`, `uncovered_days`, `open_day`,
`overflow` (the period is not whole), `previous_uncovered_days`, `previous_open_day`,
`previous_overflow` (a curve whose period before is not whole), `no_confirmed_fix`, `unknown_fix`,
`no_total_io` (no total activity to draw the curve from), `curve_needs_whole_sources` (the caller
reads only part of a source) and `unprintable_scope` (a source or environment name the card cannot
print). The engine can still refuse what the Hub sent:

| Engine reason                                 | Answer                                                                                                                        |
|-----------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------|
| `unsupported_characters` on a free field      | `422 The card's font cannot draw a character of this field. Remove it, or write it another way.` with `"field": "title"`, `"team"` or `"label"` |
| `unsupported_characters` on the organisation  | `422 The card's font cannot draw a character of the organisation's name. Ask an administrator to change Hub:Cards:Organisation.` |
| `compact_too_large`                           | `422 The card's QR code cannot hold this organisation's domain. Ask an administrator for a shorter Hub:Cards:Domain.`        |
| `unsupported_language`                        | `422 This Hub's engine cannot print cards in this language yet.`                                                              |
| anything else, a line out of contract, 45 s   | `502 The engine could not render this card.`                                                                                  |

**The file.** `200` with `Content-Type: image/png` or `application/pdf`,
`Content-Disposition: attachment; filename=perf-sentinel-card-<kind>-<from>.<format>`,
`Cache-Control: no-store` and `X-Content-Type-Options: nosniff`. The first card makes the signing
key. The Hub writes `card.json` and a copy of the private key in a private temporary directory,
deleted afterwards, and the key never appears on a command line.

**Attachment.** `409 Set Hub:Cards:Domain to check an attachment.` without a domain. Otherwise the
Hub renders the card as a PNG and runs `verify-card --online`, which reads the TXT record through
`cloudflare-dns.com` and `https://<domain>/.well-known/perf-sentinel-key`. The answer is the
engine's verdict and what the check reached:

```json
{"integrity": "ok", "level": 2, "domain": "acme.example", "key": "current", "reason": null,
 "contacted": ["cloudflare-dns.com", "acme.example"]}
```

A verdict line without its five keys, or none within 20 seconds, is `502`.

**Who may.** The Hub judges these three routes in this order: cards off `404`; a caller the Hub
cannot name `403 The Hub does not know who is asking, and a card is made in somebody's name.`; on
the two `POST`s, the same-origin rule and the JSON content type (`403`, `415`); no `cards.write`
anywhere `403 You may not create cards.`; no `findings.read` anywhere `403`; an engine without the
`card` subcommand
`503 This Hub's engine cannot render cards: it needs a perf-sentinel release with the card subcommand.`
Then the scope: the sources asked for that both permissions reach, or
`403 You may not create cards for these sources.` when none is left. A fix on a service the caller
cannot read is left out, and the curve, drawn from daemon-wide totals, keeps only the sources both
permissions hold whole.

## Actions

An action says who works on a finding and where it stands. The Hub keeps it itself: nothing
is written at a daemon. It is global to the signature, as an ack is, so a finding seen at
two sources carries one action. Two routes write it, with a JSON body, and neither is ever
open: under `Hub:Auth` they need a session.

| Endpoint             | Body                                                            | Does                                  |
|----------------------|-----------------------------------------------------------------|---------------------------------------|
| `POST /api/actions`  | `{"signature":"…","take":true}` or `{"signature":"…","status":"…","owner":"…"}` | Declares the action on a finding      |
| `PATCH /api/actions` | `{"signature":"…","status":"…","owner":"…"}`                    | Changes the status or the owner of it |

- `status` is `todo`, `in_progress` or `done`. Nothing defaults to `todo`.
- `owner` is a string of 1 to 256 characters with no control character. `null` on a `PATCH`
  clears it.
- `take: true` is a `POST` only and stands for `status: "in_progress"` with the caller as
  owner. It cannot be combined with `status` or `owner`.
- A `POST` needs `take: true` or a `status`, and an `owner` alone is not enough:
  `400 {"detail":"An action needs a status or take."}`. A `PATCH` needs a `status` or an
  `owner`. A `by` in the body is ignored: the Hub names the caller itself.

The Hub judges a request in this order, and a request it refuses writes nothing.

1. **The caller.** As for an ack, the signed-in user, or the proxy header when
   `Hub:AckRelay:TrustIdentityHeader` is set. With nobody to name, a `403`.
2. **Where the request comes from.** The same `403` and `415` as an ack.
3. **The permission.** With access control on, a caller holding `actions.write` nowhere gets
   `403 {"detail":"You may not act on findings."}` before the body is read. A body over
   8 KiB is a `413`.
4. **The body.** One `400` per rule above, the signature held to the bounds of an ack's.
   `take` in a `PATCH` is a `400`.
5. **The finding.** The Hub must hold it, and with access control on the caller must read a
   copy of it, otherwise `404 {"detail":"The Hub holds no such finding."}`, the very answer
   a finding the caller may not read gets. A readable one the caller may not write is a
   `403`, see [With access control](#with-access-control).
6. **The write.** A `POST` on a signature that has an action is a `409`, and a `PATCH` on one
   that has none a `404`. A write the store could not take in time is a `503` with
   `Retry-After: 1`.

A `POST` answers `201` and a `PATCH` `200` with the action as it now stands:

```json
{"signature": "n_plus_one_sql:order-svc:…", "status": "done", "owner": "ada@example.com",
 "owner_display": "Ada Lovelace", "created_by": "ada@example.com", "created_at_ms": 1760000000000,
 "updated_at_ms": 1760003600000, "done_at_ms": 1760003600000}
```

`owner_display` is the name the identity provider gave the caller who took the finding, or
`null`: a proxy-header or token caller has none, and an `owner` set from the body is typed by
hand, so it clears it. A status change or the same owner keeps it. Events carry it too. It is
shown, never judged. `done_at_ms` is stamped when the action enters `done` and cleared when it leaves it. Every
change of the status or the owner appends an event, with the status it left, the one it
reached, the owner, the caller and the time. The events are never rewritten, and neither the
action nor its events are purged with the finding: an action outlives the retention of the
finding it was declared on, so a recap can still tell whether the fix held. An action that
enters `done` also keeps the finding as it stood then, from the newest copy the caller reads,
for the share card. It is never served, survives the finding's purge and is dropped when the
action leaves `done`. A `PATCH` that
changes nothing appends nothing. Every refusal the Hub words itself carries
`{"detail":"…"}`.

## Endpoint labels

A label is what a person calls an endpoint in business words (`Showing a cart` for
`checkout-api` `GET /api/cart/{id}`). It belongs to one `(service, endpoint)` pair for the
whole Hub, whichever source holds the findings, and the recap shows it as the title of a row.
A service name does the same for a whole service (`Payment` for `checkout-api`), once for every
step of it, and the recap shows it wherever a row names its service. Two routes write them,
each with a JSON body, and neither is open: under `Hub:Auth` both need a session.

| Endpoint                   | Body                                                      | Does                         |
|----------------------------|-----------------------------------------------------------|------------------------------|
| `PUT /api/labels`          | `{"service":"…","endpoint":"…","label":"Showing a cart"}` | Sets, replaces or removes it |
| `PUT /api/labels/services` | `{"service":"…","label":"Payment"}`                       | Sets, replaces or removes it |

- `service` and `endpoint` are the finding's own, 1 to 1024 characters with no control
  character, as they read in `/api/findings` (`endpoint` is its `source_endpoint`).
- `label` is trimmed, then 1 to 80 characters with no control character and no format
  character (a direction override such as U+202E, a zero-width one). `null`, or a label with
  nothing left once trimmed, removes it. A body without a `label` is a
  `400 {"detail":"A label needs a label value; send null to remove it."}`.
- `PUT /api/labels/services` names a whole service, once for every step of it: the recap's
  Management rows say `in Payment` rather than `in checkout-api`, and an unnamed step reads
  `An unnamed step in Payment`. It takes `service` and `label` under the same rules, and is
  judged on every copy of every finding of that service: `404 {"detail":"The Hub holds no
  finding of this service."}` when the caller reads none.
- The Hub judges the request like an action: the caller first (nobody to name is a `403`),
  then where it comes from (`403`, `415`), the permission, the body (`400`, or `413` over 8 KiB),
  the endpoint (`404` when the Hub holds no finding there that the caller reads, `403` when it
  reads one it may not name), then the write (`503` with `Retry-After: 1` when the store could
  not take it in time). A request it refuses writes nothing.

It answers `204` with no body.

## Analysis API

An analysis is a run of the perf-sentinel binary against one configured source, producing
the self-contained HTML dashboard the engine renders.

| Endpoint                 | Does                                                                          |
|--------------------------|-------------------------------------------------------------------------------|
| `POST /api/analyses`     | Takes `{"source_id": "...", "request": {...}}`, answers `202` with the run id |
| `GET /api/analyses`      | Lists recent runs, newest first                                               |
| `GET /api/analyses/{id}` | Returns one run                                                               |
| `GET /reports/{id}.html` | Serves a succeeded run's report, from the same origin as the rest             |

The request shape follows the source's kind: `{}` for a daemon, which takes no parameters
at all, `{service, lookback | from_ms + to_ms, max_traces}` for a trace backend, or
`{trace_id}`. The engine's own exclusions are enforced before anything is queued, so an
impossible pair is refused rather than discovered as a failed run three minutes later.

### Two invocations, not one

The query subcommands emit text, JSON or SARIF, and only `report` writes HTML. So the
source is read into a report JSON, and that JSON is then rendered. A daemon source skips
the first step, since its own `/api/export/report` already returns one.

Both invocations run from `Hub:Analysis:ReportDirectory`. The engine looks for
`.perf-sentinel.toml` relative to its own working directory, so leaving this unset would
let a stray file beside whatever directory launched the Hub decide detection thresholds
for every run.

### Detection overrides

A request may carry a `detection` object overriding the engine's thresholds:
`n_plus_one_min_occurrences`, `window_duration_ms`, `slow_query_threshold_ms`,
`slow_query_min_occurrences`, `max_fanout`, `chatty_service_min_calls`,
`pool_saturation_concurrent_threshold`, `serialized_min_sequential`,
`sanitizer_aware_classification` (one of `auto`, `strict`, `always`, `never`) and, from
engine 0.18.0, `sanitizer_aware_min_cv` (a decimal from `0.01` to `10`).

Bounds mirror the engine's own validator, and `GET /api/status` publishes them with each
default under `detection_knobs`. Each entry names its `kind`: `integer` and `decimal`
carry `min`, `max` and a numeric `default`, `choice` carries its `choices` and a string
`default`. A knob the probed engine's `[detection]` does not read is withheld from the list
and refused on submission with a 400 that names both versions, rather than written to the
run config and refused by the engine at run time, so `sanitizer_aware_min_cv` appears only
once the embedded binary is 0.18.0 or later. A value equal to the default is dropped rather than
recorded, so a run carries only what departs from the standard configuration. The
overrides are written to a per-run TOML handed to both invocations through `-c`, and
deleted when the run ends.

These thresholds decide what counts as a problem, not how the report is written. Raising
one does not make a run lighter, it stops the detector from reporting the smaller cases.
That is why counts from runs with different thresholds are not comparable, and why the
launcher says so. A daemon source takes none: it detects with its own configuration, and
the Hub only reads what it already found.

### Report size

Not a knob. The sink's 5 MiB target is a private constant with no flag, no environment
variable and no config key. A report built from a backend query tops out around 4 MB,
because the share of that budget reserved for embedded span trees is never spent: a
backend query returns findings, not spans. When the sink does drop findings to fit, the
run records how many survived, read back from the rendered file, and the result panel says
so above the link.

### Failure and expiry

Every failure is one of nine codes: `source_unreachable`, `source_auth_failed`,
`source_rejected_request`, `timeout`, `output_too_large`, `binary_failed`,
`invalid_request`, `hub_memory_low`, `internal`.

`hub_memory_low` is the Hub's own refusal. The engine runs inside the Hub's container, so
before each of its two runs the Hub reads what its cgroup can still give, the reclaimable
page cache counted as free, and fails the run when that is under
`Hub:Analysis:MinFreeMemoryMiB`. An engine that does not fit would get the whole Hub
OOM-killed, every request in flight with it. A container without a memory limit, or
outside cgroup v2, is not checked.

Raw stderr never leaves the process. It is read to name an owner, since "the backend
refused us" and "the binary broke" have different owners, and that classification is a
heuristic on a bounded set of markers rather than a contract.

Reports are deleted `Hub:Analysis:ReportRetention` after they succeed, and the run is
marked expired while keeping its parameters. The row itself then survives until
`Hub:Analysis:RunRetention`, thirty days by default, after which it is deleted and
`GET /api/analyses/{id}` answers `404`. This is not an audit trail, and a link shared
yesterday is already dead. A run still pending or running is never removed, however old
its row looks.

A run still running when the service stops comes back `interrupted` and is never replayed
on its own. A silent retry would fire a second heavy query at a backend nobody asked to
query twice.

## Disclosures

A periodic disclosure is the report `perf-sentinel disclose` builds for one period from the
production daemons' archives, signed or not. A scheduled job publishes it to the Hub, in
practice a CI workflow, since keyless signing needs the OIDC identity the CI gives it. The Hub
checks and renders it with its embedded engine's `disclose-pdf`, keeps the files exactly as they
arrived, serves them to the holders of `disclosures.read` and shares them through expiring
links. Every route below answers `404` while `Hub:Disclosures:ImportKey` is unset, see
[CONFIGURATION.md](CONFIGURATION.md#disclosures).

### Publishing

`POST /api/import/disclosures` takes `X-API-Key` with `Hub:Disclosures:ImportKey`, never a
session, and a `multipart/form-data` body of at most 8 MiB. Its parts are `report`, required,
then `attestation` and `bundle`, each sent once and never empty. Any other part refuses the
whole body. One publication runs at a time, and the request has 60 seconds, the engine 45 of
them.

```sh
curl --fail -H "X-API-Key: $HUB_DISCLOSURES_KEY" \
  -F report=@report.json \
  -F attestation=@attestation.intoto.jsonl \
  -F bundle=@bundle.sig \
  https://hub.example.com/api/import/disclosures
```

The Hub reads the report's identity, period and confidentiality, then runs the engine on the
three files. Nothing is stored before the engine answers, and a refusal stores nothing.

| Answer                      | When                                                                                                   |
|-----------------------------|--------------------------------------------------------------------------------------------------------|
| `201`                       | A new disclosure, stored                                                                               |
| `200`                       | The same bytes again, or a signed copy of a report stored unsigned, which replaces it. No second event |
| `401`                       | A missing or wrong key                                                                                 |
| `404`                       | Disclosures are off                                                                                    |
| `408`                       | Past 60 seconds                                                                                        |
| `409 {"reason":"conflict"}` | Another report under a `report_uuid` already published. A new report takes a new uuid                 |
| `413`                       | A body over 8 MiB                                                                                      |
| `415`                       | Not `multipart/form-data` with a boundary                                                              |
| `422 {"reason":"…"}`        | Refused, see below                                                                                     |
| `503`                       | Another publication is running, with `Retry-After: 1`                                                  |

A signed copy keeps each signature file already stored, byte for byte, and adds one it lacks:
the attestation `disclose --emit-attestation` writes with the report, then the bundle cosign
signs. It replaces the stored report, its `integrity_level` included, which the hash leaves
out. A copy that changes or drops a stored file is a `409`, and so is one whose organisation,
period, intent, confidentiality or figures differ from the stored ones, rendered or not.

The `422` reasons are `content_hash_mismatch`, the hash does not recompute, `binding_mismatch`,
the attestation names another report, `malformed_report`, a body, a part or a report the Hub or
the engine cannot read, a confidentiality other than `public` or `internal` among them, or a
period date that is not `YYYY-MM-DD`, `file_too_large`, `missing_file`, `internal_refused`, an
internal report while `Hub:Disclosures:AllowInternal` is off, and `engine_contract`, an engine
answer outside its contract.

A taken publication answers
`{"id":"…","pdf":"rendered","reason":null,"content_hash_checked":true,"binding_checked":true}`.
`binding_checked` is null without an attestation. An engine that cannot draw the report (its
exit 5, a timeout) or that has no `disclose-pdf` stores it all the same, with `pdf` reading
`not_checked`, both checks false or null, and `reason` naming why: the engine's reason, or
`unsupported_engine`. The render route below tries again once the engine can.

### Reading

| Endpoint                                 | Returns                                                                 |
|------------------------------------------|-------------------------------------------------------------------------|
| `GET /api/disclosures`                   | Every disclosure, newest period first, withdrawn ones marked. No file   |
| `GET /api/disclosures/{id}`              | One disclosure with its checks, its share links and its deliveries      |
| `GET /api/disclosures/{id}/files/{name}` | One of its files, as an attachment, with `X-Content-Type-Options: nosniff` |

They need `disclosures.read`, otherwise `403 {"detail":"You may not read disclosures."}`.

A listed disclosure carries `id`, `report_uuid`, `organisation`, `period` (`type`, `from`,
`to`), `intent`, `confidentiality`, `content_hash`, `integrity_level`, `figures`, the few
totals a card shows, read from the report, `files` (`report`, `attestation`, `bundle` and `pdf`
as booleans), `pdf` (`rendered` or `not_checked`), `render_reason`, `published_at_ms`,
`signature_added_at_ms` and `withdrawn` (`at_ms`, `reason`, `by`, or null).

The detail adds `checks`, which says who checked what. `content_hash` is `checked` when the
engine recomputed it here, `not_checked` otherwise. `binding` is `checked`, `not_checked` or
`no_attestation`. `signature` is only ever `declared` or `absent`: what the report's
`integrity_level` says of itself, since the Hub never checks a signature. `engine_version` is
the engine that rendered it. `share_links` lists each link without its token: `id`, `state`
(`active`, `expired`, `revoked`, or `withdrawn` once its disclosure is), `created_by`,
`created_at_ms`, `expires_at_ms`, `revoked_at_ms`, `revoked_by`, `downloads` and
`last_used_at_ms`. `deliveries` lists each
webhook event by the webhook's name, never its URL: `id`, `webhook`, `event`, `state`
(`pending`, `delivered` or `failed`), `attempts`, `last_attempt_ms`, `next_attempt_ms` and
`last_status`. `expected_identity` and `expected_issuer` are the configured signer, for the
verify command. `can_manage` says whether this caller may take the actions below: it holds
`disclosures.manage` and the Hub can name it.

The file names are `report.json`, `attestation.intoto.jsonl`, `bundle.sig`, `report.pdf` and
`SHA256SUMS`, computed on each request in the format `sha256sum -c` reads. Any other name, or
a file the disclosure lacks, is a `404`.

### Sharing and withdrawing

These routes need `disclosures.manage` and somebody to name, as the
[ack relay](#ack-relay) does: a `Hub:Auth` session, or the identity header once
`Hub:AckRelay:TrustIdentityHeader` is set. They hold to its same-origin rule as well: a
`Sec-Fetch-Site` that is present and not `same-origin` is a `403`, and a content type that is
not JSON is a `415`, a `DELETE` included.

| Endpoint                                                   | Body                 | Answers                                                          |
|------------------------------------------------------------|----------------------|------------------------------------------------------------------|
| `POST /api/disclosures/{id}/share-links`                   | `{"days":n}` or `{}` | `201` with the token, once                                       |
| `DELETE /api/disclosures/{id}/share-links/{linkId}`        | none                 | `204`, or `404` for an unknown or already revoked link           |
| `POST /api/disclosures/{id}/withdraw`                      | `{"reason":"…"}`     | `204`, or `409` when already withdrawn                           |
| `POST /api/disclosures/{id}/deliveries/{deliveryId}/retry` | `{}`                 | `204`, or `409` for a delivery that went through                 |
| `POST /api/disclosures/{id}/render`                        | `{}`                 | `200` with the publication answer, `422` with a reason, or `503` |

A share link lasts `days`, 1 to 30, `Hub:Disclosures:ShareLinkDays` when the body leaves it
out. The answer, `{"id":"…","token":"pshare_…","path":"/share/pshare_…/","expires_at_ms":…}`, is
the only place the token appears: the Hub stores its SHA-256. Only a public disclosure is
shared (`422 {"reason":"internal_not_shareable"}` otherwise), and a withdrawn one never (`409`).

A withdrawal is final. Its `reason`, 1 to 500 characters with no control character, is kept
with the caller's name. The files stay and stay readable, every share link stops answering,
and a `withdrawn` event goes to each webhook the `published` event was addressed to. A wrong
disclosure is replaced by a new one under a new uuid.

A retry puts a delivery that waits or gave up back in the queue, due at the next pass. It
starts a fresh run of attempts, so a delivery that had given up gets the whole backoff again.
A render runs the engine again on the stored files, for a disclosure stored before it could
render. It waits for no publication: one running answers `503` with `Retry-After: 1`. A
refusal stays on the disclosure, `pdf` reading `not_checked` and `render_reason` naming it, so
a report the engine finds altered says so.

### Share links

`GET /share/{token}/{name}` serves the five names above under a live token, anonymously: not
revoked, not expired, and of a disclosure not withdrawn. Everything else under `/share/`,
whatever the reason, gets one identical `404` with no body. Every answer carries
`Referrer-Policy: no-referrer`, `Cache-Control: no-store`, `X-Content-Type-Options: nosniff` and
`X-Robots-Tag: noindex`. The route stands outside sign-in and access control, and the token is
the whole credential. Four downloads run at once at most, each holding its file in memory: a
fifth answers `503` with `Retry-After: 1`. With disclosures off, the route is not there at all.

A link counts its downloads in memory and writes them once a minute and at shutdown, so a
crash loses a minute of counts at most. The client address is recorded with the count only
when `Hub:Auth:TrustedProxyNetworks` is set, since the Hub cannot otherwise tell a client from
its proxy, and no route serves it.

The engine verifies a shared disclosure from its link, fetching the attestation and the bundle
beside it:

```sh
perf-sentinel verify-hash --url https://hub.example.com/share/pshare_…/report.json \
  --expected-identity release@example.com --expected-issuer https://accounts.google.com
```

### Webhooks

Each webhook in `Hub:Disclosures:Webhooks` receives a `POST` with a JSON body when a public
disclosure is published, and when it is withdrawn if the publication was addressed to it. An
internal disclosure is never sent.

```json
{"event":"published","disclosure_id":"…","organisation":"…",
 "period":{"type":"calendar-quarter","from":"2026-07-01","to":"2026-09-30"},
 "content_hash":"…","intent":"official","integrity_level":"signed-with-attestation",
 "share_url":null}
```

`X-PerfSentinel-Timestamp` carries the Unix time in seconds, and `X-PerfSentinel-Signature`
`sha256=` followed by the lowercase hex HMAC-SHA256 of `<timestamp>.<body>` under the
webhook's `Secret`. A receiver recomputes it over the raw body, compares in constant time, and
refuses a timestamp older than it accepts. With `IncludeShareLink`, a `published` event carries
in `share_url` a link of its own, issued for that attempt for the webhook's `ShareLinkDays`,
named `webhook:<name>` in `created_by`, to `report.pdf` when rendered and `report.json`
otherwise. An attempt that fails revokes its link.

Deliveries run every 15 seconds from the `disclosure_deliveries` table, so a restart resumes
them. A `2xx` settles one. Any other status, a redirect included, which is never followed, or
no answer within `Hub:HttpTimeout` is tried again after 30 seconds, then a doubling wait capped
at one hour, 8 attempts in all, after which the delivery is `failed`. A delivery to a webhook
no longer configured fails without a request, and so does a `published` event still waiting
when its disclosure is withdrawn: the receiver never hears of it after the `withdrawn` one. A
retry taken during an attempt wins over that attempt's outcome.

## With access control

With `Hub:Access:Enabled` every route but the imports, `/share/`, `/health`, `/metrics` (anonymous,
on `Hub:MetricsPort` only) and the CLI's `POST /auth/device`, `POST /auth/device/token`
and `POST /auth/revoke` ([Terminal sign-in](#terminal-sign-in)), and the sign-in page
routes ([Local sign-in](#local-sign-in)), needs a session, a terminal
token or a machine token, and each answer covers what the caller's grants cover. The model is in
[ACCESS-CONTROL.md](ACCESS-CONTROL.md); what changes on the wire:

- **Credentials.** A browser session, or `Authorization: Bearer` with a terminal token
  (`pshub_…`, [Terminal sign-in](#terminal-sign-in)) or a machine token. A request
  carrying a bearer is judged on it alone. An unknown or expired token gets `401`, even
  next to a valid cookie, with
  `WWW-Authenticate: Bearer error="invalid_token"`. A machine token only reads: any other
  method gets `403 {"detail":"A machine token only reads."}`. A browser write without
  `Sec-Fetch-Site: same-origin` gets `403 {"detail":"A request from another site is refused."}`.
- **`/api/status` and `/api/sources`** answer `403 {"detail":"No grant on this Hub reaches
  you."}` to a caller holding no permission anywhere. `/api/sources` lists only the sources
  where the caller holds something, each with `permissions`: `findings.read`,
  `sources.read`, `analyses.run` and `acks.write` for a daemon, `traces.read` and
  `analyses.run` for a trace backend. The field is absent with access off.
- **Both findings routes** are filtered by `findings.read`: a finding is listed when one
  of its copies is in scope, and its `sources[]`, `acks[]`, status, first and last seen,
  and the `include_acked`, `grouping` and window filters are judged on the in-scope copies
  alone. Pages are slices of that one list. A `source_id` or an `environment` outside the
  caller's `/api/sources` answers the same `400` as an unknown one. A caller holding
  `findings.read` nowhere gets `403` rather than `[]`, so a CI gate whose token has no
  grant fails.
- **Incidents** follow `findings.read` the same way; `/api/incidents/{id}` serves the
  richest copy in scope, and `404` when none is. A refresh reads only the daemons where
  the caller reads something.
- **The daemon view** needs `sources.read` at that source as a whole, and answers `404`
  like an unknown source otherwise, before the trace-backend `400`.
- **Acks**, in the order above: the source must be in the caller's `/api/sources`, or the
  same `404`; then, before a relay slot is taken or the body read, the caller must hold
  `acks.write` somewhere at that source, or `403 {"detail":"You may not acknowledge
  findings at this source."}`; then, on the finding held at that source, a copy the caller
  cannot read answers the very `404` an unknown one does, and one it reads but may not
  write `403`. A revoke of a mirrored ack whose finding the Hub no longer holds needs the
  source as a whole. `expires_at` stays the caller's choice: no expiry is forced.
- **The recap** follows `findings.read` the same way: it is built from the copies the
  caller reads, as `/api/findings` is, and a caller holding `findings.read` nowhere gets the
  same `403`. A `source_id` or an `environment` outside the caller's `/api/sources` is the
  `400` of an unknown one.
- **Recap reports** are read through `recaps.read` (Hub level, no machine token holds it) or, for
  a report a caller generated on demand, through `findings.read` and the identity that generated
  it. A subscription report needs `recaps.read`. A report the caller may not read answers the `404`
  an unknown one gets. Generating needs a caller the Hub can name and `findings.read`, and the
  report is built from the copies that caller reads.
- **Actions** need a caller the Hub can name, then `actions.write` somewhere, or
  `403 {"detail":"You may not act on findings."}`, before the body is read. An action is
  global to the signature, so it is judged on the copies of the finding the caller reads, at
  any source: none answers the `404` an unknown finding gets, and when none of them is
  writable under `actions.write` the answer is
  `403 {"detail":"You can read this finding but not act on it."}`. A machine token holds
  neither permission.
- **Labels** follow the same order as actions: a caller the Hub can name, then `labels.write`
  somewhere, or `403 {"detail":"You may not name endpoints."}`, before the body is read. A label
  is global to the `(service, endpoint)` pair, so it is judged on every copy of every finding
  there that the caller reads: none answers `404 {"detail":"The Hub holds no finding at this
  endpoint."}`, the answer an unknown endpoint gets, and when none of them is writable under
  `labels.write` the answer is `403 {"detail":"You can read this endpoint but not name it."}`. A
  service name follows the same order on the copies of every finding of the service:
  `403 {"detail":"You may not name services."}`, then `404 {"detail":"The Hub holds no finding
  of this service."}` or `403 {"detail":"You can read this service but not name it."}`. A
  machine token holds neither permission.
- **Analyses.** One rule decides who sees a run's row and opens its report: a daemon run
  needs `findings.read` and `sources.read` at the daemon as a whole; a trace-backend run
  needs `traces.read` on the requested service, or at the backend as a whole for a trace
  ID. Starting a run needs that and `analyses.run`, otherwise
  `403 {"detail":"You may not start this analysis."}`; a source outside the caller's list
  is the `400` of an unknown `source_id`. `GET /api/analyses` lists only visible runs, never
  `403`; a hidden run and its report answer `404`. The run JSON is unchanged. **A trace
  report shows every service its traces cross**, `result` counts included. A daemon
  report goes live through the ack relay (`--hub-relay`) when the source has an ack
  credential, under the checks above. The others render static: no `--daemon-url`.
- **Disclosures** need `disclosures.read` or `disclosures.manage` on `*`, which no machine
  token holds. A caller without it gets `403`, never a filtered list, since a disclosure
  belongs to no source. The import keeps its own key and `/share/` its token, both outside
  access control.

From CI:

```sh
curl -fsS -H "Authorization: Bearer $HUB_TOKEN" \
  "https://hub.example.com/api/findings?environment=production&status=active&severity=critical"
```

### `GET /api/access`

Mapped only with `Hub:Access:Enabled`: with access off it answers `404`, and the launcher
then keeps its identity chip plain text and hides the Coverage tab. Any caller reads it, including one no grant reaches,
since it is how that caller finds the id an administrator asks for. It never writes and
never lists a machine token's value or fingerprint.

```json
{
  "identity": "ada@example.com",
  "subject": "2f6d0c1e-8a4b-4e2f-9c71-0d3b5a6e7f81",
  "caller": "session",
  "expires_at_ms": 1760040000000,
  "admin": false,
  "groups": [{"name": "/teams/shop", "origin": "provider"}, {"name": "oncall", "origin": "internal"}],
  "reach": [{"group": "/teams/shop", "project": "shop", "role": "member",
             "permissions": ["findings.read", "traces.read", "analyses.run", "acks.write",
                              "actions.write"]}],
  "projects": [{"name": "shop", "patterns": [{"source": null, "service": null, "attribute": "team=shop"}]}],
  "configuration": null,
  "unmatched": null,
  "conflicts": null
}
```

- `identity` is the display name `/api/status` returns; `subject` is the authorization key
  that `Admins` and internal-group `Members` list. `caller` is `session`,
  `terminal_token` or `machine_token`. `expires_at_ms` is the session's end, the terminal
  token's (the approving session's end), or the machine token's configured expiry. A
  terminal token is never `admin`.
- `groups` are the caller's effective groups: the provider groups the configuration names,
  kept at sign-in, and the internal groups listing the subject, named without `internal:`.
- `reach` holds the caller's grants with what each gives them: a machine token keeps
  `findings.read`, and `access.admin` appears only in an admin's session. An `Admins` or
  `AdminGroups` entry reads as the `admin` role on `*`, with `group` null for `Admins`.
  `projects` are the projects `reach` names.
- **For an admin only**, the three last fields are filled:
  - `configuration`: `projects`, `roles` (`built_in` marks `reader`, `member` and
    `admin`), `grants`, `internal_groups` with their `members`, `admins`, `admin_groups`,
    `machine_tokens` as `{name, group, expires_at_ms, active}`, `active` meaning not
    expired, and `local_accounts`, the declared local accounts by name, never their hash.
  - `unmatched`: the finding copies seen within `Hub:Retention` that no pattern of any
    project reads, as `services` (`{service, source_ids, findings}`), `attributes`
    (`{key, value, findings}`) and `sources`, every configured source no pattern names in
    its source part (`{source_id, findings}`, `findings` null for a trace backend).
    `findings` counts copies, one per signature and source. Each list is ordered by count,
    then name, and stops at 200 entries; `truncated` is true when one was cut.
  - `conflicts`: `{copies, truncated}`, the copies on which a key some project pattern uses
    carries two values, or is missing from one of the groupings the copy was seen under,
    as `{signature, source_id, service, key, values, identities_without_key}`, ordered by
    source then signature, at most 200. These are the copies where an attribute grant reads
    but cannot acknowledge, see [ACCESS-CONTROL.md](ACCESS-CONTROL.md#reading-conflicts).

### Terminal sign-in

Mapped only with `Hub:Access:Enabled`. The device-flow routes, the first five below, are
mapped only when `Hub:Auth:TrustedProxyNetworks` is set as well; otherwise they answer as
any unmapped route does (`302` to the provider, `401` under `/api`), and a client reads a
`3xx`, `401` or `404` on `POST /auth/device` as "no terminal sign-in here". See
[AUTHENTICATION.md](AUTHENTICATION.md#terminal-sign-in) for the flow.

| Route | Who | Answers |
|---|---|---|
| `POST /auth/device` | anonymous (the CLI) | `200` below; `400` `invalid_request` or `invalid_client`; `403` with `Sec-Fetch-Site`; `413` over 4 KiB; `429` `temporarily_unavailable` with `Retry-After: 60` |
| `POST /auth/device/token` | anonymous (the CLI) | `200` token; `400` with `authorization_pending`, `slow_down`, `access_denied`, `expired_token`, `invalid_grant`, `unsupported_grant_type`, `invalid_request` or `invalid_client`; `403`; `413` |
| `GET /device` | browser session | the approval page, or the sign-in when no session or less than 1 h is left |
| `POST /api/device/verify` | browser session only | `200` below; `400` malformed code; `401`; `403`; `404` no such pending code; `415`; `429` |
| `POST /api/device/approve` | browser session only | `204`; `400`; `401`; `403`; `404`; `409` the session ends within the hour; `415`; `429` |
| `POST /auth/revoke` | anonymous, by possession | RFC 7009: `200` whether or not the token existed; `400 invalid_request`; `403`; `413`; `503` with `Retry-After: 1` |
| `GET /api/tokens` | browser session only | `200` list; `401` |
| `DELETE /api/tokens/{id}` | browser session only | `204`; `401`; `403`; `404`; `415`; `503` with `Retry-After: 1` |

**The CLI routes** (`/auth/device`, `/auth/device/token`, `/auth/revoke`) take an
`application/x-www-form-urlencoded` body with each parameter once, answer
`Cache-Control: no-store`, and give their errors as `{"error":"<code>"}` (RFC 6749 §5.2). A
request carrying `Sec-Fetch-Site` comes from a browser and gets `403`. `client_id` must be
`perf-sentinel`.

```sh
curl -fsS -d client_id=perf-sentinel https://hub.example.com/auth/device
```

```json
{"device_code": "x4Kd…", "user_code": "BCDF-GHJK", "verification_uri": "https://hub.example.com/device",
 "expires_in": 600, "interval": 5}
```

There is no `verification_uri_complete`: the code is always typed on the page. Poll with
`grant_type=urn:ietf:params:oauth:grant-type:device_code`, `device_code` and `client_id`,
no faster than `interval` seconds (each early poll answers `slow_down` and adds 5). Once
approved:

```json
{"access_token": "pshub_…", "token_type": "Bearer", "expires_in": 28512}
```

`expires_in` runs to the end of the approving session; there is no refresh token. Revoke
with `token=<token>` on `/auth/revoke`; `token_type_hint` is ignored.

**The browser routes** read the session cookie alone: a terminal token, even a valid one,
gets `401 {"detail":"Approving a sign-in or managing tokens needs a browser session."}`.
A machine token gets that `401` on `GET /api/tokens` and the `403` every machine-token
write gets on the three others (`{"detail":"A machine token only reads."}`). Their
writes need `Content-Type: application/json` (else `415`) and `Sec-Fetch-Site: same-origin`
(else `403`), the DELETE included. `verify` takes `{"user_code":"BCDF-GHJK"}` (case,
`-` and spaces ignored) and answers:

```json
{"user_code": "BCDF-GHJK", "client_address": "203.0.113.7", "same_network": false,
 "requested_at_ms": 1760040000000, "identity": "ada@example.com", "token_expires_at_ms": 1760068800000}
```

`client_address` is the requesting client's address, an IPv6 one cut to its /64;
`same_network` compares it with the address of this request. `approve` takes
`{"user_code":"…","approve":true}` or `false` to deny; a code is decided once. Ten
malformed, unknown or expired codes in 10 minutes answer `429` with `Retry-After` until
the window ends.

`GET /api/tokens` lists the caller's live terminal tokens, newest first, or every live one
for an admin (at most `Hub:MaxReadLimit`); never the token nor its hash:

```json
[{"id": "0f1e2d3c4b5a69788796a5b4c3d2e1f0", "identity": "ada@example.com", "subject": "2f6d0c1e-…",
  "client_address": "203.0.113.7", "created_at_ms": 1760040000000, "expires_at_ms": 1760068800000,
  "last_used_at_ms": 1760043600000}]
```

`last_used_at_ms` moves at most once every 5 minutes. `DELETE /api/tokens/{id}` revokes
the caller's own token, or anyone's for an admin; another user's token answers the `404`
of a missing one.

### Local sign-in

Mapped only with `Hub:Access:Enabled` and at least one entry in `Hub:Access:LocalAccounts`;
otherwise these answer as any unmapped route does. Every answer carries
`Cache-Control: no-store`, `X-Content-Type-Options: nosniff`,
`Referrer-Policy: same-origin` and
`Content-Security-Policy: default-src 'none'; font-src data:; style-src 'sha256-<hash of the page's inline stylesheet>'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'`.
See [AUTHENTICATION.md](AUTHENTICATION.md#local-accounts) for the model.

| Route | Who | Answers |
|---|---|---|
| `GET /auth/sign-in?return_url=` | anonymous | `200` and the page, signed in or not |
| `GET /auth/sign-in/sso?return_url=` | anonymous | `302` to the provider, back to `return_url` after sign-in |
| `POST /auth/sign-in/local` | anonymous, from the page | `302` to `return_url` with the session cookie; `403` and the page on a wrong name or password; `403` plain text from another site; `413`; `415`; `429` or `503` and the page, with `Retry-After` |

`return_url` is a path on the Hub; anything else, `//host`, `/\host` or an absolute URL,
becomes `/`. With a local account declared, a page request without a session is
redirected to `/auth/sign-in?return_url=<path and query>` instead of the provider; `/api`,
and a report inside the launcher's frame, still answer `401`.

The POST takes `application/x-www-form-urlencoded` (else `415`) of at most 4 KiB with a
`Content-Length` (else `413`): `name`, `password` and `return_url`. A `Sec-Fetch-Site`
other than `same-origin`, or an `Origin` other than the Hub's own, gets `403`. The name is
compared in lower case. A wrong password, an unknown name and an empty field answer the
same `403` page, byte for byte. A client address and name past five consecutive failures,
or an address past 5 attempts in its minute, get `429` and `Retry-After` in seconds; a
request that waits more than 10 seconds for the hashing slot, or finds 8 already waiting,
gets `503` and `Retry-After: 1`.
