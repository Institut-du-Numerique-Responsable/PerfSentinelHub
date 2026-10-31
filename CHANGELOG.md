# Changelog

All notable changes to PerfSentinelHub are recorded here.

## [Unreleased]

### Added

- The launcher reads in French as well as English. A FR/EN button in the top bar switches the
  language in place, a first visit follows the browser's languages, and a click is remembered under
  `perf-sentinel:lang`. Numbers, dates and durations follow the language. What the engine writes
  (daemon recommendations, run warnings, suggested fixes, disclosure limits) stays in English,
  marked `lang="en"` and tagged EN on a French screen, and printed commands keep the engine's syntax.
- Every refusal the Hub words itself carries a stable `code` beside its unchanged English
  `detail`, which the launcher translates. A client that does not know a code shows `detail`.
- Recap reports have a language. `POST /api/recap/reports` takes `lang` (`en` by default or
  `fr`), a report item carries `language`, and `GET /recaps/{id}/{view}.html?lang=` renders a page
  in the other language. A subscription takes `Language`, the language of its reports, its webhook's
  `plain` sentences and its mails, and `Recipients`, addresses with a language of their own: each
  mail's subject, body and attached page follow its address.
- The sign-in page and the terminal approval page follow `?lang=`, then the language picked with
  the launcher's button (a `perf-sentinel-lang` cookie), then the browser's `Accept-Language`, and
  carry the FR/EN button as a link.
- Share cards choose the country of their equivalences: France, the United Kingdom or the United
  States. `Hub:Cards:Country` (`fr`, `gb` or `us`, France when unset, Helm `hub.cards.country`)
  sets the default, the card screen gains a Country of the equivalences choice, and
  `POST /api/cards/render` and `/api/cards/attachment` take `equivalence_country`, which the Hub
  always passes to the engine in `card.json`. Another country sends France and says so. The options
  response carries the Hub's country. On an engine before `0.40.0`, the image's being `0.40.0`, a
  French card leaves the key out and a UK or US card is a `422 card_country_unsupported`.

### Changed

- The image ships perf-sentinel `0.40.0` as its analysis engine, repinned by digest from
  `0.39.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.39.0` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`. The disclosure
  schema stays `perf-sentinel-report/v1.8`. Engine `0.40.0` reads an equivalence country in a
  share card's `card.json`, with UK rows from the DESNZ and a US row from the US EPA. A backend
  analysis gives the same findings, signatures and energy figures as on `0.39.0`.

## [0.10.0] - 2026-10-31

### Added

- A recap names the sources whose daemon recorded nothing until after its period, `late_sources`,
  and leaves them out of both periods instead of reading the period as uncovered. A source that
  started during the period still leaves the period before uncovered. A report waits no longer
  for such a source to seal the period's last day.
- The card options carry `previous_whole` and `confirmed_fixes_count`, so the screen says why a
  collective card drops its comparison and no longer blocks one whose fixes it cannot name. A
  collective card for a period holding a fix marked done before the Hub kept fix details is
  refused with `unkept_fixes` rather than signing a short count.
- The detection import and poll store the card signal a daemon sends from the card release:
  `below_threshold_passes` on a count row, the passes below the threshold of a watched
  signature, and `thresholds` on a day, the six detection thresholds the daemon applied. Both
  merge by maximum. A count row with zero detections is accepted and no read counts it.
- The answer to a detection push carries `watch`, the fixed findings of a threshold type
  (`slow_*`, `excessive_fanout`, `chatty_service`, `pool_saturation`, `serialized_calls`) that
  source detected and has not yet confirmed fixed, newest first, at most 10,000, `[]` when none.
  The daemon counts their passes below the threshold, the signal a share card stands on.
- An action that enters `done` keeps the finding's envelope as it stood then, from the newest
  copy the caller reads, in `finding_actions.frozen_finding_json`. It is never served, outlives
  the finding's purge and is cleared when the action leaves `done`. A share card reads it.
- Quarter, semester and year periods, for share cards only. `GET /api/recap` still takes
  `day`, `week` or `month`.
- `Hub:Cards:Organisation` names the organisation that signs share cards and turns them on, and
  `Hub:Cards:Domain`, optional, is the domain a card's DNS line attaches. The chart sets them from
  `hub.cards`. A new permission, `cards.write`, held by `member` and `admin`, lets a caller create
  a card from the sources it covers. `GET /api/status` answers `cards: true` to such a caller when
  the engine has `card`, `cards: false` when it does not, and leaves `cards` out otherwise.
- A share card signing key, made by the engine (`perf-sentinel card keygen`) the first time it is
  needed. The private key is a file in `card-keys/` beside the database (mode 0600 in a 0700
  directory), which `backup` does not carry; its public half and dates go in a new table,
  `card_keys` (schema 20), never purged. A key whose file is gone is retired at next use and a new
  one made. `GET /api/cards/keys`, `POST /api/cards/keys/rotate` and `POST /api/cards/keys/revoke`,
  for `access.admin` only, list the keys with their DNS lines and `.well-known` file, rotate the
  key and revoke one from the first day it may have leaked. Every card a revoked key signed reads
  as revoked, whatever its date.
- Share cards. `GET /api/cards/options` lists what a card of a closed period can show: the
  period's refusal and its sentence, the signed scope, the confirmed fixes with their labels.
  `POST /api/cards/render` computes the figures, has the engine sign and draw the card
  (`perf-sentinel card render`) and answers the PNG, the SVG, the JPEG XL (`image/jxl`, to archive
  or send, LinkedIn does not accept it) or the PDF carousel; only the title, the team and a story's
  label are free, any other key is a `400`. An SVG comes with
  `Content-Security-Policy: default-src 'none'; sandbox`, so opened from its URL it runs nothing.
  `POST /api/cards/attachment` checks a fresh card's DNS attachment with
  `perf-sentinel verify-card --online`, the one card route that reaches the network
  (`cloudflare-dns.com` and the configured domain), and answers whether the file is the one
  signed (`content`) and its figures as signed (`figures`), which Verify attachment lists to
  compare with the preview. A free field holding `{"content_sig":"`, the text the card file keeps
  for its signature, is a `400`. The three need `cards.write` and `findings.read` and somebody to
  name. `Hub:Cards:MaxConcurrentRenders` (default 2, chart `hub.cards.maxConcurrentRenders`) bounds
  how many renders, attachment checks and key rotations run the engine at once. One more is a
  `503` with `Retry-After: 5`, which the screen waits out once before asking again.
- A story whose service or endpoint name the card cannot print is refused with `unprintable_fix`
  and its sentence, rather than reading as an unknown or unconfirmed fix.
- A card whose environment and source names, joined with `", "`, take more than 120 characters is
  refused with `scope_too_long` and its sentence, which asks for an environment or a source,
  rather than the engine's `502`.
- The recap screen gains Share a result: pick a story, a curve or a collective card over a closed
  period, add a title, a team and, for a story, an everyday name for the action, preview the card,
  download it as PNG, SVG, JPEG XL or PDF, and verify its attachment. Administrators find the
  signing key there, with the lines to publish, rotation and revocation.

### Changed

- The image ships perf-sentinel `0.39.0` as its analysis engine, repinned by digest from
  `0.38.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.38.0` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`. The disclosure
  schema stays `perf-sentinel-report/v1.8`. Engine `0.39.0` brings the `card keygen`,
  `card render` and `verify-card` commands the share cards run, and counts the passes below
  the threshold of the signatures the Hub asks a daemon to watch. A backend analysis gives
  the same findings, signatures and energy figures as on `0.38.0`.
- Detection counts, days and ledgers leave on a clock of their own, `Hub:DetectionRetention`
  (default `1100.00:00:00`, read as `Hub:Retention` when shorter, so a Hub keeping findings
  longer still starts), instead of the 180 days of a finding: a
  year card compares a year with the whole year before it. The chart sets it from
  `hub.detectionRetention`. A database upgraded from 0.9.0 keeps only the detection days its
  former 180-day purge left.

## [0.9.0] - 2026-10-29

### Added

- A recap screen, `#/recap`, and `GET /api/recap`: the findings to fix first, what moved since
  the previous period of the same length, what the teams did about them and a carbon estimate,
  by day, week or month, in an Engineering and a Management view. The page and the route follow
  `findings.read`.
- Actions on findings: `POST /api/actions` and `PATCH /api/actions` declare who works on a
  finding and where it stands (`todo`, `in_progress`, `done`). The Hub judges a done action on
  the days after it and reports it `confirmed`, `contradicted` or `came_back`. Two permissions
  go with them: `actions.write` for the routes, carried by the built-in `member` role, and
  `recaps.read` for the recap reports, Hub level, carried by `admin`.
- `Hub:Auth:DisplayNameClaim` (default `name`): the userinfo field an action's owner shows by,
  stored as `owner_display` beside the identity. The recap shows that name, or without one a name
  derived from the identity (`alice.martin@acme.com` reads `Alice Martin`), with the identity on
  hover. The claim is never used for access.
- Endpoint labels: `PUT /api/labels` names an endpoint in business words, which titles its row
  in the recap, with the `labels.write` permission. `PUT /api/labels/services` names a whole
  service once, so every unnamed step of it reads in business words (`in Payment`,
  `An unnamed step in Payment`, a Management scope line too); the recap carries it as
  `service_label` beside `service` and in `filters`, and the Engineering view names a service from
  the first card of it. A label with a format character (U+202E and the like) is refused.
- `Hub:Recaps:MovementThreshold` (default `0.2`), the relative change past which a finding
  reads `up` or `down`.
- Frozen recap reports. `Hub:Recaps:Subscriptions` freezes the last closed day, week or month
  once the ledgers have sealed it (or after `Hub:Recaps:MaxDelay`) and delivers it by signed
  webhook (`Hub:Recaps:Webhooks`) and by e-mail through the organisation's SMTP relay
  (`Hub:Recaps:Smtp`), the management view attached and the engineering view linked. Reports
  leave after `Hub:Recaps:Retention`. Four routes list, read, generate and show them, behind
  `findings.read` and `recaps.read`, and the recap screen gets a Reports panel.
- The Hub reads the carbon day fields that perf-sentinel engines send with their ledger days
  (avoidable grams, avoidable I/O and total I/O) and keeps them per day. Engines that send
  none leave the recap's carbon figure empty.
- The chart takes `hub.recaps`: the movement threshold, retention, delay and public URL, the
  subscriptions, the webhooks and the SMTP relay. Webhook URLs and secrets and the SMTP password
  come from Secrets as `Hub__Recaps__*` environment variables, never from the ConfigMap.

### Changed

- The image ships perf-sentinel `0.38.0` as its analysis engine, repinned by digest from
  `0.37.1`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.37.1` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`. The disclosure
  schema stays `perf-sentinel-report/v1.8`. Engine `0.38.0` adds the day's avoidable carbon
  to the detection ledger (`avoidable_gco2`, `avoidable_gco2_low`, `avoidable_io_ops` and
  `total_io_ops` on each day of `GET /api/detections` and of the push), which the recap reads
  for its CO2 estimate. Upgrade the Hub before or with the daemons: a Hub older than this one
  drops the four keys, and a day it already acknowledged is not sent again until it changes.
  A backend analysis gives the same findings, signatures and energy figures as on `0.37.1`.

## [0.8.6] - 2026-10-28

### Changed

- The image ships perf-sentinel `0.37.1` as its analysis engine, repinned by digest from
  `0.35.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.37.0` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`. The disclosure
  schema stays `perf-sentinel-report/v1.8`. Engine `0.37.0` adds the detection ledger this
  release reads: a per-day count of every finding a daemon detects, served on
  `GET /api/detections` and pushed to `POST /api/import/detections`, and
  `perf-sentinel hub findings` relays `--from`, `--to`, `--offset`, `--include-acked` and
  `--signature` to the Hub. Engine `0.37.1` pushes the ledger after the findings when a
  daemon shuts down, so a long ledger no longer spends the findings' drain budget. Engine
  `0.36.0` changed only its SARIF, which the Jenkins Warnings NG parser reads again, and the
  expiry hint of `perf-sentinel ack create`. The Hub reads neither. A backend analysis gives
  the same findings, signatures and energy figures as on `0.35.0`, and the findings wire
  format is unchanged.

### Added

- The Hub stores the detection ledgers of daemons on perf-sentinel 0.37.0 and later, in three
  tables of schema V15. It polls each ledger after the findings, page by page from its cursor,
  and accepts the daemon's push on `POST /api/import/detections?source_id=<id>`, under the
  findings import's key, gate and write wait. Both paths merge absolute values per UTC day by
  their maximum, so a poll, a push and a replay land on the same rows, and a source's ledgers
  add up across daemon restarts. A daemon older than 0.37.0 is never asked. The poll passes
  count on `perf_sentinel_hub_detection_reads_total{source,outcome}` and
  `perf_sentinel_hub_detection_rows_total{source}`, the refused pushes on
  `perf_sentinel_hub_detection_import_rejected_total{reason}`, and the example dashboards
  gain a "Detection ledger reads by outcome" panel.
- A read of `GET /api/findings` with `from` or `to` serves the detection volume of each
  finding and each of its sources over the window's UTC days: `detections`,
  `detections_complete`, `avoidable_io_ops`, `detections_gaps` and
  `detections_sampling_rate`, plus `detections_from` and `detections_to` on the finding. A
  count is null when nothing is known, exact when complete, and otherwise a lower bound with
  its reasons from a closed set. A finding the daemons counted in the window is listed even
  when no poll observed it there, and a source that only counted it joins `sources`. A read
  without `from` or `to` is unchanged, byte for byte.
- The ack page shows the detections of the last 7 days and their avoidable I/O, per source
  with the reasons a count falls short, beside the unwindowed finding.

### Fixed

- A finding's `last_seen` is now the source daemon's last detection, the envelope's
  `stored_at_ms` clamped to the Hub's clock, instead of the time of the poll that read it.
  A daemon keeps a finding in its ring long after it stops detecting it, so every poll moved
  `last_seen` to the present and a fixed finding stayed `active` for as long as the ring held
  it. The observation day, the endpoint heartbeat, the grouping and attribute stamps and the
  push path follow the same time. A producer without `stored_at_ms` keeps the poll time, and
  a daemon clock running ahead is cut to the Hub's.

## [0.8.5] - 2026-10-27

### Changed

- The image ships perf-sentinel `0.35.0` as its analysis engine, repinned by digest from
  `0.34.1`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.34.1` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`. The disclosure
  schema stays `perf-sentinel-report/v1.8`. The engine's one output change is its SARIF,
  valid against the SARIF 2.1.0 schema again: the service and endpoint move under
  `result.locations[0].logicalLocations` and the suggested fix to
  `result.properties.suggestedFix`. The Hub asks the engine for JSON and reads no SARIF, so a
  backend analysis gives the same findings, signatures and energy figures as on `0.34.1`, and
  neither the daemon wire format nor the findings JSON changes. The rest of the engine release
  is documentation and its GitLab and Jenkins CI templates, which the Hub does not use.

## [0.8.4] - 2026-10-27

### Changed

- The toolchain moves to .NET 10.0.14, released on 2026-10-25: the build image to
  `sdk:10.0.403-resolute-aot`, the runtime image to `runtime-deps:10.0.14-resolute-chiseled-extra`,
  and `global.json` and every workflow to SDK `10.0.403`. The lock, regenerated by the new SDK,
  moves `Microsoft.DotNet.ILCompiler` and `Microsoft.NET.ILLink.Tasks` to `10.0.14`.
  `config/supply-chain.json` carries the new SDK archive, digests and package hashes.

### Fixed

- A poll that returns the daemon's cap no longer logs the "possibly truncated" warning (event 1102)
  when the daemon confirms in `api/config` that it pushes to the Hub and the Hub received a findings
  push from it within the last two hours: the push already delivered what the cap cut, so the warning
  came back on every poll for nothing. A daemon whose config cannot be read, or whose pushes do
  not reach this Hub, still gets the warning. The config is read at most once per poll, shared
  with the missed findings count, and never from a daemon before perf-sentinel 0.31.2, which has
  no such field.
- The missed findings count (event 1104, `perf_sentinel_hub_source_missed_findings_total`) no
  longer trusts the export flag alone: a daemon whose config says it pushes but from which no
  findings push landed in the last two hours is counted, since nothing it pushed reached the Hub.
  An unreadable config still counts as pushing.
- Only a findings push counts as landed for both: the new `source_finding_imports` table (schema
  version 14) records it apart from `source_imports`, which incident and correlation pushes
  refresh too. A pushed batch with no accepted finding does not count either. A failed read of
  that table counts as no push rather than failing the poll. After the upgrade the table starts empty, so until a pushing daemon's next findings
  push, within the hour, a capped poll may log 1102 and an overflowing ring may be counted as
  missed (1104).
- A daemon connection that drops mid-body now fails every daemon read as an
  `HttpRequestException`, like any other network error, instead of a bare `IOException`. Reading
  `api/config` therefore no longer fails the whole poll on such a cut: the config is treated as
  unread, as for any other read error.

## [0.8.3] - 2026-10-26

### Fixed

- The image now ships `appsettings.json`. The `Dockerfile` copied the binary, the SQLite
  library and `wwwroot` but not this file, so in a container the Hub read only its environment
  and the chart's `appsettings.Production.json`. The 0.8.2 log levels therefore never applied
  there, and the image still logged every HTTP request and every outgoing call at Information.
  The `Hub` defaults in the file match those in code, so nothing else changes. The native
  archives already shipped the file.

## [0.8.2] - 2026-10-24

### Changed

- The image ships perf-sentinel `0.34.1` as its analysis engine, repinned by digest from
  `0.33.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.33.0` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`. The disclosure
  schema stays `perf-sentinel-report/v1.8`. A backend analysis runs the engine with its
  `[detection]` section only, so its findings, signatures and energy figures are the same
  as on `0.33.0`. A daemon can now set `[daemon] findings_stdout = false` to stop writing
  its findings to stdout once the Hub receives them.
- `appsettings.json` keeps `Microsoft.AspNetCore` at Warning in every environment, as
  `appsettings.Development.json` already did. Production logged four Information lines per HTTP
  request (hosting start and finish, endpoint, result), so probes, the `/metrics` scrape,
  daemon imports and dashboards wrote tens of thousands of lines an hour. The outgoing calls
  (daemon polls, pg_stat queries, update check) go to Warning too: `System.Net.Http.HttpClient`
  logged four lines per call, each with its URL. The Hub's own categories stay at Information,
  and `Logging__LogLevel__Microsoft.AspNetCore` or `Logging__LogLevel__System.Net.Http.HttpClient`
  lowers either again for a debugging session.

## [0.8.1] - 2026-10-23

### Changed

- The image ships perf-sentinel `0.33.0` as its analysis engine, repinned by digest from
  `0.32.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.32.0` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`. The disclosure
  schema stays `perf-sentinel-report/v1.8`. A backend analysis runs the engine with its
  `[detection]` section only, so its findings, signatures and energy figures are the same
  as on `0.32.0`. A daemon that declares its database or broker hosts as groups of
  different sizes now prices each group on its own, and a daemon-source run renders that
  daemon's own report.

### Fixed

- Three launcher functions declared twice in `app.js`, one IIFE, so the later declaration
  replaced the earlier for every caller since `0.7.0`. A finished run's `Open the dashboard`
  read `[object Object]`, and a click greyed it out and did nothing: it ran the Disclosures
  screen's write button. The `Download` button of a generated config file became
  `Download the PDF` and asked `/api/disclosures/` for a file. The home and launch screens
  showed the run page's "keeps what it last read" warning instead of their own Hub-down
  banner. The Disclosures and run page copies are renamed `disclosureActionButton`,
  `disclosureFileButton` and `runUnreachableBanner`, and `tests/launcher.test.js` fails on
  any top-level function `app.js` declares twice.

## [0.8.0] - 2026-10-21

### Changed

- The image ships perf-sentinel `0.32.0` as its analysis engine, repinned by digest from
  `0.31.4`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.31.4` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`. The engine now
  reads and renders disclosures on schema `perf-sentinel-report/v1.8`, which the `0.31.4`
  engine refused as newer than itself, so the Hub imports and renders them. A backend
  analysis runs the engine with its `[detection]` section only, so its findings, signatures
  and energy figures are the same as on `0.31.4`. A daemon-source run renders the daemon's
  own report, whose totals hold the database and broker host energy when that daemon
  declares or measures it.

### Added

- A disclosure's applications table gains `Database hosts` and `Broker hosts` rows when its
  v1.8 report counts that host energy inside `total_energy_kwh`, read from
  `aggregate.database_waste.in_totals_energy_kwh`, `aggregate.messaging_waste` and the
  `carbon_breakdown` host terms. The hosts used to be in the total the screen shows above and
  in no row. Every share is now taken over the report's `total_energy_kwh`, as the engine's
  PDF takes it, instead of over the rows, so a period with energy no service was attributed
  no longer inflates the applications' shares. A host under 0.005 kWh reads in scientific
  notation rather than `0.00`, and a host the totals held for part of the period says
  `in K of N windows`. A report older than v1.8 adds no host row.
- The daemon view's carbon scoring card describes `grid_feeds`, the grid operator feeds a
  0.32.0 daemon lists, and `fingrid_dataset`. `electricity_maps` says it reports the
  configuration, and no longer that off means the embedded table, since a region can take its
  grid operator's live value instead.

### Fixed

- The daemon view's `energy_source` named only the application part of a snapshot whose
  energy holds database or broker host energy. It follows the engine's 0.32.0 label again
  and adds `· plus database hosts, <model>` or `· plus broker hosts, <model>` for each host
  figure the daemon counts inside its totals. A figure beside the totals adds nothing, and a
  model that is not a short plain token reads `unknown`.

## [0.7.4] - 2026-10-19

### Changed

- The image ships perf-sentinel `0.31.4` as its analysis engine, repinned by digest from
  `0.31.3`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.31.3` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`. `0.31.4` changes
  no code outside the engine's tests, so an analysis the Hub runs gives the same findings and
  signatures as on `0.31.3`.

## [0.7.3] - 2026-10-19

### Added

- An incident's findings carry an Est. impact (ops) column beside Severity: the avoidable
  I/O operations the findings dashboard computes for the same incident, traces seen in its
  window times the finding's extra operations per trace. It sorts like the other columns,
  most first, and reads a dash when the daemon has green scoring off.

### Changed

- The toolchain moves to .NET 10.0.13, released on 2026-10-13: the build image to
  `sdk:10.0.402-resolute-aot`, the runtime image to `runtime-deps:10.0.13-resolute-chiseled-extra`,
  and `global.json` and every workflow to SDK `10.0.402`. `Microsoft.AspNetCore.Mvc.Testing` and
  `Microsoft.Data.Sqlite` follow to `10.0.13`, and the lock, regenerated by the new SDK, moves
  `Microsoft.DotNet.ILCompiler`, `Microsoft.NET.ILLink.Tasks` and the `Microsoft.Extensions` family
  they pull to `10.0.13`. `config/supply-chain.json` carries the new SDK archive, digests and
  package hashes.
- An incident finding's template shows whole, wrapped over as many lines as it needs, where
  it was cut to one line with the rest in its tooltip: two templates sharing a long prefix,
  two routes of one host for instance, read alike.

### Fixed

- With `Hub:Auth` on and `Hub:Access` off, `GET /api/incidents`, `GET /api/incidents/{id}` and
  `GET /api/correlations` stay open, as `/api/findings` already did. A Grafana datasource
  reads them without a credential, and the findings dashboard's Correlation episodes and
  Correlated pairs panels and its Hub incidents annotation answered `401` there, since a
  machine token only exists with access control on. `POST /api/incidents/refresh` still
  needs a session, and with `Hub:Access` on these reads still need a session or a machine
  token.
- The Ack link of an incident's findings is underlined. A lone word in a table cell, set
  apart by its colour only, did not read as a link.
- The config sections the screens name read as code, as they already did in the notes under
  the tables: `[daemon.correlation]` and `[daemon.hub_export]` in the Correlations help and
  empty screen, `[daemon.incidents]` in the empty Incidents screen, and `[daemon]` in a
  daemon's read error. A help tip renders backticks the way the daemon's hints do.
- Every sortable column head of an incident's findings carries a muted double arrow, so a
  column reads as sortable before it is the sorted one, where only the sorted column showed
  an arrow.

## [0.7.2] - 2026-10-18

### Changed

- The image ships perf-sentinel `0.31.3` as its analysis engine, repinned by digest from
  `0.31.2`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.31.2` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`. `0.31.3` masks a
  quote escaped with a backslash, a hexadecimal number and a number with an exponent in its SQL
  templates, where the escaped quote used to leak the rest of its string into the template the
  Hub stores. A finding on such a query gets a new signature, so an analysis the Hub runs, or a
  daemon on `0.31.3`, reports it as a new finding: the old one stops receiving occurrences and
  its acknowledgment does not carry over.

## [0.7.1] - 2026-10-17

### Added

- The Hub counts the findings that left a polled daemon's ring before a poll read them.
  Against a daemon on perf-sentinel 0.31.2 or later that does not push to the Hub, two polls
  of one process tell how many findings went into the ring in between, from
  `findings_stored_total`, and the ones beyond the ring's length were evicted unread. The Hub
  logs them (event 1104), publishes `perf_sentinel_hub_source_missed_findings_total` per
  source and adds a hint beside the daemon's own in the fleet screen, naming
  `max_retained_findings`, `Hub:PollInterval` and the daemon's Hub export. A restart, a first
  poll or a daemon that pushes counts nothing, and the hint counts since the daemon last
  started, so a fix and a restart clear it. The daemon's config is read only when a poll
  finds a candidate, after the findings.

### Changed

- The findings ring no longer turns a daemon's verdict to near capacity, and its gauge is no
  longer toned. It fills on any daemon that runs long enough, the daemon's own advisor has no
  rule on it, and the Hub keeps what it evicts. The traces and analysis queue gauges keep the
  90 % line.

- The image ships perf-sentinel `0.31.2` as its analysis engine, repinned by digest from
  `0.31.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.31.1` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  A disclosure the Hub renders now ends on an annex of every field the schema defines,
  `not reported` marking the absent ones, and its pages print the fields `0.31.0` left in the
  attachment, each energy model named beside its tag. A report with a character outside the
  bundled fonts in a field the `0.31.0` pages did not print is now refused at import with
  `unsupported_characters`. A report a daemon serves names its carbon model on the Total CO2
  cards. `0.31.2` adds the `findings_stored_total` the missed findings count above reads, and
  `hub_export_enabled`. No detector, normalizer or finding signature changes, so every stored
  finding keeps its signature.

### Fixed

- An empty Disclosures screen says a scheduled job builds a disclosure from the daemons'
  archives and publishes it with the import key, where it named a CI job.

## [0.7.0] - 2026-10-16

### Added

- A report of a daemon source that has an ack credential acknowledges through the Hub. The
  render passes `--hub-relay` when the engine takes it (0.31.0 and later), and the report's Ack
  and Revoke buttons call the ack relay at the Hub's own origin, in the reader's name and under
  their rights, with access control on or off. No daemon key in the browser, no CORS entry at
  the daemon, and no route from the viewer to the daemon. Before, such a report showed
  "Unreachable" wherever the daemon was not exposed, and rendered static with access on.
- `PgStat` and `MySqlStat` on a source fill a report's `pg_stat` and `mysql_stat` tabs from a
  Prometheus the engine scrapes at render time, with a `Selector` for an exporter that serves
  several tenants or a primary with its replicas. Before, the Hub never passed those flags and
  the tabs stayed empty. A render the scrape fails is tried again without the tab (event 1603),
  and the chart takes `pgStat` and `mysqlStat` per source, the auth header from a Secret.
- `GET /api/sources/{sourceId}/acks` lists the daemon acks the Hub mirrors at a source, in
  the shape of the daemon's own `GET /api/acks`, filtered by what the caller reads there.
- Periodic disclosures. A scheduled job publishes the report `perf-sentinel disclose` built
  from the production daemons' archives, with its attestation and Sigstore bundle when signed,
  to `POST /api/import/disclosures` with `Hub:Disclosures:ImportKey`. The embedded engine's
  `disclose-pdf` checks the content hash and the attestation's binding and renders a PDF/A-3b
  before anything is stored, and a refusal stores nothing. An engine that cannot render stores
  the files without a PDF, to render again later. A signed copy of a report stored unsigned
  replaces it, and is refused with a `409` when its organisation, period, intent,
  confidentiality or figures differ. The Hub never checks a signature: it says the report
  declares one, and prints the `verify-hash` command with `ExpectedIdentity` and
  `ExpectedIssuer`.
- The Disclosures tab lists them by period, with who checked what, the files, `SHA256SUMS`, the
  method and the disclaimers. It needs `disclosures.read`, and sharing, withdrawing, retrying
  and rendering need `disclosures.manage`. Of the built-in roles only `admin` carries them, a
  configured role carries them on `*` only, and a machine token never holds them.
- Share links serve a public disclosure's files at `/share/<token>/<name>` for 1 to 30 days,
  anonymously, the token shown once and stored as its SHA-256, so
  `verify-hash --url https://hub/share/<token>/report.json` works from anywhere. A withdrawal
  is final and kills every link.
- Webhooks receive a `published` and a `withdrawn` event, signed with HMAC-SHA256 over the
  timestamp and the body, optionally with a share link of their own, retried for 8 attempts.
  Their URLs are never stored, shown or logged.
- `perf_sentinel_hub_disclosure_import_rejected_total{reason}` and
  `perf_sentinel_hub_disclosure_deliveries_failed_total`, published while disclosures are on.
  Schema V13 keeps the files in the database, so the backup carries them, and the chart takes
  the whole section as `hub.disclosures`, its keys and webhook URLs from Secrets.

### Changed

- The image ships perf-sentinel `0.31.0` as its analysis engine, repinned by digest from
  `0.30.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.30.0` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  `0.31.0` brings what the disclosures and the ack relay above run on: `disclose-pdf`, which
  checks and renders a published report, and `report --hub-relay`. It also takes the
  `--pg-stat-selector` and `--mysql-stat-selector` the `Selector` settings pass. No detector,
  normalizer or finding signature changes, so every stored finding keeps its signature.
- An analysis is refused with the new `hub_memory_low` when the container has too little memory
  left, rather than getting the whole Hub OOM-killed: the analysis binary runs inside the Hub's
  container, and a Hub already at 190 MB of a 256Mi limit was killed rendering an 8 MB report,
  every request in flight with it. Before each engine run the Hub reads its cgroup v2 headroom,
  the reclaimable page cache counted as free, against `Hub:Analysis:MinFreeMemoryMiB`, 256 by
  default, 0 turning it off. A container without a memory limit is not checked.
- The chart's memory limit is `1Gi` by default instead of `256Mi`, to hold the Hub, an analysis
  and the database's page cache. Requests stay at `64Mi`.
- The chart exposes `hub.analysis.minFreeMemoryMiB` and `hub.newSignaturesPerHourWarning`. The
  signature-rate threshold 0.6.0 added could only be changed through the environment.
- The chart's probes are values: `probes.startup` allows 5 minutes by default (5 seconds times
  60) instead of 60 seconds, for a first start that migrates a large database, and
  `probes.liveness` allows 5 seconds and 6 misses instead of 1 second and 3. `probes.readiness`
  keeps Kubernetes' own timings.

