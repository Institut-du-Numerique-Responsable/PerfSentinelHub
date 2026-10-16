# Operations

## Freshness and recovery

Push is primary. Each source is also polled independently, as a safety net.

A successful poll updates observations. It does not delete a finding merely because a
later daemon response omits it: the daemon's ring buffer may have evicted it, so missing
does **not** mean resolved. Only retention removes findings, when their last observation
is older than the configured period.

The perf-sentinel 0.11.x daemon caps `/api/findings` at 1,000 rows. The Hub uses that
exact cap and warns whenever it is reached, because the safety-net snapshot may be
incomplete. High-volume coverage therefore requires the bounded push exporter.

Failures keep previously stored findings readable. The affected source is marked
unreachable and retried with bounded exponential backoff, and a later success clears that
state. Poll bodies are limited to 16 MiB, requests have a timeout, imports are
transactional, and logs identify only the source ID and a stable error code.

The same poll copies the daemon's incidents, from 0.20.0 with `[daemon.incidents]`
enabled, and the copy survives the daemon's own ring. What that read came to is
`incidents_state` on `/api/sources`, kept apart from reachability: a daemon answering 404
or 503 on that route publishes no incidents, one refusing the key is `unauthorized`, and
neither marks the source unreachable, since its findings were just collected. Incidents
are read on the same hourly poll as the findings. A daemon on perf-sentinel 0.29.0 or
later with `[daemon.hub_export]` on also pushes them as they are recorded, closed or
completed, so an incident its ring evicts between two polls still arrives. Without that
push, one captured minutes before a daemon restart can be lost between two polls, which
the daemon's own NDJSON archive covers.

That poll is the floor for incidents, not the only path. `POST /api/incidents/refresh`
reads every daemon on demand, and the launcher's incidents screen calls it on every open,
so an operator paged about an OOM kill sees what the fleet holds now instead of waiting out
the interval. The daemons are protected from that by a ten-second floor per source, below
which the stored copy is served untouched, and by a gate of two concurrent refreshes.
`incidents_read_ms` on `/api/sources` says when each copy was taken, which is what tells a
fleet with nothing to report from a copy nobody has refreshed.

