# The launcher

The Hub serves a browser interface at `/`, from the same origin as the reports it opens.
Plain HTML, CSS and JavaScript. No framework, no build step, no network fetch: the two
typefaces are base64 in `wwwroot/fonts.css` and every icon is inline SVG.

Six screens: start an analysis, follow one run, list recent runs, read fleet health, read
the incidents the daemons recorded, follow the correlations they pushed. With `Hub:Access` on, the identity chip at the top
right opens [Your access](#your-access), and an administrator gets a seventh tab,
[Coverage](#the-coverage-screen). A Disclosures tab shows when `/api/status` says
`disclosures`, that is while disclosures are on and the caller reads them, see
[The disclosures screen](#the-disclosures-screen). Another screen has no tab and is reached
by link only, see [The ack page](#the-ack-page).

Every route change names the screen in the browser tab and moves the focus to its
heading, so a screen reader announces where the reader landed. Below 1400px the tab icons
and the theme's label give way so the bar stays on one row, and below 760px the tabs take
a row of their own that scrolls sideways instead of the page.

## The form follows the source

The form adapts to the selected source's `kind` rather than offering an independent
live-or-historical switch. A switch would let an operator compose impossible states, such
as a three-hour window against a daemon that keeps ten minutes.

A link from the incidents screen arrives here with the form filled in: the incident's
service, and its window as an absolute range with its end held at now. A banner above the
parameters names the incident, as `ns/service` when it carried a namespace, and says that
an analysis takes a service and no namespace, so the run covers that service in every one
of them. The source is not part of the link, since a daemon takes no window: under a
daemon the banner says so and asks for a trace backend.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-sources-dark.png">
  <img alt="The fleet health screen with a daemon row unfolded" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-sources.png">
</picture>

## Gauges

A gauge is toned once it is close to a cap it published. Red from 90 %, the engine's own
advisor line and the same one that turns a row's verdict to near capacity. Amber from
75 %, the Hub's own step ahead of it. The findings ring is never toned and never turns a
verdict: it fills on any daemon that runs long enough, and the Hub keeps what it evicts.
When a poll finds that some left the ring before it could read them, the hints say so.

Each read shows what moved since the one before, rising off the figure it belongs to and
fading out: red for a rise, green for a fall. Every one of these counts toward a cap, so
up is the direction that costs something. Uptime is neither toned nor tracked, having no
cap and only one direction to go in.

## What the browser remembers

Which folds a reader has opened, in that browser's `localStorage`, under one key and as
open folds only. A row, its terminal block, its settings and the groups inside them come
back the way they were left, and a row left open reads its daemon again on the next visit
without being clicked.

Nothing but those names is stored. A browser that refuses storage simply starts everything
folded.

The theme is tri-state: system, light, dark. Only the resolved light or dark ever reaches
the DOM, so stylesheets see two values and never three. The position is stored under
`perf-sentinel:theme` in both `localStorage` and `sessionStorage`, the second because the
rendered dashboard reads that exact key from this origin. That handoff is why the launcher
and the reports must share an origin.

## Printed commands

Every printed command carries a tab per shell, because the difference is not cosmetic. A
POSIX shell continues a line with a backslash and escapes a quote by closing and
reopening. PowerShell continues with a backtick and doubles the quote, and its bare-word
set is narrower since a comma is its array operator.

The tab a first visit opens on follows the platform, Windows getting PowerShell. The
reader's own choice is remembered from then on and applies to every command on the page at
once.

Neither command carries a placeholder. The endpoint is the source's own configured
`PublicUrl`, or its `BaseUrl` when it declares none, and the monitor command carries the re-read interval the reader picked on that
row, so a copied line is runnable as it stands and does not contradict the screen it came
from. The one thing an operator still types is the service name, which is theirs to choose
and is shown empty rather than guessed.

Both commands say where to get the engine, since neither runs through the Hub. The note
links the release of the exact version this Hub runs, which is the version the flags are
spelled for. Without a probed version the link falls back to the release list rather than
inventing a tag.

### The run as a command line

The launcher prints the run as an engine command line, so an operator can take it to a
terminal instead. It is built from the very object the form posts, never from the form, so
the printed command and the submitted run cannot drift. The endpoint is the one deliberate
difference: a source that declares a `PublicUrl` prints it, while the Hub runs against its
`BaseUrl`.

It is one command and not the two the Hub runs: the JSON output and the render step exist
so the Hub can build a dashboard, and a terminal needs neither.

Values are quoted for a POSIX shell with single quotes, the only form that holds for a
service name carrying `$` or a quote. An authenticated source prints `--auth-header-env`
rather than its token, which the Hub holds and never discloses. The header it names is the
source's `PublicAuthHeaderName` when it declares one, see [CONFIGURATION.md](CONFIGURATION.md).

Detection overrides have no command-line flag, so a run that changed one carries
`-c perf-sentinel.toml`, and the file is printed beside the command, ready to copy or to
download. The name is undotted because the engine only discovers the dotted
`.perf-sentinel.toml`, which a download may not preserve, so the command names the file
rather than relying on that discovery.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-report-dark.png">
  <img alt="A rendered report opened inside the launcher" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-report.png">
</picture>

## Live reports

A report rendered from a daemon source that has an ack credential goes live through this
Hub: the render passes `--hub-relay` (engine 0.31.0 or later), and the dashboard's Ack and
Revoke controls call the [ack relay](API.md#ack-relay) at the Hub's own origin, in the
reader's name and under their rights. No daemon key reaches the browser, the daemon needs
no CORS entry, and the viewer never reaches it. This holds with access control on or off.

Otherwise, with access control off, a report goes live straight at the daemon when the daemon's `PublicUrl`, or its
`BaseUrl` when it declares none, is a bare origin. The render passes `--daemon-url`, and the dashboard's own Refresh and
acknowledgment controls then talk to that daemon from the viewer's browser.

Two conditions sit outside the Hub: the daemon's `[daemon.cors] allowed_origins` must
carry the origin this Hub serves reports from, and the viewer must be able to reach the
daemon directly. A Hub that polls its daemon by a Service name needs a `PublicUrl` for
this, see [CONFIGURATION.md](CONFIGURATION.md).

A daemon behind a path-based ingress gets a static report instead, because the engine's
flag takes an origin and nothing else. So does every daemon source when the configured
binary does not take `--daemon-url` at all: the engine declares it inside its `daemon`
feature, so a binary built without that feature refuses the argument rather than ignoring
it, and a run passing it would render nothing. The Hub asks the binary once at startup,
through `report --help`, and renders static when the answer is no or unreadable.

Report links share within network reach of the Hub and die with the retention window.

## The fleet health row

A row's health reads `reachable`, `not yet observed` before the first poll, or
`unreachable` with how long. `Hub database full` replaces the latter when the poll failed
on the Hub's own volume rather than on the daemon, and a daemon that keeps minting new
signatures reads its count for the last hour in the warning tone while it still answers.

A daemon's row unfolds into the gauges it reports against their caps and the hints it
writes about its own tuning, neither of which `/metrics` carries.

The row re-reads on an interval the reader picks, the same knob `query monitor --refresh`
carries plus an off position. A read replaces only the gauges and the hints: settings do
not change without a restart, so rebuilding them would throw away open groups for nothing.
Folding the row stops the reads.

The settings themselves are one click further in, grouped and folded, each group showing
how many of its values depart from the engine's own defaults. The row ends with the
`perf-sentinel query monitor` command for the same view in a terminal. When the header
published for that daemon, `PublicAuthHeaderName` or else `AuthHeaderName`, is `X-API-Key`,
a line under the command says to set `PERF_SENTINEL_DAEMON_API_KEY` to the daemon's read
key first, since the monitor sends that variable as `X-API-Key` and as nothing else. The
engine reads the key from the environment, so the command itself stays the same in every
shell. Any other header, an Ingress's own for one, is named instead, as one `query monitor`
cannot send.

## The incidents screen

Each row is an incident a daemon recorded when the operator's alerting posted it, in the
order the daemon's own monitor prints the columns: started, namespace, service, kind,
ended, findings, capture, source. The namespace is the alert label the daemon read beside
the service, empty when the alert carried none. The daemon is the author and the Hub copies
its record, so the screen reads what the daemon froze and re-derives nothing.

Opening the screen reads every daemon before it draws, so the rows are what the fleet holds
now rather than what the last poll left behind: an operator paged about an OOM kill would
otherwise wait out an hourly poll in front of an empty table. A button repeats that read on
demand and goes dead while one is in flight. Neither can storm the fleet, since a daemon
read in the last ten seconds is skipped and its stored copy served instead.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-incidents-dark.png">
  <img alt="The incidents screen with a row unfolded on its frozen findings" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-incidents.png">
</picture>

A row unfolds into the findings the daemon froze for that incident, each placed before
the incident or after the restart from its own stamp against the incident's. The capture
column carries the daemon's reading of how far back its ring still reached: complete,
partial, or empty. Times are relative, never dates, and the exact stamp sits in the
tooltip. Each finding shows the normalized query it was raised on, whole and wrapped,
since one endpoint often carries several, and its estimated impact, the findings
dashboard's avoidable I/O operations: traces seen times extra operations per trace, a dash
when the daemon has green scoring off. The findings open worst
first, then most seen, a click or a keypress on a column head sorts them by that column,
and a field above the table narrows them by endpoint or template. Five selects narrow the list. Kind and service stay
in sight, and namespace, environment and daemon fold behind More filters, which says how
many of them are set. The namespace one appears once a row has carried one. The Hub applies them, so the older
rows a button loads next, a hundred at a time from the store rather than from the daemons,
follow the same filter. Under the table, one line per daemon says when its copy was read,
or that it was never read, so a quiet fleet never reads as a stale copy. A daemon that
refused the Hub's key on that route gets a banner naming the setting to fix, and its
findings stay collected.

An unfolded row carries an Analyse this window button under the note describing the
window, which opens New analysis on the incident's service and window, described under The
form follows the source. The source stays the operator's choice, because a daemon takes no
window and the incident's own daemon would run its in-memory snapshot rather than the
window. The link keeps its parameters, so it can be shared or reloaded, and the New tab
itself carries none. When a source in the Hub holds an ack credential, each finding of the
unfolded row ends with an Ack link, which opens [The ack page](#the-ack-page) on that
finding with the incident's daemon checked. Without one, a line under the table says the
findings are acknowledged in each daemon instead.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-handoff-dark.png">
  <img alt="New analysis opened on an incident's window, under the banner naming it" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-handoff.png">
</picture>

## The correlations screen

Each row is a pair of findings that kept following each other, one firing within seconds after the
other, as the daemons with `[daemon.correlation]` and `[daemon.hub_export]` on pushed it: the side
that fires first, the one that follows it, how many episodes the pair had in the range, the last
one, marked active while it runs, its peak confidence, the median lag between the two and the
daemon. The rows are built in the browser from the episodes of the range, a thousand at most, and a
line under the table says when the read stopped there. A range, the last 24 hours by default, a
service matched on either side, a daemon and an "active now only" switch narrow the list.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-correlations-dark.png">
  <img alt="The correlations screen with a pair unfolded on its strip and its episodes" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-correlations.png">
</picture>

A row unfolds into a strip of the range, each episode in green over the same daemon's incidents in
red, then the list of its episodes: span, duration, peaks and the incidents each one overlaps,
which open the incidents screen on that incident. Analyse this window opens New analysis on the
episode's window, a minute wider on each side, and on the service of the side that fires first, as
the incidents screen does. Explain the last pair, shown when the Hub has a trace backend, opens New
analysis on the trace of one finding of the pair's last co-occurrence, with the other trace one
click away in the banner.

An unfolded incident lists the correlations active during it: the episodes of its daemon that
overlap the incident's window, up to its end once it has one, each with a link to every episode of
its pair. That link, `#/correlations?pair_key=<key>`, is the one the Grafana correlations table
uses too: the screen opens on that pair over 180 days, unfolded, with a Show every pair button
to leave it. A daemon that pushed no correlation at all says so, so an empty block never reads as
a quiet one. On a phone the tables scroll sideways inside their frame, as the incident tables do, and
the page does not.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-incident-correlations-dark.png">
  <img alt="An unfolded incident listing the correlations active during it" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-incident-correlations.png">
</picture>

## The disclosures screen

The periodic disclosures a scheduled job published, see [API.md](API.md#disclosures). A rail of
the last twelve quarters at most, up to the current one, opens the screen: each cell reads the
integrity level of the quarter's disclosure, internal, withdrawn, "not published", or how many
days the current quarter has left. A legend under it tells the two kinds of mark apart. A dashed
level is what the report declares about itself. A solid mark is what this Hub recomputed when
the report arrived, the content hash and an attestation that names it. The signature is checked
on the reader's machine, never here.

One card per disclosure follows, newest period first: the period and its dates, its chips, its
headline figures, when it was published and when a signed copy replaced it, whether the PDF
rendered here, and buttons to download the PDF and each file. A banner says when a disclosure
was withdrawn, by whom and why, when an official one carries no signature, and when the Hub has
not rendered it, with the reason. The screen ends on the commands a CI job runs to produce,
sign and publish the next one.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-disclosures-dark.png">
  <img alt="The disclosures screen: the rail of quarters, then one card per disclosure, one of them withdrawn" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-disclosures.png">
</picture>

A card opens the disclosure's own screen:

- **Integrity**, who checked what: the content hash and the binding, checked by this Hub and
  its engine version, the signature, for the reader to check with `verify-hash` against the
  expected signer, and the audit, only ever declared. Then the `content_hash`, the
  `report_uuid`, the expected signer and the verify command to copy.
- **Files**, each one downloadable, with `SHA256SUMS` for `sha256sum -c`.
- **Share links**, with their state, author, expiry and download count. A new link is shown
  once, with its PDF address and the `report.json` beside it, which `verify-hash --url` reads.
- **Deliveries**, each webhook event by the webhook's name, its state, attempts and last
  status.
- **Applications**, and **Method and limits** with the disclaimers, read from `report.json` as
  the report states them. A v1.8 report whose total holds database or broker host energy adds
  a `Database hosts` or `Broker hosts` row, which no application carries. Each share is taken
  over the report's `total_energy_kwh`, as in the PDF. A host row says `in K of N windows`
  when the totals held it for part of the period only.

A holder of `disclosures.manage` the Hub can name, through a session or the trusted identity
header, also gets New share link, Render again on a disclosure not rendered, Retry on a
delivery that has not gone through, and Withdraw, which asks for a reason and says the step is
final. On a Hub without access control, that is whoever the Hub can name. An internal or
withdrawn disclosure offers no share link.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-disclosure-dark.png">
  <img alt="One disclosure: its figures, applications, who checked what, its files and its share link" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-disclosure.png">
</picture>

## The ack page

One screen acknowledges or revokes a single finding, and no tab leads to it. Acks are made
from the Grafana findings dashboard, the HTML report, the TUI or the CLI, and this page is
where a Grafana link lands.

Three ways in:

- `/?ack=<signature>&environment=<environment>&source_id=<id>`, each value percent-encoded
  and the last two optional. A query and not a hash, because a hash is lost when the
  identity provider asks for a password on the way in. On load the launcher turns it into
  the route below, and the query leaves the address bar.
- `#/ack?signature=<signature>&environment=<environment>&source_id=<id>`, the route itself.
  `environment` and `source_id` are optional and only decide which rows start checked. A
  blank one reads as absent, which is what Grafana sends for the All choice of a variable.
  An environment past 256 characters or carrying a control character makes the link
  incomplete.
- The Ack link on each finding of an unfolded incident, which names the incident's daemon.

The page reads the finding from `/api/findings` by its exact signature and shows its type,
severity, service, endpoint, operation template, first and last seen, and status. A link
with no signature, or with one past the Hub's bounds, says that it is incomplete, and a
signature the Hub holds no finding for says so. Neither falls back to another screen.

A second read of the same route, windowed over the last 7 days with `from` and `to` taken
from the browser's clock, adds the detections to the card. The facts gain
`Detections (7 days)`, the count over every source, and `Avoidable I/O (7 days)` when the
daemons counted the extra I/O. A count that falls short, a day still open for one, reads
as a lower bound with a ≥ sign, and as `?` when no day of the window is known. Under the
facts, one line per source gives its count, its avoidable I/O and, in parentheses, why it
falls short, in words. The reasons are listed under [What the Hub adds to a
finding](API.md#what-the-hub-adds-to-a-finding). A window with no detection the Hub knows
reads "No detection known in the last 7 days". Any failure of this second read, a refused
request or an answer that is not a list, leaves the card and the form as they are and adds
the one line "The Hub could not read the detections of the last 7 days."

Below it sits one form: a required reason, an optional expiry, and one row per source that
carries the finding. The expiry is a day, sent as the last second of that day in UTC, and
an empty one is a permanent ack. When no source carrying the finding can take an ack from
the Hub, the page prints the daemon's own `perf-sentinel ack` command for it instead. A line
under the field reads the expiry back in UTC, "Holds until
2026-11-30 23:59:59 UTC.", beside the date the browser shows in its own locale.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-ack-dark.png">
  <img alt="The ack page on a finding four daemons carry, one row per case" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-ack.png">
</picture>

An ack hides a finding, so the link's context decides which rows start checked, among the
rows something can be done on. When the link names a source, that row alone is checked.
Otherwise, when it names an environment, the rows of that environment are. With no context
at all, a row starts checked only when it is the one row something can be done on. Every
other row stays visible and can be ticked by hand, so a link opened from production never
writes to staging or to a CI daemon unless the reader asks for it.

| The source                                        | Its row offers                               |
|---------------------------------------------------|----------------------------------------------|
| relays, and the Hub mirrors no ack from it        | Acknowledge                                  |
| relays, and holds an ack taken at runtime         | Revoke, beside who took it, when and why     |
| relays, and holds an ack of the CI baseline       | nothing                                      |
| relays, and its ack state is unknown              | both buttons, and the row names `acks_state` |
| has no ack credential, or is no longer configured | nothing, and the row says which              |

A source relays when `ack_relay` is true on `/api/sources`. An ack read is conclusive at
`ok` and `truncated`, the two states the Hub mirrors acks from. Anywhere else the ack state
is unknown: the Hub cannot tell an unacknowledged finding from one already acked, which is
the case of every daemon below 0.24.0 and of any daemon whose last ack read failed. The
relay would still answer, so such a row stays actionable and both buttons apply to it when
it is checked. The daemon refuses the one that does not apply, an ack of a finding already
acked or a revoke of one that is not, and its answer shows in that source's result line. A
row whose ack the Hub already mirrors keeps Revoke, or nothing for a baseline ack, whatever
the state reads.

A baseline ack cannot be revoked here because it cannot be revoked anywhere at runtime: the
daemon's own API answers `404` to it, and the baseline changes by editing its file under
review, through a pull request. See [LIMITATIONS.md](LIMITATIONS.md).

One submit acknowledges on every checked source, because acks live in each daemon's own
store and nothing fans one out. The launcher sends one relay request per source, one after
the other, then prints one line per source, with the Hub's own reason on a refusal, and
reads the finding again so the rows show the new state. One source refusing costs the
others nothing. A row the reader unticked stays unticked through that read, so a second
submit never reaches a source they left out.

Who may use it is the relay's rule, not the page's: a `Hub:Auth` session, or the identity
a proxy sets once `Hub:AckRelay:TrustIdentityHeader` is on. Anybody else gets the relay's
`403` on submit, and the ack is taken in the name the Hub knows the caller by. See
[API.md](API.md#ack-relay) and [AUTHENTICATION.md](AUTHENTICATION.md#how-it-works).

### Pull requests only in an environment

A team that acknowledges through pull requests only in one environment, a CI daemon for
instance, gives that source no ack credential: `AckHeaderName` and `AckHeaderValue` stay
unset on it, see [CONFIGURATION.md](CONFIGURATION.md#the-ack-credential). Its row then
offers nothing, and the Hub has no write path to that daemon. The acks of its baseline are
still mirrored and shown on the row, with `source` reading `toml` in `acks` on
`/api/findings`, and they can only be changed in the file.

Do not set `[daemon.ack] enabled = false` on that daemon to close runtime acks, because the
daemon then stops loading the TOML baseline too, see the engine's
[ACK-WORKFLOW.md](https://github.com/Institut-du-Numerique-Responsable/perf-sentinel/blob/main/docs/ACK-WORKFLOW.md).

## When no grant reaches you

With `Hub:Access` on, `/api/status` answers `403` to a signed-in caller no grant reaches.
The Hub is up, so the launcher does not show its unreachable banner. Every route shows
[Your access](#your-access) under the banner "No grant on this Hub reaches you. An
administrator grants access in the Hub's configuration." The tabs and the version chip are
gone, since each tab would lead to the same refusal, and the identity chip still names the
caller. In place of an empty reach, a block to copy holds what an administrator needs: the
name, the id they list in `Admins` or `Members`, and the groups a grant can name. Any
other failure of `/api/status` keeps the unreachable banner. See
[ACCESS-CONTROL.md](ACCESS-CONTROL.md).

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-no-grant-dark.png">
  <img alt="The no-grant banner above the caller's access, with the request to send an administrator" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-no-grant.png">
</picture>

With access on, each source in `/api/sources` carries the caller's `permissions` there. The
New analysis form offers only the sources whose list holds `analyses.run` and says how many
it leaves out, as "2 of 4 configured · 2 need analyses.run". A daemon row on the Sources
screen unfolds only where the list holds `sources.read`, which its view needs, and says
"details need sources.read" under its name otherwise. Without a list, as with access off,
nothing is withheld.

## Your access

The identity chip links to `#/access` when `GET /api/access` answers, which is with
`Hub:Access` on; a `404` leaves the chip plain text, and a `#/access` link then opens a page
saying there is no access page on this Hub, as any unknown route says it has no screen. The page changes no right, since rights are declared in the
Hub's configuration (Helm values `hub.access`) and nowhere else. Its one action revokes a
terminal token. It shows:

1. **Signed in as.** The identity, how (browser session, terminal token or machine token)
   and until when, that a group change at the provider applies at the next sign-in, and
   "Your id, for an administrator", the subject as selectable text.
2. **Your groups.** Chips marked provider or internal.
3. **What you reach.** Project, group, role and permissions per grant. A grant on `*`
   reads "Everything, including records no project covers".
4. **Your terminal tokens**, from `GET /api/tokens`: the ones `perf-sentinel login` got for
   this caller, with the address the login was requested from, creation, expiry and last
   use ("never" until the first). **Revoke** asks once more, as **Confirm revoke**, and
   leaving the button or pressing Escape puts it back. The confirmed click sends
   `DELETE /api/tokens/{id}`, says in the card what happened (the terminal gets a `401` on
   its next request, or the token was already gone, or it is still valid and why) and moves
   the focus to the card. See [AUTHENTICATION.md](AUTHENTICATION.md#terminal-sign-in).
5. **Projects you reach**, for a member: each project those grants name, with its patterns
   written as `source lab-daemon · service checkout-* · attribute team=shop`. An
   administrator gets a link to Coverage instead.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-access-dark.png">
  <img alt="The access of a member of two team groups, with their terminal token" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-access.png">
</picture>

## The coverage screen

The Coverage tab appears for an administrator alone; a member who follows a `#/coverage`
link lands on their own access. The screen starts with what asks for a change:

1. **Needs attention.** The services, attribute values and sources no project covers, with
   their finding counts and the sources each was seen at by name. Findings no project
   covers are read only through grants on `*`. Each list stops at its first 200 entries
   and says so.
2. **Patterns to paste.** One YAML line per uncovered service, attribute and source, with
   a Copy button, ready to go under the `patterns` of a project in `hub.access.projects`.
   Values are quoted, so a `*` or a `=` keeps its meaning.
3. **Deployments that disagree on a project attribute.** The finding copies a project's
   attribute pattern reads but cannot write, because the copy carries two values of the
   key or was also seen under a grouping without it. An attribute grant reads them but
   cannot acknowledge them, see
   [ACCESS-CONTROL.md](ACCESS-CONTROL.md#reading-conflicts).
4. **Terminal tokens, everyone's**, with the same Revoke.
5. **Configuration**, as the Hub loaded it at its last start, one fold per part with its
   count: projects, roles (built-in ones marked), grants, internal groups and their
   members, admins by id and by group, machine tokens with their expiry as "expired", "in
   less than a day" or "in N days", never their value, and local accounts by name, never
   their hash. An empty part says "None declared."

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-coverage-dark.png">
  <img alt="The coverage screen of an admin: what no project covers, the patterns to paste, the conflicts, every terminal token and the configuration with the grants unfolded" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-coverage.png">
</picture>

The terminal sign-in page, `/device`, is not a launcher screen: the Hub serves it on its
own, with a policy that allows no framing and no inline script, for a browser approving a
`perf-sentinel login`. The code field takes the focus and accepts the code in any case,
with or without its dash. Once the code is verified, the focus moves to where and when it
was requested. A code from another network turns **Approve** into **Approve anyway**,
which the page announces, and the outcome is announced and takes the focus once the
buttons are gone.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/hub-device-dark.png">
  <img alt="The terminal approval page with a pending code entered, before Approve or Deny" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/hub-device.png">
</picture>

## The sign-in page

With a [local account](AUTHENTICATION.md#local-accounts) declared, a browser without a
session lands on the Hub's own sign-in page, `/auth/sign-in`, instead of the provider's.
It is not a launcher screen either: one self-contained page with its styles inline and no
script, since the launcher's files sit behind the sign-in. It carries the launcher's logo,
inline, its typeface, DM Sans written into the stylesheet as `data:` fonts, and its
palette, light or dark after the system. **Sign in with single sign-on** comes first and leads to the provider; **Sign in
with a local account** is folded below it, with the note that it is for when single
sign-on is unavailable and that every local sign-in is logged. After a refused attempt the
fold opens with the reason, never with what was typed. Either way the browser comes back to
the page it asked for. The launcher itself is unchanged: on a `401` it reloads, and the
reload is what reaches this page. A page that renders whether or not the provider answers
is the point: the launcher never sends a browser straight to a provider that is down.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/hub-sign-in-dark.png">
  <img alt="The Hub's own sign-in page, with the local account fold opened" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/hub-sign-in.png">
</picture>

## Safety

Nothing from the server is ever written with `innerHTML`. Every displayed string is a text
node.