### Fixed

- On a phone, the tab row scrolls to the tab of the open screen. It scrolls on its own below
  760 pixels, so a screen opened by a link or a reload had its tab off to the right, hidden or
  half cut.
- A Hub on a large database no longer restarts in a loop. Its start derived, in one
  transaction before it answered, the attribute rows that copies written by a build older than
  0.4.0 lack, which scanned every copy: 41 to 53 seconds on a 1 GB database, against a 60-second
  startup probe, and a kill rolled it back so every start began again. The derivation now runs
  after the Hub answers, in the retention worker's first pass, and takes the write lock only when
  a read-only check finds something to write.
- An analysis page no longer says "No analysis with that ID." when the Hub merely failed to
  answer. Any failed read of a run, a 503 while the Hub restarted included, read as an unknown ID
  and stopped the page from asking again, so a run interrupted by that restart never showed as
  such. Only a 404 now means an unknown run. Any other failure keeps what the page last read,
  says the Hub is not answering, and asks again after 1 second, doubling up to 15.
- The `/metrics` findings count runs on a thread of its own. SQLite runs its Async methods
  synchronously, so a count of a minute held a thread pool thread for that minute, which a
  liveness probe could then wait behind.

## [0.6.0] - 2026-10-16

### Added

- `perf_sentinel_hub_database_bytes` publishes the database file and its write-ahead log,
  read from the file system, and `perf_sentinel_hub_findings_age_seconds` the age of the
  counts behind `perf_sentinel_hub_findings`.
- A daemon that first sees more than `Hub:NewSignaturesPerHourWarning` signatures in an hour,
  1000 by default, is flagged on `/api/sources` (`new_signatures_last_hour`,
  `signature_rate_warning`), in the launcher's health column and once in the logs. A producer
  that puts something unique per trace into a signature filled a 1Gi volume in 17 hours.

- A Correlations screen and `GET /api/correlations` keep the periods during which a pair of
  findings kept following each other, as episodes a daemon on perf-sentinel 0.30.0 or later pushes
  to the new `POST /api/import/correlations?source_id=<id>` with `[daemon.correlation]` and
  `[daemon.hub_export]` on. The daemon cuts each episode, the Hub merges its copies in any order
  and purges it with the findings retention. A pair unfolds into a strip of its episodes beside the
  daemon's incidents, an episode hands its window to New analysis, and an unfolded incident lists
  the correlations active during it. The import takes the same key and gate as the two others, at
  most 100 episodes and 1 MiB per request, and refusals count on
  `perf_sentinel_hub_correlation_import_rejected_total{reason}`.

### Changed

- The image ships perf-sentinel `0.30.0` as its analysis engine, repinned by digest from
  `0.29.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.29.0` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  `0.30.0` changes one family of signatures, once: an `excessive_fanout` whose parent span
  is outside the trace no longer carries that span's id, so a run finds the same fanouts
  under one stable signature each instead of one per trace. Its other change sits in the
  daemon: with `[daemon.hub_export]` and `[daemon.correlation]` both on, a daemon pushes its
  correlation episodes to `/api/import/correlations`.
- A `/metrics` scrape waits at most five seconds for the findings counts and is otherwise served
  the previous ones while the count finishes. A store of millions of signatures made the count
  outlast a 10-second `scrape_timeout`, and every family of the Hub went missing with it. The
  count runs on no request's token, so it lands even when the scraper gave up, one at a time,
  and pauses nine times its own duration when slow: a cancelled count used to start over on
  every scrape and kept the database read from disk, slowing the findings imports behind it.
- A poll that fails because the Hub's own volume is full now records `database_full` instead of
  `storage_error`, and the launcher shows `Hub database full` rather than the daemon as
  unreachable. Any other storage failure keeps `storage_error`, now explained as the Hub's.
- The first start after the upgrade indexes `finding_sources` by source and first sighting
  before the Hub answers. On a large database it takes time in proportion to the rows and
  needs room for the index on the volume: grow a full volume before upgrading.
## [0.5.0] - 2026-10-15

### Added

- `POST /api/import/incidents?source_id=<id>` takes the incidents a daemon on perf-sentinel 0.29.0
  or later pushes beside its findings, with the same key and gate, at most 10 incidents and 4 MiB
  per request. An incident its ring evicts between two hourly polls now reaches the Hub. The poll
  still reads incidents, and both paths merge under one rule. Refusals count on
  `perf_sentinel_hub_incident_import_rejected_total{reason}`.

### Changed

- The image ships perf-sentinel `0.29.0` as its analysis engine, repinned by digest from
  `0.28.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.28.0` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  `0.29.0` changes no detector, normalizer or signature, so a run finds what it found on
  `0.28.0`, with the same signatures, and acknowledgments keep matching. Its change sits in
  the daemon: with `[daemon.hub_export]` and `[daemon.incidents]` both on, a daemon pushes
  its incidents to the new `/api/import/incidents` beside its findings.
- An incident's frozen findings open worst first, then most seen, where they followed their
  first sighting, and every column but Ack sorts on a click or a keypress on its head. The
  sorted column says so to a screen reader. A Template column shows the normalized query
  each finding was raised on, cut to one line and whole in its tooltip: on a lab OOM kill,
  72 findings over 12 templates had read as rows that repeated word for word. A field above
  the table narrows them by endpoint or template as you type.