The last read of the poll copies the daemon's acknowledgments, its CI baseline included,
from `GET /api/acks?include_toml=true` with the read credential. It comes after the incident
read, on findings already stored, and the version the status read returned decides whether
it happens at all: a daemon below 0.24.0 ignores `include_toml` and lists its runtime acks
alone, an answer nothing can tell from a daemon that holds no baseline, so it is never asked
and the read is filed `absent`. A pre-release of that floor is asked, its suffix dropped,
and a version that does not parse counts as an older daemon. That poll is the floor for the
mirror, not the only path: the relay re-reads the listing as soon as a daemon takes an ack
or a revoke, which can leave the mirror far fresher than the interval, see
[API.md](API.md#ack-relay).

`acks_state` on `/api/sources` says what that read came to and `acks_read_ms` when it was
taken. `ok` is a whole listing, mirrored as it stands, less any row the Hub refuses, which
is dropped and counted in the logs without changing the state. `truncated` is one that
reached the daemon's own cap of a thousand acks, which it does not page, so its tail may be
missing: the rows read are still mirrored and served, but a truncated listing is never
trusted to say a finding is un-acknowledged, see
[API.md](API.md#how-include_acked-judges-a-finding). `absent` also covers a route the daemon
does not serve, `unauthorized` a daemon refusing the read key, and `error` any other
failure, its stable code in the logs. A daemon whose acknowledgments are disabled is not
`absent`: it answers an empty listing, filed `ok`. No outcome of that read marks the source
unreachable or fails the poll, whose findings were collected before it ran, and a failed
read leaves the previous mirror behind with nothing saying it still holds. On the launcher's
ack page, a relaying source whose state is neither `ok` nor `truncated` has it named on its
row and keeps both buttons, while a source the Hub holds no ack credential for offers
neither, see [LAUNCHER.md](LAUNCHER.md#the-ack-page).

## Metrics

`GET /metrics` serves the Prometheus text format. It is written by hand rather
than through a library: thirteen metric families over data the Hub already holds do
not justify a dependency in a service whose only two packages are SQLite.

| Metric                                                                 | Type    | What it answers                                       |
|------------------------------------------------------------------------|---------|-------------------------------------------------------|
| `perf_sentinel_hub_build_info{version}`                                | gauge   | Which version is running                              |
| `perf_sentinel_hub_source_reachable{source}`                           | gauge   | Whether the last poll of a daemon succeeded           |
| `perf_sentinel_hub_source_unreachable_seconds`                         | gauge   | How long it has been unreachable, 0 when it answers   |
| `perf_sentinel_hub_source_last_success_seconds`                        | gauge   | Age of the last successful poll                       |
| `perf_sentinel_hub_source_last_import_seconds`                         | gauge   | When a daemon last pushed. Not a heartbeat, see below |
| `perf_sentinel_hub_import_rejected_total{reason}`                      | counter | Imports refused, by reason                            |
| `perf_sentinel_hub_incident_import_rejected_total{reason}`             | counter | Incident imports refused, by the findings reasons     |
| `perf_sentinel_hub_correlation_import_rejected_total{reason}`          | counter | Correlation imports refused, by the findings reasons  |
| `perf_sentinel_hub_analysis_queue_depth`                               | gauge   | Runs accepted and not yet claimed by a worker         |
| `perf_sentinel_hub_analysis_runs{status}`                              | gauge   | Runs currently stored, per status                     |
| `perf_sentinel_hub_findings{environment,finding_type,severity,status}` | gauge   | Distinct finding signatures stored, per environment   |
| `perf_sentinel_hub_findings_age_seconds`                               | gauge   | Age of the counts behind `findings`                   |
| `perf_sentinel_hub_database_bytes`                                     | gauge   | Size of the database file and its write-ahead log     |

Four things the shape is deliberate about.

Only a daemon gets a source series, and only one the Hub has actually observed.
A trace backend is never polled, and a daemon with no `source_state` row has
never been reached at all, so publishing a value for either would assert
something the Hub has not seen. That row is also what retention drops for a
source it stopped attempting, so a long forgotten source goes silent rather
than turning green.

A daemon never polled successfully gets no `last_success` series at all. Zero
would read as "succeeded just now", which is the opposite of never.

`analysis_runs` is a gauge, not a `_total` counter. A run moves between statuses,
and a finished one ages out on `Hub:Analysis:RunRetention`, so every series falls
as well as rises. A run still `pending` or `running` is never purged however old
its row looks, since a worker is about to write to it or already is. Every status
is emitted even at zero, a gauge that vanishes reading as a scrape failure rather
than as "nothing is in that state".

`findings` counts distinct signatures per environment, and the `status` it
carries is the environment's own, computed the way
`GET /api/findings?environment=` computes it: a finding still seen in staging
does not keep production's copy `active`. The series of one environment and
status therefore add up to the findings that read holds for them, across its
pages. `finding_type` and `severity` are free text a daemon sends, so a value
outside the engine's twelve types or its three severities counts under `other`
instead of opening a series of its own. Unlike `analysis_runs`, an empty
combination publishes no series. The zeros would be every environment times
every type, severity and status, nearly all of them empty for good, and the
family is read through `sum()`, where an absent series already is a zero. The
counts are kept for 15 seconds, because the endpoint is anonymous and a count
reads every finding of its environment. A scrape inside that interval is served
the previous counts, and every other family is still computed at scrape time. A
scrape waits at most five seconds for a new count. Past that it gets the previous
counts while the count finishes for a later scrape, and
`perf_sentinel_hub_findings_age_seconds` says how old they are. A count grows with
the signatures stored, so without that bound a store of millions of them made it
outlast Prometheus' 10-second `scrape_timeout`, and every family went missing with
it.

`perf_sentinel_hub_database_bytes` is read from the file system, the database file
plus its write-ahead log, so it still answers when SQLite itself is what fails. Alert
on its growth against the volume rather than on the kubelet's volume metrics alone: a
Hub whose database fills stops storing anything, and its sources then show
`database_full` as their last error, a code about the Hub's own volume and not about
the daemon, which answered.

A daemon that first sees more than `Hub:NewSignaturesPerHourWarning` signatures in an
hour, 1000 by default, is flagged on `/api/sources` and logged once until it falls back
under. A healthy fleet settles on a stable set of signatures, so a source that keeps
minting new ones is putting something unique per trace into a signature, and the Hub
stores a row per signature it gets. The check runs every five minutes and skips a
source whose findings all date from the last hour, as on a first contact. 0 turns it
off.

Cardinality is bounded by configuration. `source` takes the ids in
`Hub:Sources`, fixed at startup and restricted to 1 to 64 ASCII letters, digits,
`.`, `_` or `-`. `environment` takes the environments of those same sources,
which are refused a control character at startup and escaped on the way out.
The run `status` takes six constants. `findings` holds at most
environments × 13 × 4 × 3 series: twelve types, three severities, an `other`
bucket on each, and three statuses. Nothing a caller sends reaches a label, and
nothing a daemon sends reaches one unfolded.

The endpoint carries no authentication and stays open under `Hub:Auth`, so the scrape
needs no session. Without `Hub:MetricsPort` it answers on the Hub's own port, which it
shares with the launcher: a proxy opened for the browser serves `/metrics` as well unless
the path is excluded. Keep it behind whatever fronts the rest of the Hub.

`Hub:MetricsPort` (Helm `hub.metricsPort`) gives it a port of its own: `/metrics` then
answers there and nowhere else, and nothing else answers there. It is judged on the port
the connection reached, never on the `Host` header. Access control requires it, see
[ACCESS-CONTROL.md](ACCESS-CONTROL.md). Setting the key does not make the Hub listen on
the port: the chart adds it to `ASPNETCORE_HTTP_PORTS` (`"8080;9464"`), to the container
and to the Service as a port named `metrics`; outside Helm, set
`ASPNETCORE_HTTP_PORTS="8080;9464"` beside `Hub__MetricsPort=9464`. Once started, the Hub
checks the port is one it listens on besides another, and otherwise logs event `2002` at
Critical and stops with exit code 1: the main port would answer and `/metrics` would be
unreachable everywhere, or, the metrics port alone, the probes and the launcher would get
`404`. The chart refuses `8080`. The Ingress keeps routing to `http` only.

The chart leaves the scrape opted into rather than assumed:

```yaml
service:
  annotations:
    prometheus.io/scrape: "true"
    prometheus.io/port: "9464"   # hub.metricsPort; "8080" without it
    prometheus.io/path: /metrics
```

An operator that selects Services (a Prometheus Operator `ServiceMonitor`, a
VictoriaMetrics `VMServiceScrape`) matches the Service on
`app.kubernetes.io/name: perf-sentinel-hub` and scrapes its port `metrics` at
`/metrics`, or `http` without `hub.metricsPort`.

### Consuming it

Four files under [`examples/`](../examples), each validated rather than sketched.

| File                                                                       | Is                                                                     |
|----------------------------------------------------------------------------|------------------------------------------------------------------------|
| [`grafana-dashboard.json`](../examples/grafana-dashboard.json)             | Eleven panels over eight of the thirteen families, importable as it stands |
| [`FR/grafana-dashboard-FR.json`](../examples/FR/grafana-dashboard-FR.json) | The same dashboard with French titles and descriptions                 |
| [`prometheus-alerts.yml`](../examples/prometheus-alerts.yml)               | One rule, checked with `promtool check rules`                          |
| [`prometheus-scrape.yml`](../examples/prometheus-scrape.yml)               | A scrape job for a deployment that names its targets                   |

The engine ships its own dashboard for its own metrics, and the two do not
overlap: no panel here reads a daemon series, and no panel there reads a Hub
one. Import both to watch a fleet and the Hub collecting it.

The two findings panels count and never list. The history of each finding in
Grafana, a table that follows the time picker, is served by the engine's
findings dashboard, `examples/grafana-findings-dashboard.json` in its
repository, rather than by a dashboard of the Hub's. From engine 0.24.0 on, that
dashboard reads `GET /api/findings` through a second Infinity datasource pointed
at the Hub, beside the one pointed at a daemon. [API.md](API.md#read-api)
describes the `environment`, `from` and `to` parameters that read takes. With access
control on, that datasource needs a machine token, sent as `Authorization: Bearer`, see
[ACCESS-CONTROL.md](ACCESS-CONTROL.md#machine-tokens).

### Why there is only one alert

The Hub sits in no production request path, and push is the primary path: a
daemon POSTs its findings and retains and retries coalesced batches. So almost
nothing the Hub can report is worth waking anyone, and an alert that fires on a
condition a reader could have seen on a panel is noise. Four rules were written
and cut:

| Cut                       | Why, and where the condition shows instead                                                                                                                                     |
|---------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| A source is unreachable   | It watches the poll safety net, not the push path, so it goes red on a fleet whose daemons are all delivering. The fleet screen and two dashboard panels already draw it        |
| A source has gone stale   | Same blindness, and by the time a day has passed the last-success panel has been drawing the climb for a day                                                                    |
| The analysis queue backs up | Human-submitted work: a depth of 20 is 20 people clicking run. `GET /api/status` shows it to the very person who queued them, and nothing is lost while they wait               |
| Runs were interrupted     | Fires on the most routine event there is, a restart that caught a queued run, and never clears since nothing deletes those rows                                                 |

What survives is the one condition no dashboard can show, because a dead Hub
publishes no series and every panel goes blank exactly like a broken scrape
config.

The import counter is new and still does not earn a rule, which is worth stating
because it looks like it should. `unauthorized` rises when a key expires and
also when anyone at all posts an unknown `source_id`, and the Hub cannot tell
those apart: the only label that would separate them is the caller's own
`source_id`, which is exactly the unbounded value that must never reach a label.
Alerting on it would hand a stranger the ability to page you, and so would
`bad_request`, whose query-string half is checked before the key is.
`gate_full` and `write_timeout` are backpressure a daemon retries through, kept
apart because they name different knobs: too many uploaders at once against a
writer held by the poll or by retention. The reachable-only-with-a-key half of
`bad_request`, like `too_large`, is fixed in the exporter's repo rather than
here. All four belong on the panel, where a
human reads them with the context that tells them apart.

Two gaps remain, worth naming rather than papering over. A push blocked before
it arrives, by a network policy for instance, produces no request and therefore
no rejection, so the counter cannot see the most common breakage, and
`source_last_import_seconds` cannot either, since a daemon with no new findings
pushes nothing and looks identical. Telling those apart needs per-finding
provenance the Hub does not store. And Prometheus holds no list of the sources
that ought to exist, so a daemon the Hub has never reached is silent rather than
alarming, which is a case for the fleet screen.

## Backup

`first_seen` history is the one thing the Hub stores that nothing upstream can
reconstruct. The daemon's ring buffer forgets, so losing the volume loses the timeline.

The binary ships a `backup` command that snapshots the live database with SQLite
`VACUUM INTO`, safe next to the single writer thanks to WAL. It reads `Hub:DatabasePath`
from the same configuration as the server, refuses to overwrite an existing destination,
and removes its partial file when the copy fails.

The snapshot is a full copy written onto the same volume, so keep at least the database's
own size free on the PVC before starting one. The chart default is 1Gi total. That size
grows with the observation days the Hub keeps, one row of about 300 bytes per finding,
daemon and observed day for the length of `Hub:Retention`: 2,000 signatures seen daily on
three daemons at the 180-day default come to roughly 300 MB, so size the volume for the
fleet rather than for the default.

```bash
kubectl exec deploy/perf-sentinel-hub -- /app/PerfSentinelHub backup /data/hub-backup-20260826.db
```

Date the filename. The overwrite guard makes a fixed path fail on the second run, and the
chiseled runtime image has no shell to delete a leftover with.

### Getting the file off the cluster

`kubectl cp` cannot pull from the Hub pod, which has no tar inside. Mount the same PVC in
a short-lived helper pod, copy from there, and remove the snapshot from that pod.

The volume is `ReadWriteOnce`, so pin the helper to the node the Hub pod runs on. Run it as
the Hub's own uid so it can read and delete the files, and give it the security context a
`restricted` namespace demands:

```bash
NODE=$(kubectl get pod -l app.kubernetes.io/name=perf-sentinel-hub -o jsonpath='{.items[0].spec.nodeName}')
kubectl apply -f - <<EOF
apiVersion: v1
kind: Pod
metadata:
  name: hub-backup-fetch
spec:
  nodeName: ${NODE}
  securityContext:
    runAsNonRoot: true
    runAsUser: 1654
    runAsGroup: 1654
    seccompProfile: { type: RuntimeDefault }
  containers:
    - name: fetch
      image: busybox:1.37
      command: ["sleep", "3600"]
      securityContext:
        allowPrivilegeEscalation: false
        capabilities: { drop: ["ALL"] }
      volumeMounts: [{ name: data, mountPath: /data }]
  volumes:
    - name: data
      persistentVolumeClaim: { claimName: perf-sentinel-hub }
EOF
kubectl cp hub-backup-fetch:/data/hub-backup-20260826.db ./hub-backup.db
kubectl exec hub-backup-fetch -- rm /data/hub-backup-20260826.db
kubectl delete pod hub-backup-fetch
```

Locally, `make backup DB=/path/to/hub.db DEST=backup.db` wraps the same command.

## Restore

Replace the database file while the Hub is fully stopped. Scale the Deployment to zero and
wait for the pod to actually terminate before touching the volume, since graceful shutdown
can take tens of seconds:

```bash
kubectl scale deploy/perf-sentinel-hub --replicas=0
kubectl wait --for=delete pod -l app.kubernetes.io/name=perf-sentinel-hub --timeout=120s
```

Then, from the helper pod, copy the backup over `/data/hub.db`, delete any stale
`hub.db-wal` and `hub.db-shm` next to it, and scale back up.

## Machine tokens

With access control on, CI jobs and dashboards read the Hub with a machine token, see
[ACCESS-CONTROL.md](ACCESS-CONTROL.md#machine-tokens). Issue one token per team, granted
only that team's projects through a findings-only role: a token in a shared tool serves
its whole scope to everyone who uses the tool.

- **Revoke** a token by removing its entry from `hub.access.machineTokens`. The ConfigMap
  checksum changes and the pod rolls, so the token stops working at once.
- **Rotate** a token by updating its Secret, then
  `kubectl rollout restart deployment/<release>`. The chart cannot hash a Secret it does
  not own, so a changed Secret alone rolls nothing. The same holds for the OAuth client
  secret and the source keys.
- An entry past its `expiresAt` is refused at every request with the `401` an unknown
  token gets, and logged at startup as a warning (event `2001`, naming the token, never
  its value). It never stops the Hub.

## Terminal tokens

Developers sign the CLI in with `perf-sentinel login`, which needs
`hub.auth.trustedProxyNetworks` set to the ingress controller's pods alone, see
[AUTHENTICATION.md](AUTHENTICATION.md#terminal-sign-in). A token is revoked from the
launcher (its holder on Your access, an admin on Coverage) or by `perf-sentinel logout`, and ends with the session that approved it
anyway. The table and the pending codes need nothing from an operator: expired tokens
leave with the daily retention pass, and pending codes live in memory, so a restart only
cancels the logins in flight. Events, at Information unless stated, never with a code or a
token:

| Event | When |
|-------|------|
| `2010` | A terminal sign-in was approved, logged at the click even if no terminal collects the token: who, their subject, the requesting address, whether it came from another network than the approving browser |
| `2011` | A terminal sign-in was denied |
| `2012` | A terminal token was revoked: by whom (its holder, through `/auth/revoke`, or a subject from the access page), its owner and id |
| `2013` | Warning: a code was refused because too many are pending, from that address (10) or in all (200) |
| `2014` | Warning, at startup with access on: terminal sign-in is off because `Hub:Auth:TrustedProxyNetworks` is empty |
| `2015` | The terminal collected the token of an approved sign-in: the token id, the requesting address, who approved it and their subject |

## Local accounts (break-glass)

A local account signs in on the Hub's own page while the provider is down, see
[AUTHENTICATION.md](AUTHENTICATION.md#local-accounts). Its password never reaches the
values, a command line or the Hub's log: the Hub holds only its hash, from a Secret.

**Creating one.**

1. Hash the password with the image. `hash-password` reads it from stdin, never from an
   argument, and prints the hash alone; the password holds 15 to 1,024 characters.

   ```bash
   printf 'Password: ' >&2; IFS= read -rs password; echo
   printf '%s\n' "$password" | docker run --rm -i ghcr.io/institut-du-numerique-responsable/perf-sentinel-hub:<version> hash-password > ops.hash
   unset password
   ```

   The first line works in bash and zsh; `IFS=` keeps leading and trailing spaces, which
   sign-in compares as typed. `printf` is a shell builtin, so the password appears in no
   process list. From a
   running Hub, `kubectl exec -i deploy/perf-sentinel-hub -- /app/PerfSentinelHub hash-password`
   reads stdin the same way. On a terminal of its own, `hash-password` asks twice without
   echoing.
2. Store it, then delete the file:
   `kubectl create secret generic perf-sentinel-hub-break-glass --from-file=ops=ops.hash`.
   The hash is not the password, but keep it as private: 600,000 iterations slow an
   offline guess, they do not stop one.
3. Declare the account and give it a group, then `helm upgrade`:

   ```yaml
   hub:
     access:
       adminGroups: ["/hub-admins", "internal:break-glass"]
       groups:
         - { name: break-glass, members: ["local:ops"] }
       localAccounts:
         - { name: ops, secretName: perf-sentinel-hub-break-glass, secretKey: ops }
   ```

Keep the password in the team's vault, and test the account once, through
`/auth/sign-in`, while the provider still works.

**After every use, rotate it.** Hash a new password, replace the Secret, then
`kubectl rollout restart deployment/<release>`: the chart cannot hash a Secret it does not
own, so a changed Secret alone rolls nothing. The restart ends every session of the
account. Terminal tokens it approved live until their expiry, 8 hours at most: revoke them
from the launcher's Coverage screen.

```bash
kubectl create secret generic perf-sentinel-hub-break-glass --from-file=ops=ops.hash \
  --dry-run=client -o yaml | kubectl apply -f -
kubectl rollout restart deployment/perf-sentinel-hub
```

**Removing one.** Delete its entry and its `local:<name>` member. The ConfigMap checksum
rolls the pod, and the account's sessions end at their next request.

**Throttled.** Five failures from one address for one name start a backoff, from 30
seconds up to 15 minutes, and one address hashes at most 5 times a minute. There is no
lock to clear: an attacker's failures slow their own address, not the operator's. The
state lives in memory, so a restart clears it.

| Event | When |
|-------|------|
| `2020` | Warning: a local account signed in, with its name and the client address |
| `2021` | Warning: a local sign-in was refused, with the account (`(unknown name)` for an undeclared one, never what was typed), the client address and the consecutive failures |

## Rolling back with access control on

Never roll the image back below the release that enforces access control while
`hub.access.enabled` is `true`. Earlier builds ignore the `Hub:Access` keys and nothing
checks for a newer configuration, so they **fail open**: `/api/findings` is anonymous
again and every session sees every record. `Hub:Auth:RequiredGroups` still holds in
those builds, back to the release that added it, so only its members sign in. Set
`hub.access.enabled: false` first, as a deliberate decision, then roll back.

Rolling back below the storage release (schema version 9) and forward again is safe for
the data: every start fills in the attributes, the per-source service and the incident
pairs an older build left out.