- "Analyse this window" is a link, so it opens in a new tab too. A finding first seen after
  a deploy reads "after the deploy", and after a memory saturation "after the incident",
  where both read "after the restart".
- Where the Hub cannot acknowledge a finding, the ack page prints the daemon's own
  `perf-sentinel ack --daemon <url> create` command for it, and each disabled source checkbox
  is described by the reason in its row.
- The Incidents filter line keeps kind and service in sight and folds namespace,
  environment and daemon behind More filters, which says how many of them are set.
- The ack page reads the chosen expiry back in UTC, "Holds until 2026-11-30 23:59:59 UTC.",
  beside the date the browser shows in its own locale.
- The range picker opens and closes without drawing the form again, and on a phone its
  quick ranges sit under the dates instead of beside them.
- Between 761 and 1400 pixels wide the tabs take the 12-pixel padding meant for them, which
  a later rule overrode.
- A source's or an incident's detail is held to the visible width at every screen size,
  where it was held only under 761 pixels and cut at the edge up to about 1,130.
- The four cost tiles sit four in a row, or two by two below 1,180 pixels, never three and
  one. A daemon selected on New analysis explains itself in one sentence, so Run analysis
  stays nearer the top.
- A "?" is 18 pixels with a 26-pixel target and a 3:1 ring, where it was 16 pixels with a
  ring under 3:1. A source on New analysis is named by its name and health, its chips read
  as a description, and an incident's row button names its kind and age beside the service.
- A finding's status reads "not observed", not `not_observed`. An unknown report link no
  longer credits an engine with a report that does not exist, nor repeats All analyses in
  its bar.

### Fixed

- On a phone the ack page fits the screen: its finding card and source table pushed it
  100 pixels past the edge.
- The sign-in page shows the Hub's logo in the dark theme. A rule hiding the dark logo came
  after the one showing it and won, so the page read "HUB" alone.
- The sign-in page sets the launcher's typeface, DM Sans at 400 and 600, written into its
  own stylesheet as `data:` fonts. Its policy gains `font-src data:` and the page still
  loads nothing, so it opens while the provider is down.

## [0.4.1] - 2026-10-15

### Changed

- The image ships perf-sentinel `0.28.0` as its analysis engine, repinned by digest from
  `0.27.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.27.0` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  `0.28.0` changes no detector, normalizer or signature, so a run finds what it found on
  `0.27.0`, with the same signatures, and acknowledgments keep matching. Its changes sit in
  periodic disclosure, which the Hub does not run yet: a signed disclosure's attestation now
  names the report's content hash, an official `disclose` refuses a period its archive no
  longer reaches, and a daemon with `[daemon.archive]` writes daily rollups that hold a
  quarter. A polled daemon's `/api/status` gains a `disclosure_archive` field the Hub
  ignores. The engine also moves to `rustls` 0.23.46 against RUSTSEC-2026-0346 to 0349.
- The launcher's files and the reports are compressed, Brotli or gzip at their fastest
  level, for a browser that accepts it. A report is megabytes of repetitive HTML: one of
  8.5 MB measured on a real daemon falls to under 800 KB, and its download from about
  5 seconds to well under one on the same link. `text/html`, CSS, JavaScript and SVG only:
  JSON answers stay plain, since one may carry a token next to reflected input, the pair
  BREACH reads over HTTPS.
- The report screen says "Loading the report", under a thin band sliding along its top
  edge, until its frame has loaded, where it showed an empty page. With reduced motion
  the band holds still.
- On the Incidents screen, the service and namespace filters wait for the first read of the
  daemons, disabled, instead of opening on their "every" entry alone and filling under the
  pointer when the read lands. The namespace filter holds its place meanwhile, so the line
  no longer shifts, and is dropped as before when no incident carried a namespace.
- The screens open on a functional heading and a single sentence. Incidents and Recent
  analyses lose the paragraphs that came before their tables, the rest of the Incidents
  text moves behind a "?" beside it, the Sources footer no longer repeats that an
  environment is declared, and the status legend on Recent is gone, since every card
  carries its own.
- A filled button, Run analysis first among them, has a dark green face in the light theme
  and a light green one with a dark label in the dark theme. White on the brand green read
  3.13:1, under what a 14px label needs.
- On a phone the four tabs fit the screen, the top bar scrolls away with the page, the
  footer no longer sticks to the bottom, a source's or an incident's detail stays within
  the screen's width, and an incident's findings table needs 640 pixels where it needed
  1,090.
- The Sources screen's Last success and Unreachable for columns tick with the rest of the
  screen instead of keeping the age they had when it was drawn.
- The quick ranges stop at 7 days. Past that a scan rarely finishes inside the timeout, and
  a longer window stays one custom range away.

### Fixed

- A report link whose report expired, failed, has not finished or does not exist opens on
  a page that says which, with Run it again with the same parameters, where it opened on
  an empty frame. A run ID the Hub does not hold gets a heading and a way on.
- An address the launcher has no screen for, `#/access` with access control off among
  them, says so, where it opened New analysis without a word.
- Run it again, on a failed, expired or empty run, opens New analysis filled with the run's
  source, service, window, trace cap and thresholds, where it opened a blank form.
- An incident's findings carry an Ack link only when a source in the Hub holds an ack
  credential, and each link is named after its finding. The ack page has a way back, and
  when no source carrying the finding can take an ack it says so, without a form whose
  every control is disabled.
- A daemon reads advised only for the hints that ask something of the operator,
  `ingestion_drops` and `tuning`. A `cold_start` or a `snapshot_scope` hint, drawn muted
  and blue, no longer turns the verdict amber.
- Form fields are labelled, so a screen reader names them and a click on the label focuses
  the field. Section titles are headings. A "?" is named after what it explains, its tip
  closes with Escape, and the range picker hands the focus back to its button when it
  closes. The radio groups move with the arrow keys.
- The `/device` page sets the launcher's typefaces. Its policy admits `data:` fonts, which
  is how `fonts.css` embeds them.

## [0.4.0] - 2026-10-14

### Added

- `GET /api/findings` takes `grouping`, the daemon's filter of that name: an exact match on
  a finding's effective grouping value, by default its Kubernetes namespace. The signature
  is grouping-blind, so the Hub now records every grouping a source reported a finding in,
  in a `finding_groupings` table that retention purges on its own `last_seen_ms`, and the
  filter matches any of them from a source in scope. A daemon's push coalesces a signature
  across groupings, so the poll, which reads one row per grouping, is what records each of
  them. On the upgrade each source's own stored copy is credited with the grouping it
  names; a row without its own copy is not credited with the shared one, which may be
  another source's, and an envelope that is not valid JSON is skipped. Blank reads as
  absent, like the other filters, so a dashboard's "All" choice lists everything.
- `Hub:Auth:RequiredGroups` (Helm `hub.auth.requiredGroups`) admits only members of the
  provider groups it lists, declared as configuration rather than in a screen. At sign-in
  the Hub reads the userinfo field `Hub:Auth:GroupsClaim` names, `groups` by default, as a
  list of strings or a single string, and lets the user in when one entry equals one of
  the required groups, exactly and case included. A userinfo without the field, or naming
  none of them, fails the sign-in with the same `403` as any other refusal, and event
  `1900` names the user and which case it was, never their groups. Membership is read at
  sign-in only, so with groups set the session is no longer renewed while in use: it ends
  8 hours after sign-in, and a member removed at the provider keeps access at most that
  long. The list itself is checked on every request: a session records the group that
  let it in, so turning the gate on, or dropping a group from it, ends the sessions it
  no longer covers at the restart that deploys it rather than up to 8 hours later.
  Empty, the default, the field is not read and the session is renewed as before.
  Set while `Hub:Auth:Enabled` is off, the Hub refuses to start rather than leave a gate
  that checks nobody, and the chart's schema refuses it too. Keycloak needs a Group
  Membership mapper with Add to userinfo on, GitLab's userinfo carries `groups` already,
  and Entra ID's and Google's carry no group, see `docs/AUTHENTICATION.md`.
- The Hub records, per source, every grouping attribute a daemon captured on a finding,
  each under the grouping identity of that observation, in a `finding_attributes` table,
  the service each source reported in a new `finding_sources.service` column, and the pairs
  every frozen finding of an incident copy shares in an `incident_attributes` table. Schema
  version 9. Nothing reads them yet and no answer changes: they are recorded ahead of
  access control, which will match projects on them, so that turning it on does not start
  from an empty history. Retention purges attributes on their own `last_seen_ms` and an
  incident's pairs with the incident. Every start fills in, from each source's own
  envelope, the attributes and the service an older build left out, and the pairs of every
  incident copy an older build wrote, so an image rolled back and forward again is covered
  at the next start. The first start after the upgrade does it for every row: 2.3 s on
  100,000 finding copies and 10,000 incidents of 20 frozen findings, and 6.5 s with 1,000
  incidents of 1,000, against 0.14 s and 0.11 s for every later start, see
  `docs/BENCHMARKS.md`, which also says what to do when the chart's 60 s startup probe is
  too short for it. An attribute reaches the Hub only from the daemons whose
  `[detection] grouping_attributes` lists it, see `docs/DEPLOYMENT.md`.
- `Hub:Access` (Helm `hub.access`) describes access control as configuration: projects
  matched on a source, a service or a captured grouping attribute, exact or by prefix;
  roles over the permissions `findings.read`, `traces.read`, `sources.read`,
  `analyses.run` and `acks.write`, beside the built-in `reader`, `member` and `admin`;
  grants of a role on a project or `*` to a group; internal groups of subjects; admins by
  subject or by group, provider groups included; and machine tokens for CI jobs, each
  naming the Secret that holds it and expiring within 365 days. Off, the default, nothing
  under `Hub:Access` is validated or used and every answer is unchanged; the values are
  still bound, so a machine token's `ExpiresAt` must be an ISO 8601 date. On, every rule
  is checked at startup, each message naming its key and none echoing a token: a machine
  token's group holding no person, the `admin` role granted on `*` only, `access.admin`
  never in a configured role. A provider group in `AdminGroups` or a grant must be spelled
  as the provider sends it and be a name only the organisation can create, and on GitLab
  needs `Hub:Auth:RequiredGroups` as well, see `docs/CONFIGURATION.md`. The chart passes
  the block to the Hub as is, and refuses a duplicate or malformed machine-token name, a
  missing Secret reference and an `expiresAt` that is not an ISO 8601 date and time.
- Access control is enforced with `Hub:Access:Enabled` (Helm `hub.access.enabled`), see
  the new `docs/ACCESS-CONTROL.md`. Every route but the import, `/health` and `/metrics`
  (anonymous, on `Hub:MetricsPort` only) then needs a browser session or a machine token,
  and each answer covers what the caller's grants cover. `/api/findings`, `/api/findings/{traceId}` and the incidents are filtered per
  source copy, before paging, with `sources[]`, `acks[]`, statuses and filters judged on
  the copies in scope; a listing whose permission is held nowhere answers `403`, so a CI
  gate whose token has no grant fails. `/api/sources` lists only the sources where the
  caller holds something, with a `permissions` array. A record outside the caller's read
  scope answers the `404` a missing one gets, writes included, and a readable one it may
  not write `403`. An ack needs `acks.write` at the source before a relay slot is taken,
  then on the finding's copy at that source; an ack made through an attribute grant has no
  forced expiry and may silence the same finding for another team's deployment at that
  daemon. A run's row and its report follow one rule: a daemon run needs `findings.read`
  and `sources.read` at the daemon, a trace-backend run `traces.read` on the requested
  service, or at the backend for a trace ID, and a trace report shows every service its
  traces cross. The daemon view needs `sources.read` at the source as a whole. Reports the
  Hub renders are static, since a live report would ack around `acks.write`. A browser
  write must carry `Sec-Fetch-Site: same-origin`, and every response carries
  `Content-Security-Policy: frame-ancestors 'self'`. The launcher says "No grant on this
  Hub reaches you" when `/api/status` answers `403`.
- Machine tokens: `Authorization: Bearer <token>`, compared by SHA-256 fingerprint in fixed
  time, read from `Hub__Access__MachineTokens__<index>__Token`, which the chart fills from
  each token's Secret. A token holds `findings.read` at most, whatever its grants: any
  write gets `403`. An unknown or expired token gets `401` with
  `WWW-Authenticate: Bearer error="invalid_token"`, even next to a valid cookie, so a proxy
  in front of the Hub must neither inject nor forward `Authorization`. An expired entry is
  logged at startup (event `2001`) and never stops the Hub. Revoke by removing the entry;
  rotate by updating the Secret and restarting the deployment, see `docs/OPERATIONS.md`.
- `Hub:Auth:SubjectClaim` (Helm `hub.auth.subjectClaim`, default `sub`), read only with
  access control: the userinfo field that is the authorization key, one of `sub`, `oid`,
  `uuid` and `account_id` (`account_id` for Bitbucket Cloud). `IdentityClaim` stays the
  name shown and is never compared. With access on, a sign-in without a usable subject, or
  whose subject or identity starts with `local:` or `machine:`, is refused (event `1900`);
  the session keeps only the provider groups a grant or `AdminGroups` names, and logs the
  others at Debug (event `1902`); `Hub:Analysis:IdentityHeader` is never read; and the
  session ends 8 hours after sign-in, as under `RequiredGroups`. A session opened before
  access was turned on signs in again.
- `Hub:MetricsPort` (Helm `hub.metricsPort`): `/metrics` answers on that port alone, and
  nothing else answers there. Required with access control on. The chart adds it to
  `ASPNETCORE_HTTP_PORTS`, the container and the Service (port `metrics`) and refuses
  `8080`; outside Helm, set `ASPNETCORE_HTTP_PORTS="8080;<port>"`. A Hub whose metrics port
  is not bound beside another stops at startup (event `2002`, exit code 1).
  `examples/prometheus-scrape.yml` now targets port 9464.
- **Prerequisite before turning access on:** JetBrains plugin 0.2.0, which sends a
  credential per endpoint (robintra/perf-sentinel-jetbrains-plugin#31), on every IDE that
  reads the Hub; an older plugin gets `401`. 0.2.0 is not on the Marketplace yet, so
  turning access on before it ships leaves every IDE with `401`. Grafana's Hub
  datasource needs a machine token. `Hub:Auth:RequiredGroups` is recommended alongside.
- **Rollback:** with access on, never roll the image back below this release. Earlier
  builds ignore `Hub:Access` and fail open: `/api/findings` is anonymous again and every
  session sees every record (`RequiredGroups` still holds). Set `hub.access.enabled: false`
  first, as a deliberate decision, then roll back.
- Your access and Coverage. `GET /api/access`, mapped only with access on (`404` otherwise), tells
  any caller, one no grant reaches included, their identity, their id (the subject an
  administrator puts in `Admins` or `Members`), how they are signed in and until when,
  their groups, and what each of their grants gives them. An admin also reads the whole
  access configuration, with machine tokens by name, group and expiry and never their
  value or fingerprint; the services, attribute values and sources no project covers
  within `Hub:Retention`, so a newcomer is spotted and assigned in the configuration; and
  the copies whose groupings disagree on a project attribute, where an attribute grant
  reads but cannot acknowledge. Each admin list stops at 200 entries and says so. The
  launcher shows it on two read-only screens; rights still change in the configuration
  only. **Your access**, opened from the identity chip, holds the caller's own part and
  terminal tokens. **Coverage**, a tab for admins alone, starts with what no project
  covers and one quoted YAML pattern per entry to paste into `hub.access.projects`, then
  the conflicts, everyone's terminal tokens and the configuration folded by part. A caller
  no grant reaches gets Your access on every route, under the no-grant banner, with no tab
  and no version chip, and a block to copy that holds what an administrator needs. With
  access on, the New analysis form offers only the sources where the caller holds
  `analyses.run` and says how many it leaves out, and a daemon row unfolds only where they
  hold `sources.read` and says so under its name otherwise.
- Terminal sign-in, with access on. `perf-sentinel login` (perf-sentinel 0.27.0 or later)
  gets a personal token through the Hub's own device flow (RFC 8628): `POST /auth/device`
  hands the CLI a code, the developer types it on the Hub's `/device` page in a signed-in
  browser, sees the address and time it was requested from, is warned and asked for a
  second click when that is another network than the browser's, and approves;
  `POST /auth/device/token` then returns a `pshub_` token. There is no
  `verification_uri_complete`, so no link can carry a code. Only a browser session
  approves, lists or revokes, never a bearer. The token carries the approving session's
  subject and provider groups, frozen, and the `RequiredGroups` group that admitted it; it
  is refused once that group leaves the list, is never admin even when an admin approves
  it, and expires with the approving session (8 hours at most, at least 1 hour: a session
  closer to its end signs in again first). At most 10 per person, the 11th dropping the
  oldest. Only its SHA-256 is stored, in the new `terminal_tokens` table (schema version
  10), which retention purges once expired. `POST /auth/revoke` (RFC 7009) revokes by
  possession. Your access lists the caller's tokens and Coverage everyone's, each with a
  Revoke that asks for a second click (`GET /api/tokens`, `DELETE /api/tokens/{id}`). Pending codes live
  in memory: at most 200, 10 per client address (an IPv6 client counts by its /64), 10
  minutes each, polled every 5 seconds with `slow_down`; ten bad codes in 10 minutes pause
  a person's approvals. Events `2010` to `2015`.
- `Hub:Auth:TrustedProxyNetworks` (Helm `hub.auth.trustedProxyNetworks`): the CIDR
  networks whose `X-Forwarded-For` and `X-Forwarded-Proto` the Hub trusts, one hop deep.
  Empty, the default, changes nothing: the scheme is trusted from any peer and the client
  address from none. Terminal sign-in needs it, since behind an ingress every client
  would otherwise share one address: with access on and the list empty, the device-flow
  routes are not mapped and event `2014` says why at startup, while token listing and
  revocation stay available. A Hub with no proxy in front lists `127.0.0.1/32`.
- Local break-glass accounts, with access on, for when the provider is down:
  `Hub:Access:LocalAccounts` (Helm `hub.access.localAccounts`), each `{ Name,
  PasswordHash }` with the hash delivered from a Secret. With one declared, a page
  request without a session goes to the Hub's own `/auth/sign-in` page instead of the
  provider: **Sign in with single sign-on** first, the local form folded below it. The
  page contacts nothing, so it opens while the provider is down; the API still answers
  `401`. An account signs in as `local:<name>` for a fixed 8 hours, reaches what the
  internal groups listing that subject and `Admins` give it, and is not subject to
  `Hub:Auth:RequiredGroups`. Its session carries a stamp of the hash, so a new hash ends
  it at the restart that loads it, and a removed account at its next request. The hash is
  a PHC string, PBKDF2-HMAC-SHA256 at exactly 600,000 iterations with a 16-byte salt,
  printed by the new `PerfSentinelHub hash-password` command, which reads the password
  from stdin or a prompt and never from an argument; passwords hold 15 to 1,024
  characters. One password is hashed at a time, an unknown name is hashed against a dummy
  and gets the same `403` byte for byte, a client address and name back off from 30
  seconds to 15 minutes after five failures with no lock, and one address hashes at most 5
  times a minute. The Hub refuses to start when an account is listed by no group and not
  in `Admins`, or when `Hub:Auth:TrustedProxyNetworks` is empty, and the chart's schema
  refuses the latter. Every local sign-in logs event `2020` and every refusal `2021`, both
  at Warning, never with an undeclared name. Coverage lists the accounts by name to an
  admin (`local_accounts`). The sign-in page carries the launcher's logo and palette,
  inline, since its policy loads nothing. With no local account declared, or access off, nothing
  changes.

### Changed

- The image ships perf-sentinel `0.27.0` as its analysis engine, repinned by digest from
  `0.26.2`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.26.2` or earlier now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  `0.27.0` changes no detector, normalizer or signature, so a run finds what it found on
  `0.26.2`, with the same signatures, and acknowledgments keep matching. A daemon on
  `0.27.0` can put every read of its query API behind a key with `require_read_key`, and a
  daemon source whose daemon turns it on needs `AuthHeaderName=X-API-Key` and
  `AuthHeaderValue=<read_api_key>`, or its poll gets `401` and the source shows
  unreachable. `perf-sentinel login --hub` in `0.27.0` is the CLI side of this release's
  terminal sign-in.

- The `perf-sentinel query monitor` command printed under a daemon whose published header
  is `X-API-Key` now says to set `PERF_SENTINEL_DAEMON_API_KEY` to the daemon's read key
  first, since the monitor sends that variable as that header. It said the command worked
  only from somewhere that reached the daemon without the header. Any other header, an
  Ingress's own for one, is still named as one `query monitor` cannot send.
- A findings poll, a findings import or an incidents read whose body repeats a property
  name within one object is refused as invalid JSON, a `400` for an import. The Hub reads
  a finding's grouping with System.Text.Json, which keeps the last of the two, and its
  attributes with SQLite, which keeps the first, so the two would disagree. The engine
  never repeats one.
- The `403` a refused sign-in answers now reads "Sign-in refused. Open the Hub's home page
  to try again, or ask the Hub's administrator for access.", since retrying is no use to a
  user outside the required groups, and the line still does not say which case it was.
- The launcher names each screen in the browser tab and, on every route change after the
  first screen, moves the focus to its heading, so a screen reader announces where the
  reader landed. Between 761 and 1400px the tab icons and the theme's label give way so
  the bar keeps one row, and below 760px the tabs take a row of their own that scrolls
  sideways, where the page itself used to: at 390px every screen was 84 to 232px wider
  than the viewport. Below 760px a long inline code value breaks rather than widening the
  page. Labels that were 9.5 to 10.5px are now 11px; only the glyphs inside the help dots
  and the step numbers stay smaller, inside their fixed circles. The terminal approval page names the
  code's format, takes the focus in its field, moves it to the request once the code is
  verified, and announces the outcome. The monospace face falls back to SF Mono or Menlo
  before Courier where JetBrains Mono is not loaded.

### Fixed

- The example dashboard's `Fleet reachability` table listed every label of the scraped
  series, `pod`, `namespace`, `container` and whatever else the scrape adds, which pushed
  the source and its state off to the right. It now keeps one row per source,
  `min by (source)`, so a source reads unreachable whenever the `Unreachable daemons`
  count includes it. It shows two columns, `Source` and `State` (`État` in the French
  file), and the state is drawn in the red and green its value mapping always named but
  never displayed. Dashboard `version` 4.
- `examples/prometheus-scrape.yml` suggested telling two Hubs apart by a target label
  under one job. The example dashboard reads one Hub, the one its `Job` variable selects,
  so a second Hub scraped by the same Prometheus takes its own job instead, and
  `HubDown` a rule matching that job.

## [0.3.8] - 2026-10-09

### Added

- `examples/FR/grafana-dashboard-FR.json`, the example dashboard with French titles,
  descriptions and value labels. Queries, layout and colors are the English file's.

### Changed

- The image ships perf-sentinel `0.26.2` as its analysis engine, repinned by digest from
  `0.26.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.26.0` or `0.26.1` now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  `0.26.2` makes the engine's output reproducible: the same input gives the same findings,
  in the same order, with the same figures. No finding appears or disappears and no
  detector verdict changes. A cross-trace `slow_sql` or `slow_http` finding tied on
  duration now names the smallest trace ID, and its signature follows that trace's service
  and endpoint, so an acknowledgment taken on such a finding can stop matching once.
  Avoidable I/O now counts once per grouping and adds up across parameter sets, so on
  traces that span several groupings or repeat one query with several parameter sets, the
  avoidable I/O, waste ratio and avoidable CO2 of a Tempo or `jaeger_query` run rise and
  are not comparable with a run taken before.

  A daemon source's run reads its report from the daemon, so those figures, the order of
  its findings, acks and correlations, and the rejection at load of region keys that
  differ only in case, follow the daemon's version, not the Hub's. Once a daemon runs
  `0.26.2`, an ack listing past its 1000 cap names the same acks on every poll, so the
  Hub's mirror of it stops shifting between polls.

### Fixed

- The description of the dashboard's import rejection panel compared it with a panel
  beside it that does not exist. It now names `source_last_import_seconds`, which the
  dashboard leaves out on purpose.
- The chart's Service carries `app.kubernetes.io/name` and
  `app.kubernetes.io/instance` and names its port `http`, so a `ServiceMonitor` or
  a `VMServiceScrape` can select it, as `values.yaml` already suggested. `make
  helm-template` checks both.

## [0.3.7] - 2026-10-06

### Added

- The chart can publish the Hub outside the cluster through an Ingress. Set
  `ingress.enabled` and `ingress.host`, and optionally `ingress.tlsSecretName`,
  `ingress.className` and `ingress.annotations`. The Hub is served at the root of
  that host, the only address its launcher works at, and the chart refuses to render
  the Ingress without a host. The Ingress publishes every route, `/api/findings`,
  `/metrics` and `/health` included, which `Hub:Auth` leaves open, so the host
  belongs on a network allowed to read them. An upgrade with `--reuse-values` from
  values that predate the key renders no Ingress rather than failing. `make
  helm-template` renders the Ingress, checks the missing-host refusal, and renders
  values without an `ingress` key.

## [0.3.6] - 2026-10-04

### Changed

- The image ships perf-sentinel `0.26.0` as its analysis engine, repinned by digest from
  `0.25.5`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.25.5` now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  `0.26.0` changes no detector, normalizer or signature, so no polled finding appears,
  disappears or changes signature, and acknowledgments mirrored on a daemon keep
  matching. A new run's HTML report gains an `Energy` card in its Carbon tab, rendered by
  the embedded engine, and a report rendered before the upgrade keeps its old layout. A
  Tempo or `jaeger_query` backend analysis, which no energy backend covers, reads
  `modeled from I/O counts` there.

  The energy fixes, one Kepler zone instead of their overlapping sum, the Redfish chassis
  entry and `schema = "sensor"`, and the Alumet startup warning, run in the daemon, so
  they follow the daemon's version, not the Hub's. Once a daemon fed by Kepler runs
  `0.26.0`, the energy and carbon figures of a run on its snapshot drop, and are not
  comparable with a run taken before.

### Fixed

- A daemon's `// carbon scoring` card listed the window's `energy_model` as where the
  energy figure comes from, with a help text reading measured or estimated into it. That
  tag names the carbon intensity source or tier, so a daemon with Electricity Maps
  showed `electricity_maps_api` there whether a power backend measured anything or not.
  The card now shows `energy_source`, worded as perf-sentinel `0.26.0` words it from
  per-service coverage: `modeled from I/O counts`, `source <backends>`, or
  `source <backends> on N of M services · rest modeled from I/O counts`, followed by
  `· calibrated` when calibration factors rescaled the modeled part. The daemon view
  gains the `energy_source` field, null when the snapshot computed no energy, and keeps
  `energy_model`. A daemon older than `0.26.0` gets the same label, calibration read
  from the `+cal` suffix its tags carry.

## [0.3.5] - 2026-10-03

### Changed

- The image ships perf-sentinel `0.25.5` as its analysis engine, repinned by digest from
  `0.25.3`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a daemon still on `0.25.3` or `0.25.4` now reads behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  `0.25.4` changes how the CLI and the TUI print a finding's code location, never the
  finding itself: a qualified `code.function.name` no longer repeats its namespace, a
  namespace holding `\` or `::` joins its function with `::`, and an empty attribute
  leaves no stray separator. The Hub reads none of those outputs. An analysis run's JSON
  keeps `code_location` as the four fields the span sent, and the HTML report is rendered
  from that JSON by a template neither version touches, so a run's report is unchanged.

  `0.25.5` masks a value between double quotes on MySQL and MariaDB, where it used to
  keep it in the template, in clear. That holds in a Tempo or `jaeger_query` source's
  backend analysis, whose report shows the masked template, and in a polled daemon once
  that daemon runs `0.25.5`. A daemon finding whose SQL held such a value gets a new
  template and a new signature, so it arrives as a new row, and an acknowledgment mirrored
  on the old signature does not carry over to it. Queries that split into one finding per
  value now group, so findings on them can also appear or disappear.

  The new row fits the lineage probe, same service, detector and endpoint with another
  `template_hash`. Where the old template held one constant value, the old row is the
  lone candidate once the daemon stops sending it, and the new row links to it and
  serves its `original_first_seen`. Where the old rows were one per value, there are
  several candidates and the new row links to none. A lineage block dated to this upgrade
  on a MySQL or MariaDB SQL finding is worth reading with that in mind.

  The engine the launcher starts inherits the Hub's environment, and `0.25.5` reads
  `HTTPS_PROXY`, `ALL_PROXY`, `NO_PROXY` and `SSL_CERT_FILE` for its `https://` calls. A
  Tempo or Jaeger source behind a proxy, or with a certificate from a private CA, now
  works with those set on the Hub. A Hub that already carries `HTTPS_PROXY` now sends the
  engine's `https://` source calls through that proxy, so an in-cluster `https://` source
  belongs in `NO_PROXY`. `http://` sources stay direct. A `jaeger_query` source on Jaeger
  2.21 or later, which removed the v1 search, failed every run with HTTP 404, and now
  succeeds through the v3 search the engine falls back to.

  Signatures, endpoints and SARIF locations move only for the MySQL and MariaDB findings
  above, so every other finding keeps its row and the acknowledgments mirrored on it.
  Neither version adds a configuration key, so `DetectionOverrides` and the daemon view's
  defaults are untouched.

- The images move from Ubuntu 24.04 noble to 26.04 resolute: the build image to
  `sdk:10.0.401-resolute-aot`, the runtime image to
  `runtime-deps:10.0.12-resolute-chiseled-extra`, both pinned by digest in the
  `Dockerfile` and `config/supply-chain.json`. The SDK and the runtime stay on the same
  .NET release, so the lock and `global.json` do not move. What changes is the
  distribution under them, OpenSSL included: the Hub's outbound TLS, to daemons and trace
  backends, runs on OpenSSL 3.5 where noble carried 3.0.

### Fixed

- On the recent analyses screen, the legend sat flush against the banner above it, and
  the two banners, mixed engine versions and changed detection thresholds, sat flush
  against each other. A banner carries no margin of its own; both now stand 12px apart,
  the spacing the filter line under a banner already had.

- The Helm deployment assigns the pod's group `1654` as the volume `fsGroup`, allowing
  the non-root Hub process to create its SQLite database on a newly provisioned PVC.

## [0.3.4] - 2026-09-28

### Changed

- The image ships perf-sentinel `0.25.3` as its analysis engine, repinned by digest from
  `0.25.2`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a fleet still on `0.25.2` now reads one patch behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  `0.25.3` changes the fix a Java finding suggests, never the finding itself. A SELECT
  that Hibernate generated reads `java_jpa` even when no span names Hibernate, which
  covers the lazy loads the OpenTelemetry Java agent never wraps in a Hibernate span, and
  a service traced through Micrometer Observation gets the Java generic fix where it got
  none. The HTML report of an analysis shows the new fix. The suggested fix is not part
  of the signature, so every finding keeps its row, its lineage and the acknowledgments
  mirrored on it, and no finding appears or disappears.

  The two other changes of `0.25.3` are daemon start-up logs, a chmod warning on an ack
  store at an `fsGroup` volume root and advisories printed twice, which the Hub's own
  analysis runs never meet. `0.25.3` adds no configuration key, so `DetectionOverrides`
  and the daemon view's defaults are untouched.

### Fixed

- The page title and the wordmark name the product `Perf Sentinel Hub`, where they read
  `perf-sentinel hub`. The logo's alternative text reads `Perf Sentinel`.

## [0.3.3] - 2026-09-25

### Changed

- The image ships perf-sentinel `0.25.2` as its analysis engine, repinned by digest from
  `0.25.1`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a fleet still on `0.25.1` now reads one patch behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  `0.25.2` reads the `method` and `status` tags that Micrometer Observation puts on the
  outbound HTTP spans of a Spring Boot service, where it read every such call as a `GET`
  without a status. That holds in a Tempo source's backend analysis, in a `jaeger_query`
  source's, and in a polled daemon once that daemon runs `0.25.2`. On such a service, a
  finding on a call that is not a `GET` now names its real verb in its template, so it
  arrives under a signature the Hub has never seen, as a new row, and an acknowledgment
  mirrored on the old `GET` signature does not carry over to it. The `GET` row keeps its
  signature, with fewer occurrences where the other verbs had counted in it.

  The new row fits the lineage probe, same service, detector and endpoint with another
  `template_hash`, while the `GET` row it would link to is still current. Stored before
  that `GET` row in the same batch, it links to it and serves the `GET` row's
  `original_first_seen`. Stored after, it links to nothing, because the `GET` row was
  just seen. The order of findings within a batch is not a contract, so either can
  happen, and a lineage block dated to this upgrade on a non-`GET` HTTP finding is worth
  reading with that in mind.

  `0.25.2` adds no configuration key, so `DetectionOverrides` and the daemon view's
  defaults are untouched.

## [0.3.2] - 2026-09-24

### Changed

- The image ships perf-sentinel `0.25.1` as its analysis engine, repinned by digest from
  `0.25.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a fleet still on `0.25.0` now reads one patch behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  `0.25.1` counts a connection pool's validation ping, the empty query the OpenTelemetry
  JDBC instrumentation traces with `db.statement=""`, as `not_io` instead of a missing
  statement. The Hub reads neither counter, so what moves in its data is narrow: a ping
  whose span name read as a query execution no longer borrows the statement of a related
  span, in a Tempo source's backend analysis as in a polled daemon, so the occurrence count
  of such a finding can drop by the pings it held. Its signature does not move. A
  `jaeger_query` source's analysis does not go through that ingest path.

  `0.25.1` adds no configuration key, so `DetectionOverrides` and the daemon view's
  defaults are untouched.

### Fixed

- A run submitted right after the Hub started could be marked `interrupted`, as if the
  previous process had lost it. At startup the Hub interrupts the runs a previous process
  left running, and it did so from the analysis worker's background loop, which the host
  does not wait for, so the listener could already have accepted a run by then. It now
  does so before the listener opens.

## [0.3.1] - 2026-09-23

### Changed

- The image ships perf-sentinel `0.25.0` as its analysis engine, repinned by digest from
  `0.24.0`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a fleet still on `0.24.0` now reads one minor behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  A backend analysis gives the findings and signatures it gave on `0.24.0`, since nothing
  in `0.25.0` touches detection or scoring. What reaches the Hub's data comes from the
  daemon: a `0.25.0` daemon freezes into an incident only the findings of that incident's
  namespace, where it froze those of the same service in every namespace, as long as
  `k8s.namespace.name` is among its `[detection] grouping_attributes`, as it is by
  default. The incident mirror copies what the daemon froze, so an incident polled from
  such a daemon carries fewer findings, and one it recorded before keeps what it froze.
  The poll still asks for the frozen findings, so the new `findings=false` on the daemon's
  `/api/incidents` changes nothing here.

  `0.25.0` adds no configuration key, so `DetectionOverrides` and the daemon view's
  defaults are untouched.

## [0.3.0] - 2026-09-20

### Added

- `GET /api/findings` reads the history the Hub keeps, by environment and by period. The
  engine's findings dashboard reads it from perf-sentinel 0.24.0 on, so its table follows
  Grafana's time picker over the Hub's retention, where a daemon's in-memory ring cannot
  look back. `environment` and `source_id` scope the read to the sources of one environment
  or to one source. Both are closed sets, the Hub's configured sources, so a value outside
  them is a `400` rather than an empty page, and given together they intersect, the rule
  `/api/incidents` already follows. `from` and `to` bound the read to an observation
  window in epoch milliseconds, both inclusive, at the granularity of a UTC day on the
  Hub's clock: a finding is listed when a source in scope observed it inside the window,
  and the window selects rows without rewriting them. `offset` pages the rows, which come
  in a total order, `last_seen` descending then `signature`, and `signature` is an exact
  match, bounded at 1,024 characters and left to the daemon for its shape. A scoped answer
  describes its scope rather than the fleet. The daemon's document is the copy of the
  source in scope that saw the finding last, `first_seen` is the earliest and `last_seen`
  the latest over those sources, `status` is derived from that `last_seen` and from their
  heartbeats alone, and `sources` lists only them. `max_confidence` stays fleet-wide on
  purpose. Every entry of `sources` gains the source's `id`, the one `/api/sources` lists
  and `source_id` filters on. Behind it, schema `V6` records the days each source observed
  a finding in `finding_observations`, and `finding_sources` keeps each source's own copy
  of the envelope. Both start at the upgrade and nothing is backfilled. Before the first
  day recorded for a source, that source is assumed to have carried the finding since its
  `first_seen`, so an upgraded Hub does not read as empty over its whole past, and
  `docs/LIMITATIONS.md` says what that assumption costs.
- `/metrics` gains a ninth family,
  `perf_sentinel_hub_findings{environment,finding_type,severity,status}`, a gauge of the
  distinct finding signatures stored per environment. Its `status` is the environment's
  own, computed the way `GET /api/findings?environment=` computes it, so the series of one
  environment and status add up to the findings that read holds for them. `finding_type`
  and `severity` are free text a daemon sends, so a value outside the engine's twelve types
  or its three severities counts under `other` instead of opening a series of its own,
  which holds the family to 13 types by 4 severities by 3 statuses per environment. An
  empty combination publishes no series, since the family is read through `sum()`, where
  an absent series already is a zero. The counts are kept for 15 seconds, because the
  endpoint is anonymous and a count reads every finding of its environment. Every other
  family is still computed at scrape time. The example Grafana dashboard charts the family
  in two panels, stored findings by environment and status, and active findings by
  severity for the environment a new `environment` variable picks, drawn in the engine's
  severity colors and in whole counts. Dashboard `version` 3.
- The Hub mirrors the active acknowledgments of each daemon, schema `V7`. Every poll of a
  daemon on perf-sentinel 0.24.0 or later reads its ack listing, CI baseline included, into
  `source_acks`. A read replaces its source's rows whole, so a revoked ack leaves with the
  next one. A daemon below 0.24.0 is never asked, because it cannot list its baseline, and
  its findings are judged on their envelope as before. `GET /api/findings` serves the
  mirror as `acks`, one entry per source of `sources` whose daemon holds an active ack on
  the signature: the `source_id`, the `source` of the ack, `daemon` for one taken at
  runtime and `toml` for one of the CI baseline, then `by`, `reason`, `at` and
  `expires_at`. The field is absent when no source holds any, and the daemon's own
  `acknowledged_by` is still relayed verbatim beside it. `/api/sources` gains `acks_state`
  and `acks_read_ms`, what the last ack read came to and when, kept in an `ack_reads` row
  apart from reachability for the reason `incidents_state` is. The states are the same,
  plus `truncated` for a listing that reached the daemon's cap of a thousand acks, whose
  entries are served but which does not decide `include_acked=false`. The `by` and
  `reason` of an ack are readable on the open read route, as `acknowledged_by` already was.
- A source takes a second credential, `Sources[].AckHeaderName` and `AckHeaderValue` (Helm
  `sources[].ackHeaderName` with `ackSecretName` and `ackSecretKey`). It is the daemon's
  `[daemon.ack] api_key`, sent on a relayed ack or revoke and on nothing else, while the
  read pair keeps everything else. Without it the Hub never writes to that daemon, and
  `/api/sources` reports `ack_relay: false` for the source, neither the header nor its
  value being ever published. The Hub refuses to start when the pair sits on a trace
  backend, which has no ack route, or carries the same key as the source's
  `AuthHeaderValue`, since a read key that can write is a write key.
  `Hub:AckRelay:TrustIdentityHeader` (Helm `hub.ackRelay.trustIdentityHeader`), `false` by
  default, lets the relay name its caller from `Hub:Analysis:IdentityHeader`, for a Hub
  behind a proxy that writes that header itself and strips the one a client sent. A
  `Hub:Auth` session always names the caller and ignores the header. With neither, the
  relay refuses every caller, and the Hub logs one warning at startup naming the sources
  whose credential will never be sent.
- Two routes relay an ack or a revoke to the daemon behind one source,
  `POST /api/sources/{sourceId}/acks` with `{signature, reason, expires_at?}` and
  `POST /api/sources/{sourceId}/acks/revoke` with `{signature}`. Neither is ever open:
  under `Hub:Auth` they need a session, and a caller nobody names gets a `403` before the
  source is even looked at. An unknown source, a trace backend and a daemon without an ack
  credential answer one and the same `404`. The Hub has no antiforgery token and no CORS
  policy, so a `Sec-Fetch-Site` other than `same-origin` is a `403` and a content type
  other than `application/json` a `415`. Two relays run at a time, a third gets `503` with
  `Retry-After: 1`, and a body over 8 KiB is a `413`. The Hub relays only for a finding it
  holds at that source, and a revoke also for an ack it mirrors from it, which bounds the
  relay to what the Hub knows. It writes the daemon's request itself, so nothing the
  caller sent travels as bytes, a `by` in the body is ignored, and the caller the Hub
  knows travels as `by` on an ack and as `X-User-Id` on a revoke. A `2xx` answers `204`.
  The daemon's `400`, `409`, `503` and `507` keep their code with a `detail` saying why, a
  `404` on a revoke means not acked there or only by the CI baseline, and silence is a
  `504`. A `502` covers the rest, and its `detail` says which it was: a daemon that could
  not be reached, a status the relay does not know, or a daemon's `401`, never relayed as
  one because it says the key the Hub holds is wrong, which no sign-in mends. After a
  `2xx` the Hub reads that daemon's ack listing again, so `acks` and `include_acked=false`
  follow at once instead of at the next poll. Each relay logs one line, event `1310` when
  the daemon answered and `1311` when it did not, without the reason or the credential.
- The launcher gets an ack page, a sixth screen with no tab, reached by link only. Acks are
  made from the Grafana findings dashboard, the HTML report, the TUI or the CLI, and this
  page is where a Grafana link lands:
  `/?ack=<signature>&environment=<environment>&source_id=<id>`, the last two optional, a
  query rather than a hash because a hash is lost when the identity provider asks for a
  password on the way in, which the launcher turns into `#/ack?signature=<signature>` with
  the same two parameters on load. Each finding of an unfolded incident also ends with an
  Ack link, which names the incident's daemon as `source_id`. An ack hides a finding, so
  the link's context decides the default ticks: the source it names, else the sources of
  the environment it names, else a source that is alone in taking an action, and every
  other row stays tickable by hand. The page reads the finding by its exact signature,
  then offers one form, a required reason, an optional expiry day sent as the last second
  of that day in UTC, and one row per source that carries the finding. A row offers
  Acknowledge, or Revoke beside who took the ack, when and why, or both when the source's
  ack state is unknown, its last ack read not being conclusive, which includes every
  daemon below 0.24.0: such a source stays actionable because the relay would still
  answer, and the daemon refuses the button that does not apply. A row offers nothing,
  with a note saying why, for an ack of the CI baseline, which no runtime route can
  revoke, for a source with no ack credential, which is how a source is kept out of the
  page, and for a source the Hub no longer configures. One submit sends one relay request
  per checked source, one after the other, prints one line per source with the Hub's own
  reason on a refusal, and reads the finding again. One source refusing costs the others
  nothing.

### Changed

- The database moves to schema `V7` through `V6` at the first boot. Both steps only create
  tables and add nullable columns inside the startup transaction, and neither backfills
  anything, so an upgrade asks nothing of the operator. A read of `/api/findings` without
  the new parameters, and the lookup by trace beside it, answer what they answered before
  apart from two additive fields, `sources[].id` on every finding and `acks` on one a
  mirrored daemon holds an ack on. A test pins both responses byte for byte. The relay
  stays off until a source is given an ack credential.
- A filter given empty or as whitespace only reads as absent, which is what Grafana sends
  for its "All" choice: `service`, `finding_type`, `severity` and `status` on
  `/api/findings`, like the new `environment`, `source_id`, `signature`, `from` and `to`,
  and `service`, `namespace`, `kind`, `environment` and `source_id` on `/api/incidents`. It
  used to answer `400` on a closed set, `status`, `kind`, `environment` or `source_id`,
  and to match nothing on a free filter such as `service`, which emptied the table.
  `limit`, `offset` and `include_acked` are not filters, and a blank one is still a `400`.
- `include_acked=false` judges each source in scope that carries the finding, by
  whichever of its two views the Hub read last. The mirror decides when the last ack read
  of that source came to `ok` and is no older than its last observation of the finding,
  and otherwise the source's own copy of the envelope decides by a non-null
  `acknowledged_by`, which is all the Hub judged before. The finding stays listed while at
  least one such source holds it un-acknowledged. It used to be judged on the single
  envelope the fleet shares, so an ack taken in production hid the finding for staging
  too. It no longer does, in a scoped read or in one over the whole fleet.
- A source whose `Environment` carries a control character is refused at startup. An
  environment is now a Prometheus label value and a member of a closed set a query
  parameter is compared against, and the label is escaped on the way out besides.

### Fixed

- The container image serves the launcher. Its last stage copied the binary, the SQLite
  library and the engine, never `wwwroot`, so `/` and every page under it answered `404`
  in a chart or a `docker run` deployment while the API answered as usual. The CI image
  smoke now fetches `/` beside `/health/ready`.

## [0.2.1] - 2026-09-18

### Added

- `Sources[].PublicUrl` (Helm `sources[].publicUrl`), the address printed commands and live
  reports target when `BaseUrl` is a name only the cluster resolves. The Hub's own polls,
  reads and runs keep `BaseUrl`. Without it, nothing changes.
- `Sources[].PublicAuthHeaderName` (Helm `sources[].publicAuthHeaderName`), the header
  printed commands name when the public route authenticates differently from `BaseUrl`.

### Changed

- The example Grafana dashboard shows times in the viewer's browser time zone instead of
  UTC, like the launcher already does. Dashboard `version` 2.
- The image ships perf-sentinel `0.23.0` as its analysis engine, repinned by digest from
  `0.22.2`. It is also the version the launcher compares a polled daemon's
  `producer_version` against, so a fleet still on `0.22.2` now reads one minor behind.
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

  The change that reaches the Hub's own data comes from the daemon, not from this binary:
  a `0.23.0` daemon counts slow episodes across analysis batches, on by default, and
  reports `slow_*` findings it used to leave in its duration histogram. They carry the
  signature a batch run would give them, so they fold into a row the Hub already holds for
  that template and otherwise arrive as new rows with today's `first_seen_ms`. Expect more
  slow findings per source once it upgrades, not a change in how any row is stored.

  A report the Hub renders shows its times in the reader's browser time zone, as the
  launcher and the dashboard already do. For a daemon run, the source side of a
  correlation card opens Explain on its own trace when the daemon is on `0.23.0` too,
  since the field it reads is new there. The Hub reads the JSON the engine writes, never
  its terminal text, so the local-time `Window:` line changes nothing here.

  The new `[detection] slow_query_window_minutes` only acts in the daemon, and a backend
  analysis is a batch run, so `DetectionOverrides` is untouched and the launcher still
  gates `sanitizer_aware_min_cv` alone, on engines `0.18.0` or later. The daemon view's
  defaults are unchanged too: `0.23.0` adds nothing to the settings a daemon publishes.

## [0.2.0] - 2026-09-17

### Added

- The Hub can sign browser users in against an OAuth2 provider itself, so an operator who
  does not want to run an authenticating proxy in front of it no longer has to. `Hub:Auth`
  is off by default, and off nothing changes. On, the launcher, the API it calls and
  `/reports/` need a session, and the routes a machine calls stay open: `/api/findings` for
  IDE plugins and CI jobs, `POST /api/import/findings` behind its own `X-API-Key`,
  `/health/live`, `/health/ready` and `/metrics`. The provider is described by three
  endpoints, a confidential client and the userinfo field that names the user, which fits
  Keycloak, Entra ID, Google, GitLab and Bitbucket alike, and `docs/AUTHENTICATION.md`
  gives the values for each. It is built on the framework's own OAuth and cookie handlers,
  so no package is added and the NativeAOT publish stays free of trimming warnings. The
  browser goes through the authorization code flow with PKCE, the Hub keeps no token, and
  the session is an encrypted `hub_session` cookie, `Secure`, `HttpOnly` and
  `SameSite=Lax`, valid eight hours and renewed while in use, whose keys live in a `keys`
  directory next to `Hub:DatabasePath` so a restart signs nobody out. A page without a
  session redirects to the provider, and so does a shared report link opened on its own;
  the API and a report inside the launcher's frame answer `401` instead, since a fetch
  cannot follow a redirect to another origin, and the launcher reloads into the sign-in. A
  cancelled consent screen, or a userinfo without the configured field, answers `403` with
  a line pointing back to the Hub's home page and logs the reason as event `1900`, rather
  than failing with a 500. The signed-in user is what the topbar shows and what a run
  records as `requested_by`, and `Hub:Analysis:IdentityHeader` is ignored for a session,
  since any client can send it. `X-Forwarded-Proto` is honoured so the redirect URI reads
  `https` behind a TLS-terminating ingress. The chart gains a `hub.auth` block whose client
  secret comes from a Secret as `Hub__Auth__ClientSecret`, and its schema refuses
  `enabled` without the endpoints, the client id and that Secret.

## [0.1.10] - 2026-09-16

### Changed

- The image ships perf-sentinel `0.22.2` as its analysis engine, repinned by digest from
  `0.22.1`. That is the binary the Hub runs for a backend analysis, and it is also the
  version the launcher compares a polled daemon's `producer_version` against, so a fleet
  still on `0.22.1` now reads one patch behind where it read level. The engine is copied
  from the published image rather than downloaded, so the build reaches no host outside
  the registry, and `config/supply-chain.json` carries the same digest as the
  `Dockerfile`.

  Unlike the last two repins, this one is visible in the Hub's own data, and the reason is
  that the Hub does not compute a finding's identity, it takes the daemon's. A signature is
  `type:service:endpoint:template-hash`, `findings.signature` is the primary key, and
  `UpsertFindingAsync` resolves `ON CONFLICT(signature)`. `0.22.2` makes a trace rooted in
  a message consumer report its destination where it used to report `unknown`, so once a
  source upgrades, the same recurring problem arrives under a signature the Hub has never
  seen. It lands as a second row with today's `first_seen_ms` beside the `unknown` one,
  and the per-source window in `finding_sources` starts over with it.

  `finding_lineage` cannot join the two, and not by accident: its probe looks for a
  candidate on the same `endpoint` with a different `template_hash`, which is a renamed
  query on a stable route. This is the reverse, a stable query on a renamed endpoint, so
  the probe finds nothing and the successor carries no `lineage` block. The abandoned row
  then settles on `not_observed` rather than `likely_resolved`, because that status wants
  an `endpoint_heartbeats` row on the same endpoint and nothing resolves to `unknown` any
  more, and it ages out on the usual retention. A `first_seen` that restarts is the cost
  of the engine learning where those findings came from, and it is paid once per consumer
  endpoint per source. `docs/API.md` and its French mirror now say so under Lineage, which
  until this release described the template path alone.

  One caveat on the same release's route-attribution fix, which stops I/O spans on a
  sibling branch inheriting the outermost route. A finding re-attributed that way lands on
  a route that already exists, so unlike the consumer case it can satisfy the lineage
  probe: if that route holds exactly one recent finding of the same type with a different
  template hash, the Hub links them and serves an `original_first_seen` that belongs to an
  unrelated finding. Narrow, and worth knowing before trusting a lineage block dated to
  this upgrade.

  What this does not touch: incidents, keyed on an id that hashes service, kind and
  `at_ms` and nothing of the endpoint, a launched analysis, which stores a run row and
  never a finding, and acknowledgments, which the Hub does not own. The image also picks
  up `rustls` 0.23.45 against RUSTSEC-2026-0285 through the engine layer.

  No detection knob is added by this release, so `DetectionOverrides` is untouched and the
  launcher still gates `sanitizer_aware_min_cv` alone, on engines `0.18.0` or later.

- The toolchain moves to .NET 10.0.12, which is one release rather than a set of bumps:
  the build image to `sdk:10.0.401-noble-aot`, the runtime image to
  `runtime-deps:10.0.12-noble-chiseled-extra`, and `global.json` to SDK `10.0.401` with it.
  The SDK decides the NativeAOT toolchain, so `Microsoft.DotNet.ILCompiler` and
  `Microsoft.NET.ILLink.Tasks` follow to `10.0.12` in the lock across all five runtime
  identifiers. They carry no explicit version, which is why the lock has to be regenerated
  by the new SDK rather than edited. `Microsoft.AspNetCore.Mvc.Testing` and
  `Microsoft.Data.Sqlite` were already on `10.0.12`, so this realigns the images and the
  compiler with the packages rather than moving them.

## [0.1.9] - 2026-09-11

### Fixed

- The `osx-arm64` native binary is reproducible again. The release workflow builds every
  binary twice and compares the two byte for byte, and that gate had been failing
  intermittently on this one RID, taking the whole release down with it: v0.1.7 failed it
  twice before passing on a fresh run, and v0.1.9 failed it outright.

  The cause is in the linker, not in the compiler. Everything ILC emits is byte-identical
  between two builds of one commit: `__text`, `__managedcode`, `__const`, the `__got`
  itself and the entire symbol table. `ld` allocates two interchangeable `__got` slots for
  `_objc_msgSend`, one for the classic `__stubs` path and one for the `__objc_stubs` it
  synthesizes, and which consumer gets which slot is unstable across runs. That is three
  bytes, the `imm12` of `ldr x16, [x16, #off]` picking `0x820` against `0x828`. The other
  111 bytes of the 114 that differed follow mechanically, since `ld` derives `LC_UUID`
  from the content and the linker-signed ad-hoc `CodeDirectory` hashes the pages that
  moved. Passing `-objc_stubs_small` collapses the duplicate slot, so there is nothing
  left to choose. Measured after the change: the duplicate is gone from the indirect
  symbol table, and two consecutive local builds are byte-identical.

  `IlcSingleThreaded`, added in v0.1.7 for an earlier and different incident, was working
  the whole time. It simply never reaches the linker.

### Changed

- The image ships perf-sentinel `0.22.1` as its analysis engine, repinned by digest from
  `0.21.0`. That is the binary the Hub runs for a backend analysis, and it is also the
  version the launcher compares a polled daemon's `producer_version` against, so a fleet
  still on `0.21.0` now reads one patch and one minor behind where it read level. The
  engine is copied from the published image rather than downloaded, so the build reaches
  no host outside the registry, and `config/supply-chain.json` carries the same digest as
  the `Dockerfile`.

  Two engine releases are behind this pin and neither changes what the Hub does.
  `0.22.0` lets every key-gated daemon route take `Authorization: Bearer` beside
  `X-API-Key`, which widens what a deployment can send without touching the Hub: it keeps
  sending the single header value `AuthHeaderValue` holds, and that header still decides
  on its own. The same release ships ready-made alert rules and receivers for both
  Kubernetes operators, which feed `POST /api/incidents` on the daemon and reach the Hub
  only as incidents it already mirrors. `0.22.1` moves the daemon's blocking file
  operations off its runtime workers, so a daemon on a slow disk keeps answering the
  polls this Hub sends, and nothing it writes changes shape.

  No detection knob is added by either release, so `DetectionOverrides` is untouched and
  the launcher still gates `sanitizer_aware_min_cv` alone, on engines `0.18.0` or later.

## [0.1.8] - 2026-09-10

### Changed

- The image ships perf-sentinel `0.21.0` as its analysis engine, repinned by digest from
  `0.20.2`. That is the binary the Hub runs for a backend analysis, and it is also the
  version the launcher compares a polled daemon's `producer_version` against, so a fleet
  still on `0.20.2` now reads one minor behind where it read level. The engine is copied
  from the published image rather than downloaded, so the build reaches no host outside
  the registry, and `config/supply-chain.json` carries the same digest as the
  `Dockerfile`. The release adds `grouping` and `offset` to the findings listing, and
  the Hub sends neither: it polls `GET /api/findings?limit=1000&include_acked=true`,
  with no filter at all, and mirrors incidents through `GET /api/incidents`, where it
  already pages with `offset`. That `limit=1000` is the daemon's own cap, so a source
  whose distinct signatures outgrow it is truncated for the Hub, and `offset` is now
  the way to read past it. Nothing here does that yet. What this engine does change for
  the Hub is a `serialized_calls` suggestion that names three templates instead of
  every call of the sequence, which shortens what a backend analysis renders and stores
  for that finding type, and a folded read that clones only the page it returns, which
  is the read every poll makes.

## [0.1.7] - 2026-09-06

### Fixed

- The launcher's type annotations are checked rather than decorative. Its header has said since it was written that the file is type-checked with `tsc --noEmit` against `types.d.ts`, and none of that existed: no `types.d.ts`, no TypeScript anywhere in the repository, and no job running either. So 122 annotations across 967 lines were read by nobody. The six types they name are now declared from the API records they describe, and `npm run typecheck` at the root runs the check the header promised. It found one real defect on its first pass, `monitorCommand` documenting two of its three parameters and marking the second optional, which left the undocumented third reading as a required parameter after an optional one. A `launcher-js` job runs both the check and the launcher's 42 unit tests on every pull request that touches code, which is the first time any of this repository's JavaScript is exercised anywhere but a maintainer's machine: the CI had no Node at all. It is a required job, so it joins the aggregate gate rather than reporting beside it.

### Changed

- The native build is reproducible by construction rather than by luck. ILC compiles with one thread per core, and the order it emits some symbols follows the order those threads finish, so two runners on the same image produced binaries that differed. The release workflow compares its two candidates byte for byte and rejected the `osx-arm64` pair over 83 bytes: three in an indirection table where two entries had swapped places, sixteen more in the `LC_UUID` the linker derives from that content, and the rest in the ad-hoc signature over the whole thing. `IlcSingleThreaded` removes the only variable, measured rather than assumed: a parallel and a single-threaded build of one commit differ by 82 bytes, and two single-threaded builds are identical. Applied to every runtime identifier and not just the one that failed, since nothing about the cause is platform specific and the other three were stable by chance. The build jobs run in parallel, so the slower compile costs a release one job's worth of wall clock.

- The findings a mirrored incident froze read as labels rather than as wire values. The type column printed the engine's identifier verbatim, so a row announced `n_plus_one_sql` where the engine's own dashboard says `N+1 SQL`, and the severity sat in the same grey chip whatever it said. The twelve types now carry the wording the dashboard uses, and the severity chip takes the engine's three tones from the `--crit`, `--warn` and `--info` variables this Hub already mirrors, so a critical row is legible before it is read. The mapping is display only: `finding_type` stays a stored column, an index key and a public parameter of `GET /api/findings`, and the raw string is what the API still filters on. A type outside the twelve renders as itself rather than as a blank, since the Hub accepts any string on ingest and a newer engine can send one it has never heard of.

## [0.1.6] - 2026-09-05

### Added

- The Hub mirrors the incidents a perf-sentinel 0.20.0 daemon records when the operator's
  alerting posts one. Every poll copies the daemon's `/api/incidents` ring, frozen findings
  included, into a table the daemon's own restart cannot empty. A copy is one daemon's
  capture, keyed on the incident's id and the source together, because the id hashes
  nothing of the daemon and two daemons fed the same alert would otherwise collapse into
  one row and lose one ring's findings, and of two captures by the same daemon the richer
  one is kept, because Alertmanager repeats a firing alert and a daemon restarted in
  between would freeze a window its ring no longer reaches. The ring is read a page at a
  time under a 4 MiB body cap, and a page that overflows it is re-read at half the size
  from the same offset down to a single incident, since a daemon embeds up to a thousand
  findings per incident and a full page of a busy daemon would otherwise be filed as an
  error on every poll and mirror nothing. The read carries the source's auth header, so a
  daemon's `[daemon] read_api_key` goes in `AuthHeaderValue` under
  `AuthHeaderName=X-API-Key`, and its outcome lives in an `incident_reads` row of its own
  rather than in the source's reachability: a wrong key, or a daemon before 0.20.0
  answering 404, never demotes the findings that daemon reported on the same poll, and a
  page that is not a JSON array is filed as `invalid_incidents`, apart from the findings
  leg's own code. `GET /api/incidents` lists the copies newest first without their
  findings, which are stored apart so the listing never reads them, and with a `capture`
  verdict derived from the daemon's `oldest_finding_ms`, `GET /api/incidents/{id}` returns
  one whole, the richest copy when several sources hold the id, `/api/sources` gains
  `incidents_state`, `/api/status` publishes `max_read_limit` among its limits, and the
  copies expire with the findings retention on the Hub's clock. The launcher gets a fifth
  screen listing them in the daemon monitor's column order, its pages sized under the
  operator's read limit, each row unfolding into the findings it froze, placed before the
  incident or after the restart from their own stamp.
- The incidents screen reads the daemons when it is opened, rather than serving whatever the
  last poll left behind. An operator paged about an OOM kill opens that screen within the
  minute, and `Hub:PollInterval` defaults to an hour, so a poll-only screen would be empty
  exactly when it matters. `POST /api/incidents/refresh` reads every daemon's ring now and
  answers with the same body and the same parameters as the listing, so the screen costs one
  round trip, and the poll is the floor rather than the only path. A POST because the route
  writes to the store, and because a GET would be cached and prefetched, which a fleet read
  must never be. The
  read leg itself is one class shared by the poll and the route, so the two cannot drift
  on how a refused key, a missing route or an oversized page is filed, and it carries the
  same isolation: those outcomes live in the source's own `incidents_read` row and
  never in its reachability, since a wrong read key says nothing about the findings that
  daemon reports. What bounds it, given every screen open triggers one: the fan-out is
  capped by `Hub:MaxConcurrentPolls` the way the poll worker is, two refreshes run at once
  and a third gets a `503` with `Retry-After`, and a source read in the last ten seconds is
  skipped with its stored copy served instead, so a reload loop or five people watching the
  same screen cannot storm the fleet. That floor is a constant rather than a setting,
  because it protects the daemons from this Hub and is not an operator's preference.
  `/api/sources` gains `incidents_read_ms` beside `incidents_state`, and the screen prints
  one line per daemon saying how long ago its copy was read, or that it was never read: a
  fleet with nothing to report and a copy nobody refreshed would otherwise look identical,
  which is the second half of the same problem.
- The incidents screen shows the namespace an incident carries and filters on service,
  kind, namespace, environment and daemon. The namespace is the alert label a perf-sentinel
  0.20.0 daemon writes on an incident when the alert named one, relayed as the daemon wrote
  it and absent when it did not, so two
  deployments of one service name in two namespaces read apart: a tag for reading and
  filtering, never part of the copy's key. The daemon's own monitor prints namespace and
  service in one cell, the Hub gives the namespace a column of its own so the screen reads
  and narrows on either. The filters are applied by the Hub rather than by the page,
  `GET /api/incidents` and `POST /api/incidents/refresh` take `kind`, `namespace` and
  `environment` beside `service` and `source_id`, so the older rows a button loads next
  follow the same filter instead of the screen narrowing the one page it holds. `service`
  and `namespace` are free strings matched exactly, and an unknown one is an empty page,
  since only the daemons know the set. `kind`, `environment` and `source_id` are closed
  sets, the daemon's five kinds and the Hub's configured sources, and a value outside them
  is a `400` rather than an empty page: an empty incidents screen is the answer an operator
  hopes for, so a typo in a filter that configuration could have checked must not be able
  to produce it. `environment` resolves to every source configured with it, and given
  together with `source_id` the two intersect, so a pair that excludes itself lists
  nothing rather than one of the two quietly ceasing to apply. The
  filters narrow the answer, never the read: a refresh still reads the whole fleet whatever
  the query asks for.
- An unfolded incident carries an Analyse this window button, which opens New analysis
  with the mode set to a service, the incident's service filled in and the range set
  absolute to the window the daemon froze, under a banner naming the incident and, while
  the incidents screen still holds its row, how far the window reaches either side of it,
  otherwise its length. Without it the operator paged about an incident reads the window
  off the row and retypes it into the form, four numbers and a name copied by hand at the
  worst moment to copy anything. The source stays the operator's choice and is
  not part of the link: a daemon takes no window, so the incident's own daemon would run
  its in-memory snapshot rather than the window, and the window has to go to a trace
  backend the operator picks on the left. Under a daemon the banner says so and asks for
  one, instead of the form pretending the pair can run. The window's end is clamped to
  now, in the link when it is built and again when it is read: the daemon's window reaches
  past the alert, so a young incident still has a `window_to_ms` in the future, and the
  Hub refuses a range that ends there, which means the link built the moment the alert
  fires would otherwise open a form that cannot run. Clamping on the read side as well
  holds a hash that reaches the form by any other path to the same rule rather than
  refusing it. The handoff travels as
  `#/new?from=<ms>&to=<ms>&service=<name>&incident=<id>`, so the link keeps its parameters
  and can be shared or reloaded while the New tab itself carries none, and a query on any
  other route leaves the form alone.

### Changed

- The image ships perf-sentinel `0.20.2` as its analysis engine, repinned by digest from
  `0.19.0`. That is the binary the Hub runs for a backend analysis, so a run gets the
  0.20 detectors, and it is also the version the launcher compares a polled daemon's
  `producer_version` against, so a fleet still on `0.19.0` now reads one minor behind
  where it read level. The engine is copied from the published image rather than
  downloaded, so the build reaches no host outside the registry, and
  `config/supply-chain.json` carries the same digest as the `Dockerfile`. Two patches
  land with it. `0.20.1` repairs a daemon that would not restart once a Kubernetes
  `fsGroup` had weakened its incident archive, which the Hub never meets in a backend
  analysis since it runs the binary as a subprocess, but which a daemon this Hub polls
  does. `0.20.2` stops the report's detail cards hiding a long endpoint behind an
  ellipsis, which the Hub does meet: a backend analysis renders that report.

### Documentation

- A new page, [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md), answers the question the rest of the
  documentation left to be reconstituted from the code: whether walled-off environments get a
  Hub each or one central Hub, which way every flow goes, and what opening only the push
  direction gives up. It states what a poll actually resynchronises, that the daemon's
  exporter pushes a signature on discovery or on a severity escalation and refreshes a
  still-active one at most hourly, and that a backend a central
  Hub cannot reach stays analysable from the command line the launcher prints.
- `docs/LIMITATIONS.md` now carries two boundaries an operator used to meet at the first page
  load. The Hub must be served at the root of an origin, the launcher calling the API in
  absolute paths, which is the cousin of the daemon constraint already described beside it.
  And a single endpoint asks for a credential, `POST /api/import/findings`, so whoever
  reaches the port reads every finding of every tenant and can start a run. `docs/OPERATIONS.md`
  adds the consequence for `/metrics`, which shares its origin with the launcher.
- The two reachability claims in `docs/ARCHITECTURE.md` say what the code does. The import
  handler touches no reachability column rather than never touching `source_state`, since it
  may refresh an existing row's `producer_version`, and the poll writes reachability through
  its upsert as well as through the two `MarkSource` calls.

## [0.1.5] - 2026-09-03

### Changed

- The image carries the perf-sentinel 0.19.0 engine instead of 0.18.0. That release adds a
  `grouping` label beside `service` on `perf_sentinel_findings_total`,
  `perf_sentinel_slow_duration_seconds` and the three `perf_sentinel_service_*_io_ops_total`
  counters, which is breaking for an unaggregated alert on any of them the way 0.18.0 was for
  `service`: `sum()` or `sum by (service)` around one keeps what it matched before. The value is
  the finding's effective grouping, the namespace on Kubernetes, and `[daemon]
  per_grouping_labels = false` restores the 0.18.0 shape. Nothing the Hub itself reads moved.
  The report JSON keeps its shape, the new per-pair split is `serde(skip)` and in-process only,
  and the engine adds no `[detection]` key, so the ten defaults `GET
  /api/sources/{sourceId}/daemon` compares against still hold and the launcher offers the same
  knobs. The Hub's own dashboard reads `perf_sentinel_hub_*` and is untouched.

### Fixed

- The daemon view carries a default for `per_service_labels` and `per_grouping_labels`, and the
  settings panel draws them under a `metrics labels` group. A daemon publishes the first since
  engine 0.18.0 and the second since 0.19.0, and a setting the panel has no group for is not
  drawn at all, so both were invisible rather than marked as changed.
- The two buttons on a run's outcome panel had no ground of their own, so on the five toned
  panels they took the panel's tone as their face and read as prose with a ring around it. They
  now sit on the ground the insets already use, which separates by lightness on all five tones
  and in both themes. Their hover was `filter: brightness()`, which on the light theme fades a
  pale border towards the panel behind it: the hovered `Check the source` lost its outline where
  it was meant to gain one. Both outlined buttons state a darker edge instead, and the filled
  one keeps the brightening its opaque face was written for.

## [0.1.4] - 2026-09-02

### Fixed

- The launcher's `sanitizer_aware_classification` control was a native select, whose menu the
  OS draws over the button and aligns on the selected item. It now opts into the base
  appearance the refresh select already used, so the list hangs under the button and is
  painted by the page. Two details were measured rather than assumed. The picker asks for
  `max-height: stretch`, so it claims whatever room the axis offers instead of the height its
  four options need, and down the page that reads as too little room below and flips the list
  back over the button. Bounding the height does not change that decision, only dropping the
  position fallbacks does. And without the flex the base appearance leaves behind, the label
  sat at the top-left of the button with the arrow adrift in the corner.
- The four values `sanitizer_aware_classification` accepts, `auto`, `strict`, `always` and
  `never`, are marked as code in its description, where they read as ordinary words of the
  sentence before. The same treatment reaches `strict` and `auto` where
  `sanitizer_aware_min_cv` names them.
- `ParseEngineVersion` splits the probed version on separators passed as an array. As loose
  arguments they partially matched `Split(char, int, StringSplitOptions)`, which is what a
  reader has to rule out before trusting the line.

## [0.1.3] - 2026-09-02

### Changed

- The image carries the perf-sentinel 0.18.0 engine instead of 0.17.0. That release labels
  `perf_sentinel_findings_total` and `perf_sentinel_slow_duration_seconds` by service, which
  is breaking for an unaggregated alert on either, and it resolves a span with no service name
  to `unknown` on every ingestion path, so an acknowledgment taken on a Zipkin or Jaeger
  finding with a blank service has to be re-taken. It also adds `[detection]
  sanitizer_aware_min_cv`, which is the knob the launcher withholds below this version, so the
  advanced panel now offers it. The engine gains that one `[detection]` default and leaves the
  other nine unchanged, so the table `GET /api/sources/{sourceId}/daemon` compares against
  still holds for them.

### Fixed

- Two visual faults on a run's result panel. The `tuning` inset sat on
  `--surface-2`, close enough in lightness to `--crit-bg` that it dissolved into
  the panel behind it in the dark theme. It now darkens through whatever tone is
  behind it, which takes the lightness ratio between the two from 1.27 to 2.42.
  The light theme keeps its opaque white ground, which already sat above the
  pastel panels, and gains only the firmer border. And the count strip wrapped into a second row on a narrow
  viewport, where the cells ending each line are square while the container is
  round, so its outline broke at the top right and the bottom left. It stacks
  instead, each cell carrying its label beside the figure.

### Added

- The launcher's advanced panel offers the engine's two sanitizer settings on a backend
  run: `sanitizer_aware_classification` as a choice among `auto`, `strict`, `always` and
  `never`, and `sanitizer_aware_min_cv`, the timing-variance threshold that decides
  whether a run of identical parameterised queries reads as an N+1 or as a cached repeat,
  as a decimal from `0.01` to `10`. Both are recorded on the run and written to the
  per-run config like the eight thresholds before them. `detection_knobs` in
  `GET /api/status` now names each knob's `kind` (`integer`, `decimal` or `choice`) and,
  for a choice, its `choices`, with the default relayed as a JSON value rather than typed.
  A knob the probed engine's `[detection]` does not read is withheld from the list and
  refused on submission with a 400 naming both versions, rather than written to the run
  config and refused by the engine at run time with a stderr the Hub never returns. The
  mode has been read since engine 0.5.7 and is offered to every engine, the variance
  threshold appears only once the embedded binary is 0.18.0 or later.

## [0.1.2] - 2026-08-31

### Fixed

- The chart refuses to render while `image.digest` is still the all-zero release placeholder, and
  names the flag to pass. The placeholder satisfies the digest shape guard, so an install from a
  checkout that forgot `--set image.digest` used to reach the cluster and fail there as
  `ImagePullBackOff` rather than at `helm template`. A test now also asserts the committed chart
  carries the image helper `scripts/verify-release.py` pins, which until now was compared only at
  release time, against the packaged chart.

## [0.1.1] - 2026-08-30

### Changed

- The image carries the perf-sentinel 0.17.0 engine instead of 0.16.0. That release fixes
  `tempo` and `jaeger-query` dropping the spans they had already correlated, which are the two
  subcommands a run against a trace backend invokes, so such a report now carries span trees
  where 0.1.0 rendered none. It also bounds the cross-trace correlator's refused-pair set, which
  could OOM a daemon on a wide topology. The engine's own defaults are unchanged between the two
  versions, so the table `GET /api/sources/{sourceId}/daemon` compares against still holds.

### Fixed

- `/api/status` and the `perf_sentinel_hub_build_info` metric report `0.1.1` rather than `0.1.1.0`.
  Both read `AssemblyVersion`, which .NET pads to four components, so the version the Hub published
  about itself matched neither its own tag, its chart `appVersion`, nor its image label, and a
  dashboard joining the metric's `version` label against a release tag never matched. Both now read
  the informational version, through one accessor rather than two copies. The launcher already
  ignored the fourth component when comparing, so nothing downstream changes.

- `scripts/release.sh` runs the lab validation gate itself instead of leaving it to the operator's
  memory, and takes `--skip-lab` to bypass that one gate for a release the lab cannot tell apart
  from the last it validated. The bypass never writes the ledger and warns twice, so a skipped lab
  leaves a trace rather than a false PASS. `release-gate/check-lab-validation.sh` had existed since
  the first release without a single caller.

- `scripts/check-badges.py` runs in CI, validates `README-FR.md` alongside `README.md`, and reads
  each badge's evidence file instead of only checking that it exists. It had none of the three: no
  job ran it, so a README and a canonical block that disagreed passed every pull request, and the
  only job that touched it ran unit tests building their own README from their own copy of the
  badge set. That copy is now imported from the checker, so the two cannot drift.

- `image.repository` defaults to `ghcr.io/robintra/perf-sentinel-hub` rather than the bare name
  `perf-sentinel-hub`. The published chart already carries a stamped `image.digest` naming a
  GitHub Container Registry image, so a bare repository left half the identity unresolved and an
  install that did not override it ended in `ImagePullBackOff` against Docker Hub.

### Documentation

- The README carries its latest-release, container image and Helm chart badges again, all three
  having been removed while nothing was published for them to point at. The release badge takes the
  `512BD4` of the .NET one rather than the shields default orange.

- The launcher screenshots and both tour GIFs are regenerated against this version, so the version
  chip they show matches the release rather than the one before it.

## [0.1.0] - 2026-08-30

### Added

- Initial NativeAOT Hub release with durable SQLite findings, authenticated push ingestion, recovery polling, retention, and Helm deployment support.

- `GET /api/sources/{sourceId}/daemon` reads one daemon's applied settings and its own account of
  its state: the `[daemon]` section relayed verbatim, the detection thresholds and carbon scoring
  its export carries, three gauges against their caps, and the tuning hints the daemon writes
  about itself. None of that is in `/metrics`, which carries the counters behind the hints but
  neither the settings nor the sentences. Read on demand rather than polled, because settings
  never change without a restart the Hub has no signal for and a stale gauge would defeat the
  point. The Hub derives one thing, whether a gauge crossed 90 % of its cap, on the same line the
  daemon's own monitor draws. Every recommendation is the daemon's.

- `--brand-text` moves from `#11845d` to `#107a56` in the light theme. The brand green fell to
  4.09 against the active tab's own background, 4.12 on `--surface-3` and 4.37 on `--surface`, so
  the tab label, the settings disclosure and the footer link all sat under AA. The new value's
  worst case is 4.65, and the perceived shift is one step of lightness on the same green. The
  engine's dashboard template carries the same token and moves with it.

- The declared-environment chip takes `--text-2` on an unreachable row. `--text-3` is calibrated
  against the grey surfaces and lands at 4.49 on that row's red one.

- `--text-3` moves from `#7d8c83` to `#95a199` in the dark theme, which finishes an accessibility
  pass that had only been done on the light one: the old value fell to 3.97 against `--surface-2`
  and 3.50 against `--surface-3`, under the 4.5 WCAG AA asks of text this size. Measured on the
  rendered pages with translucent layers composited, every screen now passes in dark. The same
  value is corrected in the engine's dashboard template, which is where these tokens come from.

- The launcher is checked against the real engine: a new `tests/launcher-e2e.test.js` writes the
  generated config, runs the printed command, and asserts every threshold reaches the engine under
  the name it calls it back. It skips itself where there is no engine build.

- The generated config is named `perf-sentinel.toml`, without the leading dot, and the printed
  command names it with `-c`. A downloaded file may not keep a leading dot, so the dotted name
  the command used to ask for was one the reader could not count on having. Its caption now says
  where to put the file and that `-c` makes it required, instead of naming a second, nearly
  identical file the reader has nothing to do with.

- The generated `perf-sentinel.toml` can be downloaded, not only copied. Copying left the
  reader to create the file themselves, which is the one step between the thresholds they set
  here and a run that honours them.

- The fleet version summary counts only the sources running behind the Hub's engine. A fleet
  ahead of it, which is a normal moment during a rollout, drew an amber warning whose arrow
  pointed from the newer version down to the older one.

- The Hub says "update available" when a newer release exists. It asks the GitHub releases API once a day what the
  newest published engine and Hub versions are, and the version chip names whichever of the two is
  behind, with a link to its release notes. A source running behind the Hub's own engine now says
  where to get the newer one, and for a daemon that is the chart it is deployed from rather than a
  binary. This is the Hub's only outbound request that does not go to a configured source, so it
  has its own configuration section, its own endpoints, and `Hub:UpdateCheck:Enabled` turns it off.
  Off, or when the request fails, nothing is shown rather than a claim of being current.

- Native controls follow the theme: `color-scheme` is declared per theme, so the number steppers
  stop rendering in the browser's light scheme on a dark page.

- The re-read interval opens below its control instead of over it. A native select's menu is
  placed by the OS and no CSS moves it, so the control opts into `appearance: base-select` and the
  page paints and positions the picker itself. Browsers without it keep the native menu.

- Static assets answer `no-cache`. Nothing under `wwwroot` carries a fingerprint, so a browser
  choosing its own freshness could run an old `app.js` against a new API after an upgrade.

- `Hub:Analysis:MaxTracesCap` is bounded by the engine's own limit on `--max-traces` rather than
  by an unrelated 100000. A Hub configured in the gap accepted a count, drew it, printed it in the
  copyable command, and only then failed the run on an engine argument error.

- The dashed outline that marks a declared value reads at 3.5:1 rather than 1.2:1, on its own
  `--dash` token so the two places that draw it cannot drift apart.

- Max traces carries a "?" saying what it does, and the number itself takes the band's colour
  once the count stops being comfortable.

- The auth-header step shows the line that sets the variable, in the chosen shell's own syntax,
  rather than naming a variable and leaving the reader to work out the rest. A source the Hub
  reaches without a header shows none of it.

- Printed commands carry a tab per shell, POSIX or PowerShell, each with its own quoting and line
  continuation. The first visit opens on the platform's own, and the choice is remembered.

- Scrollbars follow the theme, using the dashboard's own three rules rather than the browser's
  default grey.

- A detection threshold's field carries the engine's default rather than showing it as a
  placeholder, so the spinner steps from 10 to 11 instead of jumping to the minimum. Each one can
  be put back on its own, and a button puts every one back at once.

- The printed command's prerequisites are a numbered "what you need first" block in the product's
  info tone, separate from the prose that only explains.

- The launcher remembers which source was last chosen and whether the advanced panel was open.

- The launcher's sink blocks carry the `//` overline every other heading on that screen has, and
  the one listing what a run hands back folds, closed until the reader opens it.

- A daemon row's re-read has its own deadline instead of waiting for the one-second countdown
  ticker to notice it is due, so the read lands when the disc closes rather than up to a second
  after it.

- The gauge strip no longer clips what rises out of it, so a move badge is not cut in half on its
  way up. Its rounded corners come from the cells at each end instead.

- The gauge move badge rises at a constant speed, its travel and its fade being two animations
  rather than one set of keyframes carrying both, and it starts clear of all but the top of the
  figure instead of halfway down it.

- A daemon row's gauges float what moved since the previous read over the figure itself, out of
  the flow and unselectable, so it neither widens a cell nor lands in a copied selection, and they take a tone once
  they are within 75 % or 90 % of the cap they publish.

- The footer's credit sits in the middle of the bar now that it is alone in it.

- The footer's step strip is gone. It was static markup shown on every screen, it described only
  the analysis form, and one of the four steps it named had no section on that form either.

- Folds are remembered between visits in the browser's own storage, and a fold closed over open
  children keeps them open for the next time it is opened.

- The daemon row's terminal block folds, closed by default, and sits one surface step above the
  row it is in so it reads as a block rather than as more of the same field.

- An unfolded daemon row says who it is written for, and that its settings are changed where the
  daemon is deployed rather than from the Hub.

- The terminal blocks' prose reads at 12px like the rest of the screen rather than at the 11.5px
  floor, and an inline code chip takes the size of the sentence it sits in instead of a fixed one.

- Clicking the interval select no longer leaves a focus ring behind. Tabbing to it still draws one,
  and so does the first key pressed after a click.

- The monitor command carries `--refresh` at the interval the daemon row is re-reading on, and
  follows the reader's choice as they change it. Off drops the flag rather than printing a zero.

- Every printed command now says it needs the `perf-sentinel` binary on the machine it is typed
  into, and links the release of the version this Hub runs. The analysis block asks "prefer your
  terminal?" rather than offering itself as an afterthought.

- An open daemon row re-reads itself on a status-only request and takes the full export at most
  once a minute, and a row whose read failed keeps asking with that same cheap request until the
  daemon answers, then reads in full. A row left open now recovers on its own.

- A report rendered from a daemon source is rendered with `--daemon-url` when the daemon's base
  URL is a bare origin and the configured engine binary takes that flag, which the Hub reads from
  `report --help` at startup rather than assuming from the version, so its Refresh and acknowledgment controls work from the viewer's browser
  once the daemon's CORS allows the Hub's origin. A backend run against an authenticated source
  now works at all: the source's auth header reaches the engine through the environment variable
  the printed command names, never through an argument a process list could show.

- The daemon view also publishes the engine's own defaults, so the fold marks what a daemon
  changed and shows the default beside it, the way the engine's `query monitor` does in its
  Config tab. The defaults belong to the binary this Hub embeds, which is the same approximation
  the monitor makes, so the response names the version they came from and the view says so when
  the daemon runs a different one.

- A daemon's row re-reads on an interval the reader picks, off or 5 to 60 seconds, with the
  countdown to the next read beside it, and a disc that fills over the interval and carries
  straight into the next one. The same knob `query monitor --refresh` carries, plus an
  off position the command has no use for: a terminal session is opened to watch, a table row is
  often opened to read one setting. A read only replaces the gauges and the hints, never the
  settings, which do not change without a restart, so open groups and focus survive it. Folding
  the row stops the reads.

- Each settings group folds on its own and starts folded, with the number of settings it holds and
  how many of them depart from the engine default. Folded is the useful state: eight headings fit
  on a glance and the counts say which one to open.

- Setting names the daemon writes in backticks render as code rather than as prose, the way the
  dashboard does. The terminal monitor strips those backticks because a terminal cannot draw them,
  and a browser can. Inline code now carries the dashboard's own chip treatment, a background and
  a border rather than a font change alone, which is what makes it read as code at all.

- Type sizes follow the dashboard's own scale: prose never drops below 11.5px, and anything
  smaller is a glyph, a badge or a short value. Several labels and countdown lines had been set at
  10.5px, which that scale reserves for things that are not sentences.

- Fleet health's headings align with their own cells. The last-success, unreachable-for and
  producer columns right-align their values while their headings stayed left, and the producer
  column disagreed with itself: two of its three cases aligned right and the third did not.

- Line length is one value across the stylesheet, 120ch, replacing limits between 58ch and 80ch
  that each resolved to about half the room their column had. Measured on the rendered screens,
  every block now wraps to the same number of lines it would with no limit at all, while a line
  still stops short of 170 characters. Six blocks were costing a line for nothing, among them the
  sentence under the run button and the gloss under each detection threshold.

- A link that follows prose inside a notice is spaced from it, rather than reading as one more
  line of the sentence above.

- The daemon panel no longer inherits the sources table's monospace face. A table of values is
  right to be monospaced and a panel of prose is not, and the two had been the same thing since
  the panel is a cell of that table. Names and values stay mono by asking for it.

- The fleet health screen unfolds a daemon's row onto that view, and the launcher prints the run
  it would submit as an engine command line for an operator who would rather use a terminal. The
  command is built from the object the form posts rather than from the form, so the two cannot
  drift, and values are quoted for a POSIX shell. Detection overrides have no flag, so a run that
  changed one prints the `.perf-sentinel.toml` it needs beside the command. An authenticated
  source prints `--auth-header-env` and never its token.

- `GET /api/sources` now also carries `base_url`, `engine_subcommand` and `auth_header_name`, the
  three things a command line needs. The header's name and never its value: the value stays in
  the Hub's configuration.

- `GET /api/sources` joins each configured source to its last known collection state, and
  `GET /api/status` now also reports the engine version, worker count, queue depth and the trace
  cap, timeout and report retention a run is held to. Together they are what a human interface
  needs to show a source list and state the cost of a run before it starts.

- Sources declare a `Kind` of `daemon`, `tempo` or `jaeger_query`. A trace backend serves no
  findings endpoint, so it is never polled and cannot carry an import key. Polling one would have
  marked a healthy source unreachable on every interval.

- A trace backend can declare `RetentionHours`, how far back it keeps traces. No backend API
  exposes it, so a client asking for a window beyond it waits for a full run before getting
  nothing useful back. A daemon cannot carry one, since it takes no window at all and the value
  would have no reader.

- `Hub:Analysis` configures the perf-sentinel binary the Hub runs, along with the worker count and
  the limits a run is held to. The binary's version is read once at startup, and a missing or
  unusable binary leaves it null rather than stopping the Hub, since collection and the read API
  do not depend on it.

- Analysis runs. `POST /api/analyses` queues a run against a configured source, a worker executes
  the perf-sentinel binary in a subprocess under a timeout, and `GET /reports/{id}.html` serves the
  HTML the engine rendered, from the same origin. A run is two invocations, because the query
  subcommands emit text, JSON or SARIF and only `report` writes HTML. A daemon skips the first one
  and its `/api/export/report` provides the input directly.

- Failures are reported as one of eight bounded codes and raw stderr never leaves the process. It
  is read only to tell a backend that refused us from a binary that broke, which have different
  owners.

- Reports expire after `Hub:Analysis:ReportRetention` and their run is marked expired while keeping
  its parameters. A run interrupted by a restart comes back as such and is never replayed on its
  own, since a silent retry would fire a second heavy query nobody asked for.

- The engine ships inside the Hub image, copied by digest from the published perf-sentinel image
  rather than downloaded, so the build reaches no host outside the registry. The Helm chart renders
  the `Hub:Analysis` section and a source's `Kind` and `RetentionHours`, which it previously
  dropped, and reports live under the existing `/data` volume since the container root is
  read-only.

- `scripts/check-supply-chain.py` accepts a GHCR container pin, restricted to this account's own
  namespace the way the Docker Hub branch is restricted to jetbrains.

- A browser interface at `/`, served from the same origin as the reports. Four screens covering the
  form, one run, recent runs and fleet health, in plain HTML, CSS and JavaScript with no framework
  and no build step. The theme is tri-state and hands over to the rendered dashboard through
  `sessionStorage`, which is why the two share an origin.

- The launcher is excluded from Sonar coverage and `fonts.css` from analysis: browser code with no
  test runner in this repository would otherwise be counted at zero and sink the new-code gate.

- A run may override the engine's detection thresholds, exposed behind an advanced disclosure in
  the launcher and published with their bounds and defaults on `GET /api/status`. They are written
  to a per-run TOML handed to both engine invocations through `-c`. They change what counts as a
  problem rather than how large the report is, so a run records the ones it used and the recent list
  flags counts that are not comparable.

- Both engine invocations now run from a Hub-owned directory. The engine discovers
  `.perf-sentinel.toml` relative to its working directory, so an unset one let a stray file beside
  the Hub's launch directory silently decide detection thresholds. Measured: one threshold moved a
  report from 5,455,307 to 559,514 bytes.

- The result panel reports how many findings survived the sink's budget, read back from the rendered
  report. The summary is parsed from the engine's pre-trim output, so the card previously stated a
  count the artifact did not contain.

- JSON responses use snake_case property names, matching the envelope perf-sentinel itself emits.
  Every pre-existing field is a single word and serialises identically under either policy, so no
  response changed shape.

- `GET /metrics` serves the Prometheus text format, eight metric families over data the Hub already
  holds. `Api/MetricsEndpoint.cs` writes that format by hand rather than pulling a client library,
  which would be a third package in a NativeAOT service whose only two are SQLite. Cardinality is
  bounded by configuration rather than by requests: `source` takes the ids in `Hub:Sources`, fixed
  at startup and restricted to 1 to 64 ASCII letters, digits, `.`, `_` or `-`, and `status` takes
  the six constants in `AnalysisStatuses`. Nothing a caller sends reaches a label. Label values are
  escaped for backslash, quote and newline anyway, so that loosening the source id rule later
  cannot silently produce a scrape Prometheus rejects. The endpoint carries no authentication,
  exactly like `/api/status`, so keep it behind whatever fronts the rest of the Hub.

- A daemon the Hub has never observed publishes nothing rather than a value that reads healthy.
  `perf_sentinel_hub_source_reachable`, `perf_sentinel_hub_source_unreachable_seconds` and
  `perf_sentinel_hub_source_last_success_seconds` are written only for a source that has a
  `source_state` row, and only for a daemon at all: a trace backend is never polled, so calling one
  reachable would assert something the Hub has not seen. A 1 for a source never attempted reads as
  a poll that succeeded, and a 0 for one never polled successfully reads as "succeeded just now",
  which is the opposite of never. Retention drops the `source_state` row of a source the Hub
  stopped attempting, so a long forgotten daemon goes silent rather than turning green. Each family
  is its own loop over the sources, because every sample of a family has to be contiguous and one
  pass would interleave them.

- `perf_sentinel_hub_source_last_import_seconds` reports how long ago a daemon last pushed, from a
  `source_imports(source_id, last_import_ms)` table added as schema `V4` and written only by
  `TryUpsertBatchAsync`. It is kept out of `source_state` on purpose: a push must never look like a
  successful poll, and a push-only source would need every poll column there nullable to own a row.
  It is not a heartbeat, and its HELP text says so, because the daemon exporter sends nothing at all
  while it has no findings, so an old value means "no new finding since" and not "the push path is
  broken". `PurgeAsync` deletes nothing from `source_imports`, so a daemon that stopped pushing
  keeps a climbing age instead of losing its series, which would read like a daemon never observed.

- `perf_sentinel_hub_import_rejected_total{reason}` counts the imports the Hub refused, over the
  five bounded reasons of the `ImportRejection` enum: `bad_request`, `unauthorized`, `gate_full`,
  `write_timeout` and `too_large`. `gate_full` is a fifth import arriving while `ImportGate` already
  holds four in flight, and `write_timeout` is the write gate not coming free in five seconds, kept
  apart because they name two different knobs, one on the uploader's spacing and one on a writer
  held by the poll or by retention. The counts live in process rather than in SQLite, since a
  refusal happens on the very paths that could not take the write gate, and resetting on restart is
  what a Prometheus counter is allowed to do. Every reason is published from startup, zeros
  included, because a series that appears only on the first failure reads as a scrape gap and an
  alert cannot tell the two apart. The label mapping keeps no working catch-all, so a reason added
  without a label throws instead of publishing refusals under a name that says nothing.

- `perf_sentinel_hub_analysis_queue_depth` counts the runs accepted and not yet claimed by a worker,
  read from the same `CountRunsByStatusAsync` snapshot as `perf_sentinel_hub_analysis_runs{status}`
  rather than from a second query, so two counts of the same rows taken a moment apart cannot
  disagree inside one scrape. `analysis_runs` is a gauge and not a `_total`: a run moves between
  statuses and a finished one ages out on `Hub:Analysis:RunRetention`, so every series falls as well
  as rises, while a run still `pending` or `running` is never purged however old its row looks.
  Every status is emitted even at zero, a gauge that vanishes reading as a scrape failure rather
  than as nothing being in that state. One scrape takes one connection for both source tables
  through `QuerySourceObservationsAsync`, two statements rather than a join because SQLite grew
  FULL OUTER JOIN only in 3.39 and a LEFT JOIN would drop exactly the push-only sources.

- Every age the endpoint publishes clamps at zero. A stored timestamp ahead of the Hub's clock, from
  skew or from a restored backup, would otherwise expose a negative value that breaks every
  threshold comparing against it. Zero reads as "just now", which understates an age rather than
  breaking the alert.

- `Hub:Analysis:RunRetention`, thirty days by default, ages a finished run out of `analysis_runs`.
  Nothing deleted those rows before, so a Hub that had ever run an analysis carried every run it had
  ever accepted. The knob is validated as strictly longer than `Hub:Analysis:ReportRetention` rather
  than merely positive, because the two are one mechanism: the report is deleted after 24 hours and
  the run is marked expired, and the row has to outlive it so an expired run can still be read and
  relaunched with the parameters it was given. It also gets its own clock instead of riding on
  `Hub:Retention`, whose 180 days would keep six months of run history in a table nothing reads back
  that far. Once the row goes, `GET /api/analyses/{id}` answers 404. This is not an audit trail, and
  a link shared yesterday is already dead.

- The purge never removes a run whose `status` is `pending` or `running`, however old its row looks,
  since a worker is about to write to it or already is. Age is read as
  `COALESCE(finished_at_ms, created_at_ms)`, so a row that reached a terminal status without a
  finish time is still aged out on when it was created rather than kept forever. `PurgeAsync` takes
  the run cutoff as a second argument beside the findings cutoff, and both still delete in bounded
  chunks, since holding the write gate for one whole delete would answer 503 to every daemon import
  for the duration.

- `examples/grafana-dashboard.json` imports as it stands: nine panels over the eight metric
  families, on a `$job` variable read from `label_values(perf_sentinel_hub_build_info, job)` so one
  dashboard serves several Hubs. Every panel carries a description saying what its silence means,
  because most of these series are absent rather than zero on purpose: a daemon the Hub has never
  once reached publishes no `perf_sentinel_hub_source_reachable` row at all, and one never polled
  successfully has no `perf_sentinel_hub_source_last_success_seconds` series, zero there reading as
  "succeeded just now". `perf_sentinel_hub_analysis_runs` is a gauge, so `increase()` over it means
  nothing. Alongside it, `examples/prometheus-scrape.yml` names its targets for a docker compose, VM
  or discovery-less Prometheus, the chart already carrying the annotations an
  annotation-discovering one reads. The engine ships its own dashboard and the two do not overlap:
  no panel here reads a daemon series, none there reads a Hub one.

- `examples/prometheus-alerts.yml` carries one rule, `HubDown` on `up{job="perf-sentinel-hub"} == 0`
  for 30m, and says in the file why it is alone. The Hub sits in no production request path and push
  is the primary path, daemons POSTing findings and retrying coalesced batches, so a rule that fires
  on something a panel already draws is noise. Four were written and cut, listed in
  `docs/OPERATIONS.md` with where each condition shows instead: unreachable and stale sources both
  watch the poll safety net rather than the push path, so they go red on a fleet whose daemons are
  all delivering, a queue depth of 20 is 20 people clicking run and `GET /api/status` shows it to
  the person who queued them, and interrupted runs fire on a restart catching a queued run and never
  clear since nothing deletes those rows. What survives is the condition no panel can show, a dead
  Hub publishing no series looking exactly like a broken scrape config. The rule is a ticket, not a
  page, the loss horizon being the daemons' retry buffers filling, which is hours.

- `docs/OPERATIONS.md` and its French mirror describe the eight families and say why the import
  counter still earns no alert rule, which is worth stating because it looks like it should.
  `unauthorized` rises when a key expires and also when anyone at all posts an unknown `source_id`,
  and the only label that would separate the two is the caller's own `source_id`, the unbounded
  value that must never reach a label, so alerting on it would hand a stranger the ability to page
  you. `bad_request` has the same problem, its query-string half being checked before the key is.
  Two gaps are named rather than papered over: a push blocked before it arrives, by a network policy
  for instance, produces no request and therefore no rejection, and Prometheus holds no list of the
  sources that ought to exist.

- The chart's Service can carry annotations, through `service.annotations` in `values.yaml`. It
  ships empty because a scrape is opted into rather than assumed: set `prometheus.io/scrape`,
  `prometheus.io/port` and `prometheus.io/path` there, or point a ServiceMonitor at the Service
  instead.

- `examples/appsettings.reference.json` sets every setting to the value the Hub already uses,
  annotated, so copying it whole changes nothing. It is the inventory rather than a starting point,
  and it exists because the .NET JSON configuration provider ignores a key it does not recognise in
  silence: a name recalled from memory produces no error and reads like a bug in the Hub rather than
  a typo in the file. `Sources` is the one exception, it has no default and the Hub refuses to start
  without it, so its entry is an example. `ReferenceConfigurationTests` keeps the file exhaustive by
  walking `HubOptions` with reflection and failing on any writable property with no key here,
  descending into a list through one populated entry and asserting out loud when it meets a
  container shape the walk does not model, rather than skipping settings in the one test whose job
  is catching what the reference forgot.

- `Hub:Analysis:MaxTracesEmbedded` is documented, default `50`, valid from 0 to 10000. It has been
  in `HubOptions` and passed on every `report` invocation since analysis runs existed, and appeared
  in no document until the reference file forced the inventory. `AnalysisRunner` always passes
  `--max-traces-embedded`, and passing it at all opts the sink out of size targeting: without it a
  wide sweep loses the tail of the finding list to the 5 MiB budget, and the finding list is what an
  operator came for, so the span trees get capped instead.

- The README is a landing page again, 524 lines down to 115, and everything it used to inline lives
  in a document named for the task it serves: `docs/ARCHITECTURE.md`, `docs/CONFIGURATION.md`,
  `docs/API.md`, `docs/LAUNCHER.md`, `docs/OPERATIONS.md`, `docs/LIMITATIONS.md`, plus
  `RELEASING.md` and `CONTRIBUTING.md` at the root. The filenames are upper case the way the
  engine's are, so a reader moving between the two repositories does not have to remember which one
  shouts. Every one of them has its French mirror under `docs/FR/`, `README-FR.md` included, and the
  README's own table says what each of the eight covers rather than leaving the reader to open them
  all.

- Five mermaid diagrams in `docs/diagrams/mmd/` explain the topology, push against poll, what a
  launched run does, the three retention clocks and the states a run passes through, each rendered
  into a light and a dark SVG under `docs/diagrams/svg/` and served through a `<picture>` so a
  dark-mode reader is not handed a white slab. The `.mmd` sources carry no `%%{init}%%` theme block
  on purpose, because the theme is an export argument and that is what lets one source produce both
  files. `docs/ARCHITECTURE.md` says the export goes through mermaid.live rather than `mmdc`, and
  measures why: on this repository's own sources `mmdc` with no background flag writes
  `hsl(80, 100%, 96.2%)` where the perf-sentinel family has `rgb(255, 255, 255)`, and
  `-t dark -b '#232030'` still writes `hsl(20, 1.6%, 12.4%)` rather than `rgb(35, 32, 48)`. It
  closes with a table checking every arrow of the topology diagram against the symbol that draws it,
  `FetchReportSnapshotAsync`, `MarkSourceAttemptAsync`, `UpdateChecker.ReadAsync` and the rest,
  named by symbol rather than by line so an edit above them does not make the table lie.

- `docs/LIMITATIONS.md` gathers the caveats that were scattered next to the features they constrain,
  so the boundaries can be read in one pass before deploying instead of one at a time afterwards:
  the 1,000-row cap the 0.11.x daemon puts on `/api/findings`, that a poll omitting a finding does
  not resolve it, that reachability is one-directional and only a successful poll clears
  `unreachable_since_ms`, that `status` is a presumption derived at read time, that the sink's 5 MiB
  report target is a private constant with no flag or config key, and that counts from runs with
  different detection thresholds are not comparable. `docs/CONFIGURATION.md` states the same split
  at its source: `Id`, `Name`, `Environment`, `Kind`, `BaseUrl` and `RetentionHours` are declared
  and never checked against anything, while `reachable`, `last_success`, `unreachable_since`,
  `producer_version` and `last_error` are written by the poll, which is why a deployment can label
  production as staging and nothing contradicts it. It also says a trace backend is contacted by
  nothing but a launched run, so a Tempo or Jaeger source showing no producer version is the design
  rather than a fault.

- The fleet table and the launcher's source list both group daemons apart from trace backends,
  under a `daemons` band and a `trace backends` band. The two kinds behave differently enough to be
  worth the split: a daemon is polled and pushes on its own, a trace backend is only read when a run
  asks it for traces. A fleet holding a single kind draws no bands at all, because the label would
  name the only thing on screen. Both surfaces partition through the same `PSL.splitByKind`, which
  keeps every source's position in the original array: a fleet row's fold ids are built from that
  index, so a grouped row still addresses the source it belongs to. An unknown kind counts as a
  backend, matching the engine, where only a daemon is polled.

- Every duration measured against the current moment now ticks once a second from one
  `state.durationTicker` over a `state.liveDurations` registry, instead of a timer per screen. A
  node registers through `live(node, compute)` with the same function that built its text, and the
  ticker rewrites the text and, where a cell carries that same string as its tooltip, the `title`
  with it. Re-rendering to refresh them was not available on the report screen: rebuilding it
  replaces the iframe and reloads the report under the reader. Each entry computes inside its own
  `try`, so one that throws cannot freeze every countdown behind it in the list for the rest of the
  screen's life, and a fact line borrowed for six seconds by a failed resubmission is left alone
  until its error clears.

- Countdowns carry seconds and uptimes carry minutes, through `PSL.durPrecise` and `PSL.durMinutes`
  over a shared `durDown(ms, floor)` that keeps every unit between the largest that applies and the
  floor, zeros included. `PSL.dur` stops at two units, which reads well for an age skimmed in a
  table and badly for a figure someone watches: 86,399,000 ms of remaining report life read as
  `23 h 59 m` and never appeared to move, and an uptime of 888,120,000 ms read as `10 d 6 h`, hiding
  the 42 minutes that were the only part changing between reads. A report already gone reads
  `expired` rather than counting on past zero, and a negative interval floors at `0 s` instead of
  reading as one second from expiry.

- The run result strip takes the treatment the rendered dashboard gives its overview tiles, so the
  two surfaces read as one product. The quality gate leads the strip as `PASS` or `FAIL`, the
  findings count is the one cell filled as a solid block and takes the dominant severity found,
  `crit` over `warn` over `info` and `ok` when there is nothing, and the severities beside it take
  their own pastel through `data-grad`. The dark theme's filled backgrounds are darker than the
  light theme's so white text on them stays legible. The daemon gauges pass no options and keep the
  neutral strip, because their tone means near a cap, which is not a severity.

- No version is tagged without a recent PASS from the simulation lab.
  `release-gate/check-lab-validation.sh --version vX.Y.Z` reads the append-only
  `release-gate/lab-validations.txt`, four tab-separated fields per line,
  `<version>\t<lab_commit_sha>\t<YYYY-MM-DD>\t<PASS|FAIL>`, and exits non-zero when there is no PASS
  for that version, when the newest one is more than thirty days old, when the date is in the
  future, or when the ledger is missing, empty or off schema. The lab runs the Hub image against a
  real daemon through `hub-ingestion`, `hub-derived-status`, `hub-lineage-mutation`,
  `hub-retention-purge`, `hub-source-reachability` and `hub-plugin-contract`, which is coverage no
  unit or integration test in this repository reaches: ingestion, polling, the derived status, the
  lineage columns and the plugin's envelope contract, all against a daemon that really produced the
  findings. The gate is operator-driven by design, since CI cannot reproduce a run that needs a
  Kubernetes cluster, a workload fleet and an image built from the commit under test. A ledger line
  names the Hub commit the image was built from and not only the version, because the version in
  column one is the one about to be tagged and so identifies nothing yet. `RELEASING.md` carries the
  procedure, and step 1 of the publish list now asserts the gate. The script is a copy of
  perf-sentinel's, which is the original and holds the full test suite, so a fix to the date
  handling or the ledger schema belongs in both. The ledger's first entry records `v0.1.0` as PASS
  on 2026-08-29 against Hub commit `d7f5889`, validating a version that has never been published,
  which is the whole point: the lab has to see the build before the tag exists.

- A `.gitleaks.toml` that extends the bundled default ruleset and adds one narrow allowlist, so
  `generic-api-key` stops flagging the engine's finding signatures carried by the demo fixtures. A
  signature is `<finding_type>:<service>:<sanitized_endpoint>:<32-hex>`, a deterministic SHA-256
  prefix operators copy out of `analyze --format json` into PR-reviewed acknowledgment files, public
  by design and not a credential. The allowlist is scoped to
  `tests/browser/demo/fixtures/.*\.json` under `condition = "AND"` rather than the default OR,
  without which the path list and the regex are two independent ways to be allowlisted and the regex
  applies everywhere. `regexTarget = "line"` silences the whole source line, so confining it to
  machine-generated capture output keeps that bypass out of hand-written code. The leading token is
  pinned to the engine's twelve finding types, from `n_plus_one_sql` through `slow_messaging` to
  `serialized_calls`, because a bare `[a-z_]+:` prefix would have accepted a real credential
  formatted as `aws_secret_key:account:region:<hex>`. A rule allowlist rather than `.gitleaksignore`
  fingerprints, which are bound to a commit and a line number: the fixtures are regenerated by
  `tests/browser/demo/capture-fixtures.sh` at every engine bump, and each regeneration would leave
  four stale entries and eleven new findings behind.

- The result panel takes its tone from the quality gate's verdict rather than the run's. A run that
  completed with a failing gate drew a green panel over its own sentence, "The quality gate did not
  pass."

- A run card sits on `--surface` rather than `--surface-2`, and its `.run-card-args` line steps back
  to `--bg`. `--surface-2` means raised above a grey panel, so it is lighter than its ground and
  resolves to white in the light theme, which left the card indistinguishable from the page it sits
  on. `.source-row` and `.cost-cell` do sit on a panel and keep the token.

- `HubDatabase` is no longer `IDisposable`, and `_writeGate` and `_initializeGate` are never
  disposed. The host disposed the database at shutdown, which disposed a `SemaphoreSlim` that a poll
  still in flight was waiting on, arming an `ObjectDisposedException` on a `WaitAsync` already in
  progress. A `SemaphoreSlim` only holds a wait handle once `AvailableWaitHandle` has been read,
  which nothing here does, so the dispose freed nothing and bought only the race. The tests dropped
  their own `using` and `Dispose` on it with it.

- `scripts/check-supply-chain.py` skips `node_modules` when walking for structured files. The
  browser demo suite's dependencies are gitignored and never reach CI, so the workflows vendored
  inside them are not this repository's supply chain to declare, and scanning them only failed the
  check on a machine where someone had run `npm install`.

- `first_seen` comes from the daemon envelope (`first_seen_ms`) instead of the Hub's poll clock, clamped to the observation time and to a Unix-ms sanity floor so neither a clock running ahead nor a seconds-unit bug can distort the irreversible MIN. `last_seen` deliberately stays the Hub's own observation clock, since retention, ordering and the freshness guard compare it and must read one monotonic clock. A finding's age now reflects when the daemon first detected it, not when the Hub first heard about it.

- The test suite runs on xunit.v3 4 over Microsoft.Testing.Platform. The .NET 10 SDK removed the VSTest bridge that xunit 3 relied on, so `global.json` opts into the platform runner, the test project builds as an executable, and `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio` and `coverlet.collector` give way to the platform's own coverage and TRX extensions. The coverage engine changed with it, so the exclusion moved rather than disappeared: coverlet dropped generated sources through its `**/obj/**` rule, and the Microsoft collector has no working equivalent, so ReportGenerator now applies the same filter to the report before the gate reads it. Without it the figure fell to 75.19%, not through any lost test but because 1,345 of 2,237 measured lines were source-generated (minimal-API route builders, the configuration binder, the JSON context); with it the baseline sits at 90.81%, next to the 91.84% coverlet recorded. Separately, the coverage run no longer carries a `--filter` allowlist of suite names, so a suite added later is measured by construction rather than by remembering to extend a list, which is how `BackupTests` had escaped it.

- A `backup` CLI command (`PerfSentinelHub backup <destination>`) snapshots the live database with SQLite `VACUUM INTO`, refuses to overwrite an existing destination, cleans up its partial file on failure, and rejects a wrong arity with a usage message instead of falling through to the server. Comes with a `make backup` wrapper and a documented backup and restore procedure. The database volume is the only non-reconstructible state the Hub holds.
