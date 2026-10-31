// The launcher's pure command builders. Node's own runner, no dependency and
// no build step, mirroring how launcher.js itself is authored.
//
// Only the quoting and the command shapes are covered: they are the one piece
// of non-trivial logic on the page, and a wrong quote would print a command
// that runs as something other than what it says.
const test = require("node:test");
const assert = require("node:assert");

globalThis.window = globalThis;
require("../PerfSentinelHub/wwwroot/text-en.js");
require("../PerfSentinelHub/wwwroot/text-fr.js");
require("../PerfSentinelHub/wwwroot/launcher.js");
const PSL = globalThis.PSL;

const tempo = {
    kind: "tempo",
    engine_subcommand: "tempo",
    base_url: "http://tempo.obs.svc:3200",
    auth_header_name: null
};

test("a plain value is left unquoted", () => {
    assert.equal(PSL.shq("order-service"), "order-service");
    assert.equal(PSL.shq("ns/svc:v1.2"), "ns/svc:v1.2");
});

test("a shell expansion is quoted into a literal", () => {
    // Double quotes would still expand this. Single quotes are the whole point.
    assert.equal(PSL.shq("$(whoami)"), "'$(whoami)'");
    assert.equal(PSL.shq("a`b`c"), "'a`b`c'");
});

test("an apostrophe closes and reopens rather than escaping in place", () => {
    // A backslash escapes nothing inside single quotes.
    assert.equal(PSL.shq("o'reilly svc"), "'o'\\''reilly svc'");
});

test("an empty value is quoted rather than dropped", () => {
    assert.equal(PSL.shq(""), "''");
});

test("a relative window becomes one lookback and a cap", () => {
    assert.equal(
        PSL.analysisCommand(tempo, {service: "order-service", max_traces: 100, lookback: "1h"}),
        "perf-sentinel tempo --endpoint http://tempo.obs.svc:3200 --service order-service \\\n"
        + "  --lookback 1h --max-traces 100");
});

test("an absolute window is whole seconds, the way the Hub writes them", () => {
    const command = PSL.analysisCommand(
        tempo,
        {service: "orders", max_traces: 250, from_ms: 1787835540600, to_ms: 1787838540400});

    assert.match(command, /--from 2026-08-27T12:59:00Z --to 2026-08-27T13:49:00Z/);
    // The engine refuses a lookback beside an absolute window.
    assert.ok(!command.includes("--lookback"));
});

test("a trace id carries neither a window nor a cap", () => {
    const command = PSL.analysisCommand(tempo, {trace_id: "abc123def456"});

    assert.equal(
        command,
        "perf-sentinel tempo --endpoint http://tempo.obs.svc:3200 --trace-id abc123def456");
    assert.ok(!command.includes("--max-traces"));
});

test("an authenticated source reads its header from the environment", () => {
    const command = PSL.analysisCommand(
        {...tempo, auth_header_name: "Authorization"},
        {service: "orders", max_traces: 10, lookback: "2h"});

    assert.ok(command.includes("--auth-header-env PERF_SENTINEL_SOURCE_TOKEN"));
    // The value never reaches the page, so it cannot reach the command.
    assert.ok(!command.includes("Bearer"));
});

test("changed thresholds point the engine at a file, having no flag of their own", () => {
    const command = PSL.analysisCommand(
        tempo,
        {service: "orders", max_traces: 10, lookback: "1h", detection: {max_fanout: 9}});

    // Undotted: a downloaded file may not keep a leading dot, so the name the
    // command asks for is the one the reader is most likely to actually have.
    assert.ok(command.includes("-c perf-sentinel.toml"));
    assert.ok(!command.includes("-c .perf-sentinel.toml"));
    assert.equal(PSL.detectionToml({max_fanout: 9, n_plus_one_min_occurrences: 8}),
        "[detection]\nmax_fanout = 9\nn_plus_one_min_occurrences = 8");
});

test("a string threshold is quoted in the config, a number is not", () => {
    // TOML has no bare words: unquoted, `strict` is a parse error the engine
    // reports before it reads anything else in the file.
    assert.equal(PSL.detectionToml({sanitizer_aware_classification: "strict", sanitizer_aware_min_cv: 0.75}),
        "[detection]\nsanitizer_aware_classification = \"strict\"\nsanitizer_aware_min_cv = 0.75");
});

test("a daemon has no command at all", () => {
    assert.equal(PSL.analysisCommand({engine_subcommand: null, base_url: "http://d:4318"}, {}), null);
});

test("the monitor keeps --daemon on query, where the engine puts it", () => {
    assert.equal(
        PSL.monitorCommand({base_url: "http://daemon.obs.svc:4318"}),
        "perf-sentinel query --daemon http://daemon.obs.svc:4318 monitor");
});

test("the shell note fires only when something was actually quoted", () => {
    assert.ok(!PSL.quotedForShell(PSL.analysisCommand(tempo, {service: "orders", max_traces: 1, lookback: "1h"})));
    assert.ok(PSL.quotedForShell(PSL.analysisCommand(tempo, {service: "two words", max_traces: 1, lookback: "1h"})));
});

test("a light refresh classifies from its gauges plus the kept hints", () => {
    // Mirrors DaemonView.Classify: a status-only body has no hints of its own.
    const at = {value: 90, capacity: 100, pct: 90, at_capacity: true};
    const under = {value: 1, capacity: 100, pct: 1, at_capacity: false};
    const unknown = {value: 1, capacity: null, pct: null, at_capacity: false};

    assert.equal(PSL.lightState({traces: at, analysis_queue: under, findings: under}, 0), "near_capacity");
    // The full gauge outranks the hints, exactly as the server rules it.
    assert.equal(PSL.lightState({traces: at, analysis_queue: under, findings: under}, 3), "near_capacity");
    assert.equal(PSL.lightState({traces: under, analysis_queue: under, findings: under}, 2), "advised");
    assert.equal(PSL.lightState({traces: unknown, analysis_queue: unknown, findings: unknown}, 0), "unknown");
    assert.equal(PSL.lightState({traces: under, analysis_queue: unknown, findings: under}, 0), "ok");
});

test("a light refresh only merges onto a view that carries the rest", () => {
    const full = {error_code: null, warnings: [], config: {api_enabled: true}, state: "ok"};

    assert.equal(PSL.mergeableView(undefined), null);
    assert.equal(PSL.mergeableView("loading"), null);
    assert.equal(PSL.mergeableView({error_code: "source_unreachable"}), null);
    // A light body has no warnings of its own, so it is never a base for another.
    assert.equal(PSL.mergeableView({error_code: null, traces: null}), null);
    assert.equal(PSL.mergeableView(full), full);
});

test("a merged light view keeps the settings and takes the gauges", () => {
    const under = {value: 1, capacity: 100, pct: 1, at_capacity: false};
    const at = {value: 99, capacity: 100, pct: 99, at_capacity: true};
    const previous = {
        error_code: null, observed_at_ms: 1000, version: "0.16.0", uptime_seconds: 10,
        warnings: [{kind: "tuning", message: "m"}], warnings_dropped: 0,
        config: {api_enabled: true}, state: "advised",
        traces: under, analysis_queue: under, findings: under
    };
    const light = {
        observed_at_ms: 2000, version: "0.16.0", uptime_seconds: 70,
        traces: at, analysis_queue: under, findings: under
    };

    const merged = PSL.mergeLight(previous, light);

    assert.equal(merged.observed_at_ms, 2000);
    assert.equal(merged.uptime_seconds, 70);
    assert.deepEqual(merged.traces, at);
    // What a light read cannot see is the last full read's, unchanged.
    assert.deepEqual(merged.config, previous.config);
    assert.deepEqual(merged.warnings, previous.warnings);
    // And the state follows the new gauges, not the state that came with them.
    assert.equal(merged.state, "near_capacity");
    // The previous view is left alone: the panel may still be rendering it.
    assert.equal(previous.state, "advised");
    assert.deepEqual(previous.traces, under);
});

test("what the next re-read of a row should cost", () => {
    const full = {error_code: null, warnings: []};
    const failed = {error_code: "network_error"};

    // Nothing on screen yet, or a read in flight: only a full read renders.
    assert.equal(PSL.refreshPlan(undefined, 0, 60000), "full");
    assert.equal(PSL.refreshPlan("loading", 0, 60000), "full");
    // A row that failed asks the cheap question until something answers.
    assert.equal(PSL.refreshPlan(failed, 0, 60000), "probe");
    assert.equal(PSL.refreshPlan(failed, 999999, 60000), "probe");
    // A row already showing a daemon rides on light reads until the full one is due.
    assert.equal(PSL.refreshPlan(full, 5000, 60000), "light");
    assert.equal(PSL.refreshPlan(full, 60000, 60000), "full");
    assert.equal(PSL.refreshPlan(full, 60001, 60000), "full");
    // A light body is never a base for another, so it reads in full instead.
    assert.equal(PSL.refreshPlan({error_code: null, traces: null}, 0, 60000), "full");
});

test("the download link points at the engine version the Hub runs", () => {
    assert.equal(PSL.releaseUrl("0.16.0"),
        "https://github.com/Institut-du-Numerique-Responsable/perf-sentinel/releases/tag/v0.16.0");
    assert.equal(PSL.releaseUrl("0.17.0-rc.1"),
        "https://github.com/Institut-du-Numerique-Responsable/perf-sentinel/releases/tag/v0.17.0-rc.1");
    // No version to pin, so the release list rather than a made-up tag.
    const list = "https://github.com/Institut-du-Numerique-Responsable/perf-sentinel/releases";
    assert.equal(PSL.releaseUrl(null), list);
    assert.equal(PSL.releaseUrl(""), list);
    assert.equal(PSL.releaseUrl("unknown"), list);
    // And nothing that is not a version is ever pasted into the path.
    assert.equal(PSL.releaseUrl("0.16.0/../../evil"), list);
    assert.equal(PSL.releaseUrl("0.16.0?x=1"), list);
    assert.equal(PSL.releaseUrl("javascript:alert(1)"), list);
});

test("the monitor command of a keyed daemon says the key comes first, outside the command", () => {
    // A line of prose rather than a flag: the key is read from the environment, so the
    // command itself stays the same in every shell.
    const hint = "Set `PERF_SENTINEL_DAEMON_API_KEY` to this daemon's read key first.";
    assert.equal(PSL.monitorKeyHint({auth_header_name: "X-API-Key"}), hint);
    assert.equal(PSL.monitorKeyHint({auth_header_name: "x-api-key"}), hint);
    assert.equal(PSL.monitorKeyHint({auth_header_name: null}), null);
    // The monitor sends that variable as X-API-Key and nothing else, so any other header,
    // an Ingress's own for one, is named as one the command cannot send.
    for (const header of ["Authorization", "X-Gateway-Token"]) {
        assert.equal(PSL.monitorKeyHint({auth_header_name: header}),
            "This daemon is behind an `" + header + "` header. `query monitor` takes no such flag, "
            + "so this command works only from somewhere that reaches the daemon without it.");
    }
    assert.doesNotMatch(PSL.monitorCommand({base_url: "http://d:4318", auth_header_name: "X-API-Key"}, 5),
        /API_KEY/);
});

test("the monitor command carries the interval the row is re-reading on", () => {
    const source = {id: "d", base_url: "http://daemon.svc:4318"};
    const bare = "perf-sentinel query --daemon http://daemon.svc:4318 monitor";

    assert.equal(PSL.monitorCommand(source, 30), bare + " --refresh 30");
    assert.equal(PSL.monitorCommand(source, 5), bare + " --refresh 5");
    // Not re-reading, so no interval to mirror: the engine keeps its own default.
    assert.equal(PSL.monitorCommand(source, 0), bare);
    assert.equal(PSL.monitorCommand(source), bare);
    // --daemon is the parent's, --refresh is the subcommand's, in that order.
    assert.match(PSL.monitorCommand(source, 10), /--daemon \S+ monitor --refresh 10$/);
    // And an address that needs quoting still gets it.
    assert.equal(PSL.monitorCommand({base_url: "http://a b"}, 5),
        "perf-sentinel query --daemon 'http://a b' monitor --refresh 5");
});

test("only open folds are worth remembering", () => {
    assert.deepEqual(PSL.openFolds({a: true, b: false, c: true}), {a: true, c: true});
    // Closed is the default, so nothing closed is written down.
    assert.deepEqual(PSL.openFolds({a: false}), {});
    assert.deepEqual(PSL.openFolds({}), {});
    assert.deepEqual(PSL.openFolds(null), {});
    assert.deepEqual(PSL.openFolds(undefined), {});
    // And nothing that is not exactly true counts as open.
    assert.deepEqual(PSL.openFolds({a: "true", b: 1, c: {}, d: true}), {d: true});
});

test("a gauge takes a tone only once it is close to a cap it published", () => {
    // 90 is the engine's own advisor line, the one that turns the row's verdict.
    assert.equal(PSL.gaugeTone(90), "crit");
    assert.equal(PSL.gaugeTone(100), "crit");
    assert.equal(PSL.gaugeTone(89.9), "warn");
    assert.equal(PSL.gaugeTone(75), "warn");
    assert.equal(PSL.gaugeTone(74.9), null);
    assert.equal(PSL.gaugeTone(0), null);
    // No published cap, so nothing is known about how close to one it is.
    assert.equal(PSL.gaugeTone(null), null);
    assert.equal(PSL.gaugeTone(undefined), null);
    assert.equal(PSL.gaugeTone(NaN), null);
    assert.equal(PSL.gaugeTone("90"), null);
});

test("a gauge move is what changed, and nothing when nothing did", () => {
    const at = value => ({value, capacity: 10000});

    assert.equal(PSL.gaugeMove(at(8057), at(9310)), 1253);
    assert.equal(PSL.gaugeMove(at(9310), at(9251)), -59);
    // Unchanged, unknown either side, or no earlier reading at all.
    assert.equal(PSL.gaugeMove(at(9310), at(9310)), null);
    assert.equal(PSL.gaugeMove(null, at(9310)), null);
    assert.equal(PSL.gaugeMove(at(9310), null), null);
    assert.equal(PSL.gaugeMove({value: null}, at(9310)), null);
    assert.equal(PSL.gaugeMove(at(9310), {value: null}), null);
    // Zero is a real reading, not a missing one.
    assert.equal(PSL.gaugeMove(at(0), at(12)), 12);
    assert.equal(PSL.gaugeMove(at(12), at(0)), -12);
});

test("PowerShell quotes by doubling, and keeps its own operators out of bare words", () => {
    // Inside single quotes everything is literal and a quote is doubled, where a
    // POSIX shell has to close, escape and reopen.
    assert.equal(PSL.psq("a b'c"), "'a b''c'");
    assert.equal(PSL.shq("a b'c"), "'a b'\\''c'");
    // A comma is PowerShell's array operator and @ opens a splat, so neither
    // stays bare even though a POSIX shell would leave them alone.
    assert.equal(PSL.psq("a,b"), "'a,b'");
    assert.equal(PSL.psq("@thing"), "'@thing'");
    assert.equal(PSL.shq("a,b"), "a,b");
    // What both leave alone: a URL, a name, a number, an ISO timestamp.
    assert.equal(PSL.psq("http://tempo.svc:3200"), "http://tempo.svc:3200");
    assert.equal(PSL.psq("order-service"), "order-service");
    // Nothing in an ISO timestamp is special to either shell, so both pass it bare.
    assert.equal(PSL.psq("2026-08-29T09:00:00.000Z"), "2026-08-29T09:00:00.000Z");
    assert.equal(PSL.shq("2026-08-29T09:00:00.000Z"), "2026-08-29T09:00:00.000Z");
    // And the empty string is quoted by both, or it would vanish.
    assert.equal(PSL.psq(""), "''");
});

test("the shell a first visit gets follows the platform", () => {
    assert.equal(PSL.defaultShell("Win32"), "powershell");
    assert.equal(PSL.defaultShell("Windows"), "powershell");
    assert.equal(PSL.defaultShell("MacIntel"), "posix");
    assert.equal(PSL.defaultShell("Linux x86_64"), "posix");
    // Nothing to go on is not Windows, so it is the line most machines run.
    // "Darwin" contains "win", so the test has to be anchored.
    assert.equal(PSL.defaultShell("Darwin"), "posix");
    assert.equal(PSL.defaultShell("darwin"), "posix");
    assert.equal(PSL.defaultShell(null), "posix");
    assert.equal(PSL.defaultShell(""), "posix");
    assert.equal(PSL.shellById("nonsense").id, "posix");
    assert.equal(PSL.shellById("powershell").label, "PowerShell");
});

test("a command continues its line the way its own shell does", () => {
    const source = {engine_subcommand: "tempo", base_url: "http://tempo.svc:3200"};
    const request = {service: "orders", lookback: "1h", max_traces: 100, detection: {}};

    const posix = PSL.analysisCommand(source, request, "posix");
    const pwsh = PSL.analysisCommand(source, request, "powershell");
    assert.match(posix, /\\\n {2}--lookback/);
    assert.match(pwsh, /`\n {2}--lookback/);
    // The backslash never appears in the PowerShell line, and vice versa.
    assert.ok(!pwsh.includes("\\"));
    assert.ok(!posix.includes("`"));
    // The monitor command takes its shell too.
    assert.equal(PSL.monitorCommand({base_url: "http://a b"}, 5, "powershell"),
        "perf-sentinel query --daemon 'http://a b' monitor --refresh 5");
});

test("setting an environment variable is written the way each shell writes it", () => {
    const header = "Authorization: …";
    assert.equal(PSL.exportLine("posix", "PERF_SENTINEL_SOURCE_TOKEN", header),
        "export PERF_SENTINEL_SOURCE_TOKEN='Authorization: …'");
    // PowerShell assigns into the env: drive, and wants the spaces.
    assert.equal(PSL.exportLine("powershell", "PERF_SENTINEL_SOURCE_TOKEN", header),
        "$env:PERF_SENTINEL_SOURCE_TOKEN = 'Authorization: …'");
    // A quote in the value is escaped by each shell's own rule.
    assert.equal(PSL.exportLine("posix", "V", "a'b"), "export V='a'\\''b'");
    assert.equal(PSL.exportLine("powershell", "V", "a'b"), "$env:V = 'a''b'");
    // An unknown shell is the POSIX one, as everywhere else.
    assert.match(PSL.exportLine("nonsense", "V", "x"), /^export V=/);
});

test("updateState only speaks when both versions are known and one is older", () => {
    // Nothing to say, and the three silences are different situations.
    assert.equal(PSL.updateState(null, "0.17.0"), null, "no version running");
    assert.equal(PSL.updateState("0.16.0", null), null, "check off or not run yet");
    assert.equal(PSL.updateState("0.17.0", "0.17.0"), null, "current");
    // A build ahead of the newest release is a pre-release, not a downgrade.
    assert.equal(PSL.updateState("0.18.0", "0.17.0"), null, "ahead");

    assert.deepEqual(PSL.updateState("0.16.0", "0.17.0"), {latest: "0.17.0"});
    assert.deepEqual(PSL.updateState("0.14.2", "0.17.0"), {latest: "0.17.0"});
    // The Hub's own version has a fourth segment that never carries meaning.
    assert.equal(PSL.updateState("0.1.0.0", "0.1.0"), null, "four segments, same release");
    assert.deepEqual(PSL.updateState("0.1.0.0", "0.2.0"), {latest: "0.2.0"});
});

test("hubReleaseUrl lands on the list, which exists before any release does", () => {
    assert.equal(PSL.hubReleaseUrl(), "https://github.com/Institut-du-Numerique-Responsable/PerfSentinelHub/releases");
});

test("knownShell answers null for anything that is not a shell id", () => {
    assert.equal(PSL.knownShell("posix"), "posix");
    assert.equal(PSL.knownShell("powershell"), "powershell");
    // shellById would answer "posix" for all of these, which is right for
    // spelling a command and wrong for judging a remembered value.
    assert.equal(PSL.knownShell("fish"), null);
    assert.equal(PSL.knownShell(""), null);
    assert.equal(PSL.knownShell(null), null);
    assert.equal(PSL.knownShell(undefined), null);
});

test("durPrecise always reaches the seconds a countdown is watched by", () => {
    // dur() stops at two units, which hides the only figure that moves.
    assert.equal(PSL.dur(86_399_000), "23 h 59 m");
    assert.equal(PSL.durPrecise(86_399_000), "23 h 59 m 59 s");
    assert.equal(PSL.durPrecise(90_061_000), "1 d 1 h 1 m 1 s");
    assert.equal(PSL.durPrecise(59_000), "59 s");
    assert.equal(PSL.durPrecise(0), "0 s");
    // A report already gone must not read as one second from expiry.
    assert.equal(PSL.durPrecise(-5_000), "0 s");
    assert.equal(PSL.durPrecise(null), "n/a");
});

test("durMinutes keeps the units dur() drops from an uptime", () => {
    // dur() stops at two, so a daemon up ten days and twenty-three hours reads
    // the same as one up ten days flat.
    assert.equal(PSL.dur(950_400_000), "11 d");
    assert.equal(PSL.durMinutes(950_400_000), "11 d 0 h 0 m");
    assert.equal(PSL.durMinutes(888_120_000), "10 d 6 h 42 m");
    assert.equal(PSL.durMinutes(3_600_000), "1 h 0 m");
    assert.equal(PSL.durMinutes(90_000), "1 m");
    // No seconds: the figure is re-read on an interval, so they would be stale.
    assert.equal(PSL.durMinutes(59_000), "0 m");
    assert.equal(PSL.durMinutes(null), "n/a");
});

test("splitByKind labels a fleet only when it holds both kinds", () => {
    const mixed = PSL.splitByKind([
        {kind: "daemon"}, {kind: "tempo"}, {kind: "daemon"}, {kind: "jaeger_query"}
    ]);
    assert.equal(mixed.split, true);
    // The original positions survive: the fold ids on the fleet table are built
    // from them, so a grouped row must still address its own source.
    assert.deepEqual(mixed.daemons.map((e) => e.index), [0, 2]);
    assert.deepEqual(mixed.backends.map((e) => e.index), [1, 3]);

    // One kind needs no label: it would name the only thing on screen.
    assert.equal(PSL.splitByKind([{kind: "daemon"}, {kind: "daemon"}]).split, false);
    assert.equal(PSL.splitByKind([{kind: "tempo"}]).split, false);
    assert.equal(PSL.splitByKind([]).split, false);
    assert.equal(PSL.splitByKind(null).split, false);

    // An unknown kind is a backend, matching the engine: only a daemon is polled.
    const odd = PSL.splitByKind([{kind: "daemon"}, {kind: "something-new"}]);
    assert.equal(odd.split, true);
    assert.deepEqual(odd.backends.map((e) => e.index), [1]);
});

test("incidentCapture reads oldest_finding_ms the way the daemon does", () => {
    // At or below the window's start, the ring still reached the whole window.
    assert.equal(PSL.incidentCapture({oldest_finding_ms: 100, window_from_ms: 100}), "complete");
    assert.equal(PSL.incidentCapture({oldest_finding_ms: 99, window_from_ms: 100}), "complete");
    // Above it, part of the window was already gone when the incident was frozen.
    assert.equal(PSL.incidentCapture({oldest_finding_ms: 101, window_from_ms: 100}), "partial");
    // Absent means the ring held nothing at all.
    assert.equal(PSL.incidentCapture({window_from_ms: 100}), "empty");
    assert.equal(PSL.incidentCapture({oldest_finding_ms: null, window_from_ms: 100}), "empty");
});

test("findingPhase places a finding before or after the incident by its stamp", () => {
    const incident = {at_ms: 1000};
    assert.equal(PSL.findingPhase({first_seen_ms: 999}, incident), "before");
    assert.equal(PSL.findingPhase({first_seen_ms: 1000}, incident), "before");
    assert.equal(PSL.findingPhase({first_seen_ms: 1001}, incident), "after");
});

test("every incident kind the daemon emits has a label", () => {
    assert.deepEqual(
        Object.keys(PSL.INCIDENT_KIND_LABEL).sort(),
        ["deploy", "memory_saturation", "oom_kill", "other", "restart"]);
});

test("incidentsCopy dates a daemon's copy and names what the read came to", () => {
    const read = {name: "Production", incidents_read_ms: 1_000_000, incidents_state: "ok"};
    // A successful read says nothing beyond its age: the age is the answer.
    assert.equal(PSL.incidentsCopy(read, 1_042_000), "Production: read 42 s ago");
    // No row at all is not the epoch, and not a quiet fleet either.
    assert.equal(PSL.incidentsCopy({name: "Production"}, 1_042_000), "Production: never read");
    assert.equal(
        PSL.incidentsCopy({name: "Production", incidents_read_ms: null}, 1_042_000),
        "Production: never read");
    // Anything else is named beside the age, or a refused key reads as silence.
    assert.equal(
        PSL.incidentsCopy({name: "Edge", incidents_read_ms: 0, incidents_state: "unauthorized"}, 60_000),
        "Edge: read 1 m ago, it refused the Hub's key");
    assert.equal(
        PSL.incidentsCopy({name: "Edge", incidents_read_ms: 0, incidents_state: "absent"}, 0),
        "Edge: read 0 s ago, it publishes no incidents route");
    // A clock that ran backwards between the read and the render is not a
    // negative age.
    assert.equal(PSL.incidentsCopy({name: "Edge", incidents_read_ms: 5_000}, 1_000), "Edge: read 0 s ago");
});

test("every incidents read state the Hub files has words, bar the one the age already tells", () => {
    assert.deepEqual(Object.keys(PSL.INCIDENT_READ_STATE).sort(), ["absent", "error", "unauthorized"]);
});

test("an incident's window travels to New analysis as a hash and back", () => {
    const incident = {
        id: "d650edad80ac5c2d99b8d1dde07100c2",
        service: "shop svc",
        window_from_ms: 1000,
        window_to_ms: 5000
    };
    const hash = PSL.incidentHandoffHash(incident, 9000);
    assert.equal(hash, "#/new?from=1000&to=5000&service=shop%20svc&incident=d650edad80ac5c2d99b8d1dde07100c2");
    assert.deepEqual(PSL.readHandoff(hash, 9000),
        {fromMs: 1000, toMs: 5000, service: "shop svc", incidentId: "d650edad80ac5c2d99b8d1dde07100c2", pairKey: ""});
});

test("the window's end is held at now, in the hash and again when it is read", () => {
    // An incident younger than two TTLs has a window still running, and the Hub
    // refuses a window that ends in the future.
    const incident = {id: "a", service: "svc", window_from_ms: 1000, window_to_ms: 5000};
    assert.match(PSL.incidentHandoffHash(incident, 3000), /&to=3000&/);
    // A shared link ages: the end it carries is held at the reader's own now.
    assert.equal(PSL.readHandoff("#/new?from=1000&to=5000&service=svc", 3000).toMs, 3000);
    assert.equal(PSL.readHandoff("#/new?from=1000&to=5000&service=svc", 9000).toMs, 5000);
});

test("a handoff the form cannot take reads as null", () => {
    assert.equal(PSL.readHandoff("#/new?from=1000&to=5000&service=", 9000), null, "empty service");
    assert.equal(PSL.readHandoff("#/new?from=1000&to=5000&service=%20", 9000), null, "blank service");
    assert.equal(PSL.readHandoff("#/new?from=1000&to=5000", 9000), null, "no service");
    assert.equal(PSL.readHandoff("#/new?from=5000&to=5000&service=svc", 9000), null, "from equal to to");
    assert.equal(PSL.readHandoff("#/new?from=6000&to=5000&service=svc", 9000), null, "from after to");
    assert.equal(PSL.readHandoff("#/new?from=abc&to=5000&service=svc", 9000), null, "non-numeric from");
    assert.equal(PSL.readHandoff("#/new?from=1000&to=&service=svc", 9000), null, "empty to");
    assert.equal(PSL.readHandoff("#/new?service=svc", 9000), null, "no bounds");
    // Held at now, the end can land at or before the start: a window entirely
    // in the future is not a window to run.
    assert.equal(PSL.readHandoff("#/new?from=9000&to=9500&service=svc", 9000), null, "all in the future");
    assert.equal(PSL.readHandoff("#/new", 9000), null, "no parameters");
    assert.equal(PSL.readHandoff("#/incidents?from=1000&to=5000&service=svc", 9000), null, "another route");
    assert.equal(PSL.readHandoff("", 9000), null);
    assert.equal(PSL.readHandoff(null, 9000), null);
    // The id is along for the banner, never a condition.
    assert.equal(PSL.readHandoff("#/new?from=1000&to=5000&service=svc", 9000).incidentId, "");
});

test("skew names the segment it compared on", () => {
    // The direction comes from all three segments, so the noun has to follow the
    // same one. Reading the minor alone called a patch gap "0 minor behind".
    PSL.setVersions("0.1.6", "0.20.1");
    assert.equal(PSL.skew("0.20.0").label, "1 patch behind");
    assert.equal(PSL.skew("0.20.0").dir, "behind");
    assert.equal(PSL.skew("0.19.0").label, "1 minor behind");
    assert.equal(PSL.skew("0.20.2").label, "1 patch ahead");
    assert.equal(PSL.skew("1.0.0").label, "1 major ahead");
    // Equal versions carry no pill at all, whatever the extra segments.
    assert.equal(PSL.skew("0.20.1"), null);
    assert.equal(PSL.skew(null), null);
});

test("finding types read as labels, and an unknown one reads as itself", () => {
    // The engine's twelve, worded as its own dashboard words them.
    assert.equal(PSL.FINDING_TYPE_LABEL.n_plus_one_sql, "N+1 SQL");
    assert.equal(PSL.FINDING_TYPE_LABEL.excessive_fanout, "Excessive fanout");
    assert.equal(PSL.FINDING_TYPE_LABEL.serialized_calls, "Serialized calls");
    assert.equal(Object.keys(PSL.FINDING_TYPE_LABEL).length, 12);
    // The Hub stores whatever string it is sent, so a type outside the twelve
    // reaches the screen. It has to render as itself, never as undefined.
    assert.equal(PSL.FINDING_TYPE_LABEL.blocking_wait, undefined);
    assert.equal(PSL.FINDING_TYPE_LABEL.blocking_wait || "blocking_wait", "blocking_wait");
});

// ------------------------------------------------------------ the ack page

const SIGNATURE = "n_plus_one_sql:shop-svc:GET__orders:1a22332fbc3bbc0b58c01ef7af1f4a31";

test("the query a Grafana link carries becomes the ack route", () => {
    assert.equal(PSL.ackEntryHash("?ack=" + encodeURIComponent(SIGNATURE)),
        "#/ack?signature=" + encodeURIComponent(SIGNATURE));
    // Whatever else a dashboard appends is not the launcher's to keep.
    assert.equal(PSL.ackEntryHash("?orgId=1&ack=abc"), "#/ack?signature=abc");
    assert.equal(PSL.ackEntryHash("?from=1&to=2"), null, "no ack parameter");
    assert.equal(PSL.ackEntryHash(""), null);
    assert.equal(PSL.ackEntryHash(null), null);
});

test("the context a Grafana link names travels into the ack route", () => {
    assert.equal(PSL.ackEntryHash("?ack=abc&environment=production&source_id=production-a"),
        "#/ack?signature=abc&environment=production&source_id=production-a");
    assert.equal(PSL.ackEntryHash("?ack=abc&environment=pre%20prod"), "#/ack?signature=abc&environment=pre%20prod");
    assert.equal(PSL.ackEntryHash("?ack=abc&source_id=prod%26x"), "#/ack?signature=abc&source_id=prod%26x");
    // Grafana sends a single space for the All choice of a variable.
    assert.equal(PSL.ackEntryHash("?ack=abc&environment=%20&source_id=+"), "#/ack?signature=abc");
    assert.equal(PSL.ackEntryHash("?ack=abc&environment=&source_id="), "#/ack?signature=abc");
    // A padded value would match no row and tick nothing, with no hint why.
    assert.equal(PSL.ackEntryHash("?ack=abc&environment=%20production%20&source_id=+production-a"),
        "#/ack?signature=abc&environment=production&source_id=production-a");
    assert.deepEqual(PSL.readAckRoute(PSL.ackEntryHash("?ack=abc&environment=production&source_id=production-a")),
        {signature: "abc", sourceId: "production-a", environment: "production"});
});

test("a signature with slashes, colons and percent signs survives both hops", () => {
    const odd = "slow_http:edge/gw:GET /a%2Fb?x=1&y=2#frag:100%";
    const hash = PSL.ackEntryHash("?ack=" + encodeURIComponent(odd));
    assert.deepEqual(PSL.readAckRoute(hash), {signature: odd, sourceId: null, environment: null});
});

test("an ack link with an empty value still lands on the ack page, which says so", () => {
    // Null here would drop the reader on New analysis with nothing explaining it.
    assert.equal(PSL.ackEntryHash("?ack="), "#/ack?signature=");
    assert.equal(PSL.readAckRoute("#/ack?signature="), null);
});

test("the ack route carries a signature and an optional source", () => {
    assert.equal(PSL.ackRouteHash("a/b:c", "prod&x"), "#/ack?signature=a%2Fb%3Ac&source_id=prod%26x");
    assert.deepEqual(PSL.readAckRoute("#/ack?signature=a%2Fb%3Ac&source_id=prod%26x"),
        {signature: "a/b:c", sourceId: "prod&x", environment: null});
    assert.deepEqual(PSL.readAckRoute(PSL.ackRouteHash(SIGNATURE, "production-a")),
        {signature: SIGNATURE, sourceId: "production-a", environment: null});
    assert.deepEqual(PSL.readAckRoute(PSL.ackRouteHash(SIGNATURE, null)),
        {signature: SIGNATURE, sourceId: null, environment: null});
});

test("the ack route carries an optional environment, and a blank one reads as none", () => {
    assert.equal(PSL.ackRouteHash("abc", null, "pre prod"), "#/ack?signature=abc&environment=pre%20prod");
    assert.deepEqual(PSL.readAckRoute(PSL.ackRouteHash(SIGNATURE, "production-a", "production")),
        {signature: SIGNATURE, sourceId: "production-a", environment: "production"});
    assert.deepEqual(PSL.readAckRoute("#/ack?signature=abc&environment=staging"),
        {signature: "abc", sourceId: null, environment: "staging"});
    // Blank is no context, never a refusal: the link still names its finding.
    assert.deepEqual(PSL.readAckRoute("#/ack?signature=abc&environment=%20&source_id=%20"),
        {signature: "abc", sourceId: null, environment: null});
    assert.deepEqual(PSL.readAckRoute("#/ack?signature=abc&environment=&source_id="),
        {signature: "abc", sourceId: null, environment: null});
    // A hand-written route is read the way the entry link is, without its padding.
    assert.deepEqual(PSL.readAckRoute("#/ack?signature=abc&environment=%20production&source_id=production-a%20"),
        {signature: "abc", sourceId: "production-a", environment: "production"});
});

test("an ack route the page cannot act on reads as null", () => {
    assert.equal(PSL.readAckRoute("#/new?signature=abc"), null, "another route");
    assert.equal(PSL.readAckRoute("#/ack"), null, "no parameters");
    assert.equal(PSL.readAckRoute("#/ack?source_id=production-a"), null, "missing signature");
    // The Hub reads a blank signature as no filter at all, and would answer with
    // whichever finding comes first.
    assert.equal(PSL.readAckRoute("#/ack?signature=%20"), null, "blank signature");
    assert.notEqual(PSL.readAckRoute("#/ack?signature=" + "a".repeat(1024)), null);
    assert.equal(PSL.readAckRoute("#/ack?signature=" + "a".repeat(1025)), null, "too long");
    assert.equal(PSL.readAckRoute("#/ack?signature=a%0Ab"), null, "control character");
    assert.equal(PSL.readAckRoute("#/ack?signature=a%C2%85b"), null, "C1 control character");
    assert.notEqual(PSL.readAckRoute("#/ack?signature=abc&environment=" + "e".repeat(256)), null);
    assert.equal(PSL.readAckRoute("#/ack?signature=abc&environment=" + "e".repeat(257)), null, "long environment");
    assert.equal(PSL.readAckRoute("#/ack?signature=abc&environment=prod%0Auction"), null, "control in environment");
    assert.equal(PSL.readAckRoute(""), null);
    assert.equal(PSL.readAckRoute(null), null);
});

const ackSources = [
    {id: "production-a", name: "Production A", environment: "production", ack_relay: true, acks_state: "ok"},
    {id: "production-b", name: "Production B", environment: "production", ack_relay: true, acks_state: "ok"},
    {id: "staging-a", name: "Staging A", environment: "staging", ack_relay: true, acks_state: "ok"},
    {id: "ci", name: "CI", environment: "ci", ack_relay: true, acks_state: "truncated"},
    {id: "edge", name: "Edge", environment: "production", ack_relay: false, acks_state: "ok"},
    {id: "old", name: "Old", environment: "staging", ack_relay: true, acks_state: "absent"},
    {id: "fresh", name: "Fresh", environment: "staging", ack_relay: true, acks_state: null}
];
const daemonAck = {source_id: "staging-a", source: "daemon", by: "robin", reason: "known", at: "2026-09-20T10:00:00Z"};
const tomlAck = {source_id: "ci", source: "toml", by: "ci", at: "2026-09-01T00:00:00Z"};

function carriedBy(ids, acks) {
    return {
        sources: ids.map((id) => ({id, name: "carried " + id, environment: "carried"})),
        acks
    };
}

test("a relaying source with no known ack offers the ack", () => {
    assert.deepEqual(PSL.ackRows(carriedBy(["production-a"]), ackSources, null), [{
        id: "production-a", name: "Production A", environment: "production",
        relay: true, ack: null, action: "ack", checked: true, note: null
    }]);
    // A truncated listing is still a listing: the Hub mirrors acks from it.
    assert.equal(PSL.ackRows(carriedBy(["ci"]), ackSources, null)[0].action, "ack");
    assert.deepEqual(PSL.ackRows({}, ackSources, null), []);
});

test("a runtime ack can be revoked, a CI baseline ack cannot", () => {
    const rows = PSL.ackRows(carriedBy(["staging-a", "ci"], [daemonAck, tomlAck]), ackSources, null);
    assert.equal(rows[0].action, "revoke");
    assert.equal(rows[0].ack, daemonAck);
    assert.equal(rows[0].checked, true);
    assert.equal(rows[0].note, null);
    assert.equal(rows[1].action, "none");
    assert.equal(rows[1].ack, tomlAck);
    assert.equal(rows[1].checked, false);
    assert.match(rows[1].note, /CI baseline/);
    assert.match(rows[1].note, /pull request/);
});

test("a source without an ack credential offers nothing and says why", () => {
    const row = PSL.ackRows(carriedBy(["edge"]), ackSources, null)[0];
    assert.equal(row.action, "none");
    assert.equal(row.checked, false);
    assert.equal(row.relay, false);
    assert.match(row.note, /ack credential/);
});

test("a relaying source whose ack state is unknown takes either action", () => {
    // A daemon below 0.24.0 is absent for good, and one failed read must not
    // hide Acknowledge until the next good poll: the relay would still answer.
    const states = ["absent", "error", "unauthorized", null];
    const sources = states.map((state, index) => (
        {id: "s" + index, name: "S" + index, environment: "staging", ack_relay: true, acks_state: state}));
    const rows = PSL.ackRows(carriedBy(sources.map((source) => source.id)), sources, {environment: "staging"});
    assert.deepEqual(rows.map((row) => row.action), ["either", "either", "either", "either"]);
    assert.deepEqual(rows.map((row) => row.checked), [true, true, true, true]);
    ["absent", "error", "unauthorized", "never read"].forEach((named, index) => {
        assert.match(rows[index].note, /unknown/);
        assert.match(rows[index].note, new RegExp("acks_state is " + named));
        assert.match(rows[index].note, /the daemon refuses the one that does not/);
        assert.match(rows[index].note, /result line/);
    });
});

test("an ack the Hub knows decides the action whatever the ack state reads", () => {
    const stale = [
        {id: "staging-a", name: "Staging A", environment: "staging", ack_relay: true, acks_state: "error"},
        {id: "ci", name: "CI", environment: "ci", ack_relay: true, acks_state: "error"}
    ];
    const rows = PSL.ackRows(carriedBy(["staging-a", "ci"], [daemonAck, tomlAck]), stale, null);
    assert.deepEqual(rows.map((row) => row.action), ["revoke", "none"]);
    assert.match(rows[1].note, /CI baseline/);
});

test("a source the link names is the only one checked", () => {
    const finding = carriedBy(["production-a", "staging-a", "ci"], [tomlAck]);
    const checked = (scope) => PSL.ackRows(finding, ackSources, scope).map((row) => row.checked);
    assert.deepEqual(checked({sourceId: "staging-a", environment: null}), [false, true, false]);
    // The source is the narrower context, so it wins over the environment.
    assert.deepEqual(checked({sourceId: "staging-a", environment: "production"}), [false, true, false]);
    // Naming a row nothing can be done on checks nothing.
    assert.deepEqual(checked({sourceId: "ci", environment: null}), [false, false, false]);
    assert.deepEqual(checked({sourceId: "elsewhere", environment: "production"}), [false, false, false]);
});

test("an environment the link names checks its sources and no other", () => {
    const finding = carriedBy(["production-a", "production-b", "edge", "staging-a", "old"]);
    const checked = (environment) => PSL.ackRows(finding, ackSources, {sourceId: null, environment})
        .map((row) => row.checked);
    // Edge is production too, and nothing can be done on it.
    assert.deepEqual(checked("production"), [true, true, false, false, false]);
    assert.deepEqual(checked("staging"), [false, false, false, true, true]);
    assert.deepEqual(checked("Production"), [false, false, false, false, false], "compared as written");
    // Every other row stays one the reader can tick by hand.
    const rows = PSL.ackRows(finding, ackSources, {sourceId: null, environment: "production"});
    assert.equal(rows[3].action, "ack");
    assert.equal(PSL.ackChecked(rows[3], {"staging-a": {action: "ack", checked: true}}), true);
});

test("a link with no context checks a source only when it is the one that can take an action", () => {
    const alone = PSL.ackRows(carriedBy(["production-a", "edge"]), ackSources, null);
    assert.deepEqual(alone.map((row) => row.checked), [true, false]);
    assert.deepEqual(PSL.ackRows(carriedBy(["production-a", "edge"]), ackSources, {}).map((row) => row.checked),
        [true, false]);
    // An ack hides a finding, so several candidates are the reader's to pick.
    const several = PSL.ackRows(carriedBy(["production-a", "staging-a", "old"]), ackSources, null);
    assert.deepEqual(several.map((row) => row.action), ["ack", "ack", "either"]);
    assert.deepEqual(several.map((row) => row.checked), [false, false, false]);
});

test("a source the finding lists and the Hub no longer configures keeps its name", () => {
    const rows = PSL.ackRows(carriedBy(["gone"]), ackSources, null);
    assert.deepEqual(rows, [{
        id: "gone", name: "carried gone", environment: "carried",
        relay: false, ack: null, action: "none", checked: false, note: rows[0].note
    }]);
    // Not the credential note: adding one would mend nothing, the source is gone.
    assert.match(rows[0].note, /no longer configures/);
    assert.doesNotMatch(rows[0].note, /credential/);
    assert.equal(PSL.ackRows(carriedBy(["gone"]), null, null)[0].action, "none");
});

test("a source that only counted the finding is no row of the ack form", () => {
    const finding = {
        sources: [
            {id: "production-a", name: "Production A", environment: "production", producer_version: "0.37.0"},
            {id: "staging-a", name: "Staging A", environment: "staging", producer_version: null}
        ]
    };
    const rows = PSL.ackRows(finding, ackSources, null);
    assert.deepEqual(rows.map((row) => row.id), ["production-a"]);
    // The count-only source is no candidate either, so the one copy is ticked.
    assert.equal(rows[0].checked, true);
});

test("a detection count reads as ?, N or at least N, grouped by thousands", () => {
    assert.equal(PSL.formatDetections(null, null), "?");
    assert.equal(PSL.formatDetections(undefined, undefined), "?");
    assert.equal(PSL.formatDetections(0, true), "0");
    assert.equal(PSL.formatDetections(1234567, true), "1,234,567");
    assert.equal(PSL.formatDetections(12345, false), "\u2265 12,345");
    assert.equal(PSL.formatDetections(0, false), "\u2265 0");
});

test("the detection window spans seven UTC days ending today", () => {
    const day = 86400000;
    const now = 20400 * day + 15 * 3600000;
    // The Hub counts whole days from the from day to the to day, both included.
    assert.deepEqual(PSL.detectionWindow(now, 7), {from: 20394 * day, to: now});
    assert.deepEqual(PSL.detectionWindow(20400 * day, 1), {from: 20400 * day, to: 20400 * day});
});

test("every reason a count falls short has its own phrase", () => {
    const reasons = ["uncovered_days", "open_day", "restart", "sampling", "shed", "ingest_rejected",
        "overflow", "multi_instance"];
    const phrases = reasons.map(PSL.gapLabel);
    phrases.forEach((phrase, index) => {
        assert.notEqual(phrase, reasons[index], reasons[index] + " is worded");
        assert.match(phrase, /^[a-z][a-z ]+$/);
    });
    assert.equal(new Set(phrases).size, reasons.length, "no two reasons share a phrase");
    // A reason a newer engine adds reads as it is sent rather than as nothing.
    assert.equal(PSL.gapLabel("new_reason"), "new_reason");
    assert.equal(PSL.gapLabel("constructor"), "constructor");
});

const ACK_NOW = Date.parse("2026-09-20T15:00:00Z");
const IN_PRODUCTION = {sourceId: null, environment: "production"};
const IN_STAGING = {sourceId: null, environment: "staging"};

function ackOn(id) {
    return {...daemonAck, source_id: id};
}

test("a tick only speaks for the action it was made on", () => {
    const row = PSL.ackRows(carriedBy(["staging-a"], [daemonAck]), ackSources, null)[0];
    assert.equal(PSL.ackChecked(row, {"staging-a": {action: "revoke", checked: false}}), false);
    assert.equal(PSL.ackChecked(row, {"staging-a": {action: "ack", checked: false}}), true, "made before the ack");
    assert.equal(PSL.ackChecked(row, {}), true);
    assert.equal(PSL.ackChecked(row, null), true);
    // A tick cannot check a row nothing can be done on.
    const dead = PSL.ackRows(carriedBy(["edge"]), ackSources, null)[0];
    assert.equal(PSL.ackChecked(dead, {edge: {action: "none", checked: true}}), false);
});

test("a tick on a row that takes either action speaks for both, and so does one read against it", () => {
    const either = PSL.ackRows(carriedBy(["old"]), ackSources, IN_STAGING)[0];
    assert.equal(either.action, "either");
    assert.equal(PSL.ackChecked(either, {}), true);
    assert.equal(PSL.ackChecked(either, {old: {action: "either", checked: false}}), false);
    // The next read of that daemon came back good: the row the reader left out
    // now reads Acknowledge, in the link's environment, and must stay out.
    const known = ackSources.map((source) => (source.id === "old" ? {...source, acks_state: "ok"} : source));
    const settled = PSL.ackRows(carriedBy(["old"]), known, IN_STAGING)[0];
    assert.equal(settled.action, "ack");
    assert.equal(PSL.ackChecked(settled, {old: {action: "either", checked: false}}), false);
    // And the other way round, when a read fails after the reader left a row out.
    assert.equal(PSL.ackChecked(either, {old: {action: "ack", checked: false}}), false);
});

test("a source the reader unticked is not written to after the rows are read again", () => {
    const ids = ["production-a", "production-b", "ci"];
    const ticks = {"production-b": {action: "ack", checked: false}};
    const before = PSL.ackPlan(PSL.ackRows(carriedBy(ids), ackSources, IN_PRODUCTION), ticks, "known", "", ACK_NOW);
    assert.deepEqual(before.ack.map((row) => row.id), ["production-a"]);
    // That submit acked one source, which now reads Revoke. A second Acknowledge
    // must not reach the one the reader left out.
    const acked = carriedBy(ids, [ackOn("production-a")]);
    const after = PSL.ackPlan(PSL.ackRows(acked, ackSources, IN_PRODUCTION), ticks, "known", "", ACK_NOW);
    assert.deepEqual(after.ack, []);
    assert.deepEqual(after.revoke.map((row) => row.id), ["production-a"]);
    assert.equal(after.blocker, "No checked source can take an ack.");
    assert.equal(after.blocked, false, "Revoke is ready");
});

test("a checked row that takes either action counts for both buttons", () => {
    const rows = PSL.ackRows(carriedBy(["staging-a", "old", "fresh"]), ackSources, IN_STAGING);
    assert.deepEqual(rows.map((row) => row.action), ["ack", "either", "either"]);
    const plan = PSL.ackPlan(rows, {fresh: {action: "either", checked: false}}, "known", "", ACK_NOW);
    assert.deepEqual(plan.ack.map((row) => row.id), ["staging-a", "old"]);
    assert.deepEqual(plan.revoke.map((row) => row.id), ["old"]);
    assert.equal(plan.blocker, null);
    // The Hub mirrors no ack from that row, so the sentence must not claim one.
    assert.equal(plan.sentence,
        "Acknowledge writes to 2 sources, with no expiry. Revoke asks 1 source to remove the ack, if there is one.");
    // Alone, it keeps Revoke ready while Acknowledge waits for its reason.
    const alone = PSL.ackPlan(PSL.ackRows(carriedBy(["old"]), ackSources, null), {}, "", "", ACK_NOW);
    assert.deepEqual(alone.ack.map((row) => row.id), ["old"]);
    assert.deepEqual(alone.revoke.map((row) => row.id), ["old"]);
    assert.equal(alone.blocker, "An ack needs a reason.");
    assert.equal(alone.blocked, false);
    assert.equal(alone.sentence, "An ack needs a reason. Revoke asks 1 source to remove the ack, if there is one.");
    // One row of unknown state is enough to hedge the whole list.
    const mixed = PSL.ackRows(carriedBy(["staging-a", "old"], [daemonAck]), ackSources, IN_STAGING);
    assert.equal(PSL.ackPlan(mixed, {}, "", "", ACK_NOW).revoke.length, 2);
    assert.match(PSL.ackPlan(mixed, {}, "", "", ACK_NOW).sentence, /Revoke asks 2 sources to remove the ack, if/);
});

test("Acknowledge waits for a checked source, a reason and an expiry the Hub would take", () => {
    const rows = PSL.ackRows(carriedBy(["production-a", "production-b"]), ackSources, IN_PRODUCTION);
    assert.deepEqual(PSL.ackPlan(rows, {}, "  ", "", ACK_NOW), {
        ack: rows, revoke: [], expiresAt: null,
        blocker: "An ack needs a reason.", blocked: true, sentence: "An ack needs a reason."
    });
    const past = PSL.ackPlan(rows, {}, "known", "2026-09-19", ACK_NOW);
    assert.equal(past.blocker, "The expiry is in the past.");
    assert.equal(past.sentence, past.blocker);
    assert.equal(past.blocked, true);
    assert.deepEqual(PSL.ackPlan(rows, {}, "known", "2026-09-30", ACK_NOW), {
        ack: rows, revoke: [], expiresAt: "2026-09-30T23:59:59Z", blocker: null, blocked: false,
        sentence: "Acknowledge writes to 2 sources, until 2026-09-30T23:59:59Z."
    });
    const one = PSL.ackPlan(rows, {"production-b": {action: "ack", checked: false}}, "known", "", ACK_NOW);
    assert.equal(one.sentence, "Acknowledge writes to 1 source, with no expiry.");
    // Several candidates and no context: nothing is written until the reader ticks.
    const unticked = PSL.ackRows(carriedBy(["production-a", "staging-a"]), ackSources, null);
    assert.equal(PSL.ackPlan(unticked, {}, "known", "", ACK_NOW).blocker, "No checked source can take an ack.");
    const ticked = PSL.ackPlan(unticked, {"staging-a": {action: "ack", checked: true}}, "known", "", ACK_NOW);
    assert.deepEqual(ticked.ack.map((row) => row.id), ["staging-a"]);
    const none = PSL.ackPlan(PSL.ackRows(carriedBy(["edge"]), ackSources, null), {}, "known", "", ACK_NOW);
    assert.equal(none.blocker, "No checked source can take an ack.");
    assert.equal(none.sentence, "No checked source can take an ack or a revoke.");
    assert.equal(none.blocked, true);
});

test("the sentence speaks for Revoke too, and reads as a refusal only when both buttons are dead", () => {
    const mixed = PSL.ackRows(carriedBy(["production-a", "production-b"], [ackOn("production-b")]),
        ackSources, IN_PRODUCTION);
    const waiting = PSL.ackPlan(mixed, {}, "", "", ACK_NOW);
    assert.equal(waiting.sentence, "An ack needs a reason. Revoke removes the ack on 1 source.");
    assert.equal(waiting.blocked, false);
    assert.equal(PSL.ackPlan(mixed, {}, "known", "", ACK_NOW).sentence,
        "Acknowledge writes to 1 source, with no expiry. Revoke removes the ack on 1 source.");
    // A revoke needs no reason.
    const revocable = PSL.ackRows(carriedBy(["staging-a"], [daemonAck]), ackSources, null);
    const revoke = PSL.ackPlan(revocable, {}, "", "", ACK_NOW);
    assert.equal(revoke.sentence, "Revoke removes the ack on 1 source.");
    assert.equal(revoke.blocked, false);
    assert.deepEqual(revoke.revoke, revocable);
});

test("an ack body carries a trimmed reason, and an expiry only when one is set", () => {
    assert.deepEqual(PSL.ackBody("ack", SIGNATURE, "  known flake ", null),
        {signature: SIGNATURE, reason: "known flake"});
    assert.deepEqual(PSL.ackBody("ack", SIGNATURE, "known", "2026-09-30T23:59:59Z"),
        {signature: SIGNATURE, reason: "known", expires_at: "2026-09-30T23:59:59Z"});
    // A revoke names the finding and nothing else.
    assert.deepEqual(PSL.ackBody("revoke", SIGNATURE, "known", "2026-09-30T23:59:59Z"), {signature: SIGNATURE});
});

test("an expiry date becomes the end of that day in UTC", () => {
    const now = Date.parse("2026-09-20T15:00:00Z");
    assert.equal(PSL.ackExpiry("2026-09-30", now), "2026-09-30T23:59:59Z");
    // Today still has hours left in it.
    assert.equal(PSL.ackExpiry("2026-09-20", now), "2026-09-20T23:59:59Z");
    assert.equal(PSL.ackExpiry("", now), null, "no date is a permanent ack");
    assert.equal(PSL.ackExpiry(null, now), null);
});

test("an expiry the Hub would refuse is rejected before it is sent", () => {
    const now = Date.parse("2026-09-20T15:00:00Z");
    assert.throws(() => PSL.ackExpiry("2026-09-19", now), /past/);
    // West of Greenwich the local day can already be over in UTC.
    assert.throws(() => PSL.ackExpiry("2026-09-20", Date.parse("2026-09-21T02:00:00Z")), /past/);
    // A date input never sends these, a hand-written caller could.
    assert.throws(() => PSL.ackExpiry("2026-02-31", now), RangeError);
    assert.throws(() => PSL.ackExpiry("tomorrow", now), RangeError);
});

test("each relay outcome reads as one line, and never as a bare status", () => {
    assert.deepEqual(PSL.ackSummary([
        {name: "Production A", action: "ack", status: 204},
        {name: "Staging A", action: "revoke", status: 204},
        {name: "Edge", action: "ack", status: 409, detail: "Already acked at the daemon or by its CI baseline."},
        {name: "Old", action: "ack", status: 503},
        {name: "Fresh", action: "ack", status: 413},
        {name: "Lost", action: "revoke", status: 0},
        {name: "Odd", action: "ack", status: 418}
    ]), [
        {ok: true, text: "Production A: acknowledged."},
        {ok: true, text: "Staging A: ack revoked."},
        {ok: false, text: "Edge: Already acked at the daemon or by its CI baseline."},
        {ok: false, text: "Old: The Hub is already relaying two acks. Try again in a moment."},
        {ok: false, text: "Fresh: The request is larger than the Hub accepts."},
        {ok: false, text: "Lost: The Hub did not answer."},
        {ok: false, text: "Odd: The Hub refused the request with status 418."}
    ]);
    assert.deepEqual(PSL.ackSummary([]), []);
});

test("the button that does not apply to a row of unknown ack state reads as the daemon's own answer", () => {
    // A result carries the button pressed, never "either": the row is only
    // where the two may meet, and the relay's detail says which one the daemon refused.
    assert.deepEqual(PSL.ackSummary([
        {
            name: "Old", action: "ack", status: 409,
            detail: "This finding is already acked at the daemon or by its CI baseline."
        },
        {
            name: "Old", action: "revoke", status: 404,
            detail: "This finding is not acked at this daemon, or only by its CI baseline, which no API revokes."
        },
        {name: "Old", action: "revoke", status: 204}
    ]), [
        {ok: false, text: "Old: This finding is already acked at the daemon or by its CI baseline."},
        {
            ok: false,
            text: "Old: This finding is not acked at this daemon, or only by its CI baseline, which no API revokes."
        },
        {ok: true, text: "Old: ack revoked."}
    ]);
});

test("a 403 on the status means no grant reaches the caller, anything else that the Hub is down", () => {
    // Hub:Access answers 403 to a signed-in caller its grants do not reach: the
    // Hub is up, and an administrator is who can change that.
    assert.equal(PSL.shellBanner(403),
        "No grant on this Hub reaches you. An administrator grants access in the Hub's configuration.");
    assert.equal(PSL.shellBanner(500), null);
    assert.equal(PSL.shellBanner(undefined), null);
});

test("a pattern reads as its set parts, in source, service, attribute order", () => {
    assert.equal(PSL.describePattern({source: "lab-daemon", service: "checkout-*", attribute: "team=shop"}),
        "source lab-daemon · service checkout-* · attribute team=shop");
    assert.equal(PSL.describePattern({source: null, service: null, attribute: "team=shop"}), "attribute team=shop");
    assert.equal(PSL.describePattern({source: "lab-d", service: null, attribute: null}), "source lab-d");
});

test("a machine token's expiry reads as expired or as whole days left", () => {
    const now = Date.UTC(2026, 9, 13, 12, 0, 0);
    const day = 86400000;
    assert.equal(PSL.accessExpiry(now - 1, now), "expired");
    assert.equal(PSL.accessExpiry(now, now), "expired");
    assert.equal(PSL.accessExpiry(now + 3600000, now), "in less than a day");
    assert.equal(PSL.accessExpiry(now + day, now), "in 1 day");
    assert.equal(PSL.accessExpiry(now + 30 * day + 5, now), "in 30 days");
});

test("a source without a permissions list permits everything, as with access off", () => {
    assert.equal(PSL.permits({id: "a"}, "analyses.run"), true);
    assert.equal(PSL.permits({id: "a", permissions: ["findings.read"]}, "analyses.run"), false);
    assert.equal(PSL.permits({id: "a", permissions: ["findings.read", "sources.read"]}, "sources.read"), true);
    assert.equal(PSL.permits({id: "a", permissions: []}, "findings.read"), false);
});

test("the signed-in block names the caller, how, until when, and their id for an administrator", () => {
    const now = new Date(2026, 9, 13, 10, 40).getTime();
    const session = PSL.accessSignedIn({
        identity: "ada@example.com", subject: "2f6d0c1e", caller: "session",
        expires_at_ms: new Date(2026, 9, 13, 18, 40).getTime()
    }, now);
    assert.deepEqual(session, {
        line: "Signed in as ada@example.com, through a browser session, until 18:40.",
        note: "A group change at your provider applies at your next sign-in.",
        subjectLabel: "Your id, for an administrator: ",
        subject: "2f6d0c1e"
    });
    const machine = PSL.accessSignedIn({
        identity: "machine:ci", subject: "machine:ci", caller: "machine_token",
        expires_at_ms: new Date(2026, 10, 12, 9, 5).getTime()
    }, now);
    assert.equal(machine.line, "Signed in as machine:ci, through a machine token, until 2026-11-12 09:05.");
    assert.equal(machine.note, null);
});

test("what you reach spells a grant on * as everything, and Admins as your own id", () => {
    const rows = PSL.accessReachRows({
        reach: [
            {group: null, project: "*", role: "admin", permissions: ["findings.read", "access.admin"]},
            {group: "/teams/shop", project: "shop", role: "member", permissions: ["findings.read", "acks.write"]}
        ]
    });
    assert.deepEqual(rows, [
        {
            project: "Everything, including records no project covers", group: "your id, in Admins", role: "admin",
            permissions: "findings.read, access.admin"
        },
        {project: "shop", group: "/teams/shop", role: "member", permissions: "findings.read, acks.write"}
    ]);
    assert.deepEqual(PSL.accessReachRows({reach: []}), []);
    assert.equal(PSL.ACCESS_NO_REACH,
        "No grant reaches your groups. An administrator grants access in the Hub's configuration.");
});

test("#/access is the access screen only when the Hub answered /api/access, New otherwise", () => {
    assert.equal(PSL.screenOf("#/access", true, false), "access");
    assert.equal(PSL.screenOf("#/access", false, false), "missing", "access off, or /api/access failed");
    assert.equal(PSL.screenOf("", false, false), "new");
    assert.equal(PSL.screenOf("#/recent", false, false), "recent");
    assert.equal(PSL.screenOf("#/correlations", false, false), "correlations");
    assert.equal(PSL.screenOf("#/new?from=1&to=2&service=s", false, false), "new");
    assert.equal(PSL.screenOf("#/run/0123456789abcdef", false, false), "run");
    assert.equal(PSL.screenOf("#/report/0123456789abcdef", true, false), "report");
    assert.equal(PSL.screenOf("#/nowhere", true, true), "missing");
    assert.equal(PSL.screenOf("#/", false, false), "new");
});

test("#/coverage is an administrator's screen: a member lands on their own access, access off on the missing screen", () => {
    assert.equal(PSL.screenOf("#/coverage", true, true), "coverage");
    assert.equal(PSL.screenOf("#/coverage", true, false), "access");
    assert.equal(PSL.screenOf("#/coverage", false, false), "missing");
});

test("the coverage snippet spells one quoted pattern per uncovered service, attribute and source", () => {
    const snippet = PSL.coverageSnippet({
        services: [{service: "reports-svc", source_ids: ["billing-stg"], findings: 3},
            {service: null, source_ids: ["shop-prod"], findings: 1}],
        attributes: [{key: "team", value: "pay\"ments", findings: 4}],
        sources: [{source_id: "billing-stg", findings: 8}, {source_id: "tempo-eu", findings: null}]
    });
    assert.equal(snippet, [
        "# Add the lines a team owns to the patterns of its project in hub.access.projects,",
        "# then restart the Hub. A finding without a service needs a source or attribute pattern.",
        "- { service: \"reports-svc\" }",
        "- { attribute: \"team=pay\\\"ments\" }",
        "- { source: \"billing-stg\" }",
        "- { source: \"tempo-eu\" }"
    ].join("\n"));
    assert.equal(PSL.coverageSnippet({services: [], attributes: [], sources: []}), null);
});

test("what a refused caller sends an administrator names their id and groups, one per line", () => {
    assert.equal(PSL.accessRequestText({
        identity: "noor@example.com", subject: "c75e0b19",
        groups: [{name: "/teams/marketing", origin: "provider"}, {name: "ops", origin: "internal"}]
    }), "Please grant access to the Perf Sentinel Hub.\nname: noor@example.com\nid: c75e0b19\n"
        + "groups: /teams/marketing (provider), ops (internal)");
    assert.equal(PSL.accessRequestText({identity: null, subject: "s1", groups: []}),
        "Please grant access to the Perf Sentinel Hub.\nname: s1\nid: s1\ngroups: none");
});

test("the source count says how many sources the caller may run on, and why the others are missing", () => {
    assert.equal(PSL.sourceCountLabel(7, 7), "7 configured");
    assert.equal(PSL.sourceCountLabel(5, 7), "5 of 7 configured · 2 need analyses.run");
    assert.equal(PSL.sourceCountLabel(6, 7), "6 of 7 configured · 1 needs analyses.run");
});

test("a revoke outcome says what happened to the terminal, or what to do next", () => {
    const row = {owner: "sam@example.com", created: "2026-10-14 14:23"};
    assert.deepEqual(PSL.revokeOutcome(204, row), {
        ok: true,
        text: "Revoked the token of sam@example.com created 2026-10-14 14:23. That terminal gets a 401 on its next request."
    });
    assert.deepEqual(PSL.revokeOutcome(404, row), {
        ok: true,
        text: "The token of sam@example.com created 2026-10-14 14:23 was already gone."
    });
    assert.deepEqual(PSL.revokeOutcome(500, row), {
        ok: false,
        text: "The Hub could not revoke this token (status 500). It is still valid. Try again in a moment."
    });
    assert.deepEqual(PSL.revokeOutcome(0, row), {
        ok: false,
        text: "The Hub did not answer. The token is still valid. Try again in a moment."
    });
});

test("the token table names each token's owner, origin, dates and last use", () => {
    const at = (h, m) => new Date(2026, 9, 14, h, m).getTime();
    const rows = PSL.tokenRows([
        {
            id: "0123456789abcdef0123456789abcdef",
            identity: "ada@example.com",
            subject: "2f6d",
            client_address: "203.0.113.7",
            created_at_ms: at(9, 5),
            expires_at_ms: at(17, 5),
            last_used_at_ms: at(10, 40)
        },
        {
            id: "fedcba9876543210fedcba9876543210", identity: "bob@example.com", subject: "9a1b", client_address: null,
            created_at_ms: at(11, 0), expires_at_ms: at(19, 0), last_used_at_ms: null
        }
    ]);
    assert.deepEqual(rows, [
        {
            id: "0123456789abcdef0123456789abcdef", owner: "ada@example.com", from: "203.0.113.7",
            created: "2026-10-14 09:05", expires: "2026-10-14 17:05", lastUsed: "2026-10-14 10:40"
        },
        {
            id: "fedcba9876543210fedcba9876543210", owner: "bob@example.com", from: "unknown",
            created: "2026-10-14 11:00", expires: "2026-10-14 19:00", lastUsed: "never"
        }
    ]);
});

test("revoking a token sends a DELETE with a JSON content type and no body", () => {
    assert.deepEqual(PSL.tokenRevokeRequest("0123456789abcdef0123456789abcdef"), {
        path: "/api/tokens/0123456789abcdef0123456789abcdef",
        init: {method: "DELETE", headers: {"content-type": "application/json"}}
    });
});

test("the approval page says where and when the code was requested, and until when the token holds", () => {
    const now = new Date(2026, 9, 14, 14, 3, 40).getTime();
    const verify = {
        user_code: "BCDF-GHJK", client_address: "203.0.113.7", same_network: true,
        requested_at_ms: new Date(2026, 9, 14, 14, 2, 31).getTime(), identity: "ada@example.com",
        token_expires_at_ms: new Date(2026, 9, 14, 18, 42).getTime()
    };
    assert.equal(PSL.deviceRequestLine(verify, now),
        "Requested from 203.0.113.7 at 14:02:31 (1 minute ago). You are signed in as ada@example.com. "
        + "The token expires at 18:42.");
    assert.equal(PSL.deviceRequestLine(Object.assign({}, verify, {requested_at_ms: now - 5000}), now).split(". ")[0],
        "Requested from 203.0.113.7 at 14:03:35 (less than a minute ago)");
    assert.equal(PSL.deviceRequestLine(Object.assign({}, verify, {requested_at_ms: now - 180000}), now).split(". ")[0],
        "Requested from 203.0.113.7 at 14:00:40 (3 minutes ago)");
    assert.equal(PSL.deviceIntro(verify.token_expires_at_ms),
        "Enter the code your terminal shows. Only enter a code from a perf-sentinel login you started yourself, "
        + "just now: approving gives that terminal your access until 18:42.");
});

test("a code from another network warns, and Approve then needs a second click", () => {
    assert.equal(PSL.deviceNetworkAlert({client_address: "203.0.113.7", same_network: true}), null);
    assert.equal(PSL.deviceNetworkAlert({client_address: "203.0.113.7", same_network: false}),
        "This code was requested from another network than this browser (203.0.113.7). Approve only if you "
        + "started that sign-in yourself on that machine.");
    assert.equal(PSL.deviceApproveStep(true, false), "send");
    assert.equal(PSL.deviceApproveStep(false, false), "arm");
    assert.equal(PSL.deviceApproveStep(false, true), "send");
});

test("the incident filters wait for the first read only, even on a fleet with no incident", () => {
    // Before any read: disabled, the namespace filter holding its place.
    assert.deepEqual(PSL.incidentFilterState(false, []), {pending: true, namespace: true});
    // A read that found nothing is still a read: enabled, no namespace column,
    // and a later refresh must not disable them again.
    assert.deepEqual(PSL.incidentFilterState(true, []), {pending: false, namespace: false});
    assert.deepEqual(PSL.incidentFilterState(true, ["shop"]), {pending: false, namespace: true});
});

test("running a past analysis again fills the form back from its request", () => {
    const window = PSL.formFromRequest({
        service: "orders",
        from_ms: 10,
        to_ms: 20,
        max_traces: 300,
        detection: {n_plus_one_threshold: 4}
    });
    assert.equal(window.mode, "service");
    assert.equal(window.service, "orders");
    assert.equal(window.rangeMode, "absolute");
    assert.equal(window.fromMs, 10);
    assert.equal(window.toMs, 20);
    assert.equal(window.maxTraces, 300);
    assert.deepEqual(window.detection, {n_plus_one_threshold: 4});
    const relative = PSL.formFromRequest({service: "orders", lookback: "6h"});
    assert.equal(relative.rangeMode, "relative");
    assert.equal(relative.lookback, "6h");
    const trace = PSL.formFromRequest({trace_id: "abc"});
    assert.equal(trace.mode, "trace");
    assert.equal(trace.traceId, "abc");
    assert.equal(trace.rangeMode, undefined, "a trace run leaves the window alone");
    assert.deepEqual(PSL.formFromRequest(null), {
        detection: {},
        pickerOpen: false,
        mode: "service",
        service: "",
        traceId: ""
    });
});

test("a light read keeps the verdict calm over hints that only describe the daemon", () => {
    const under = {value: 10, capacity: 100, pct: 10, at_capacity: false};
    const previous = {
        warnings: [{kind: "cold_start", message: "m"}, {kind: "snapshot_scope", message: "m"}],
        traces: under, analysis_queue: under, findings: under, state: "ok"
    };
    const light = {observed_at_ms: 2, traces: under, analysis_queue: under, findings: under};
    assert.equal(PSL.mergeLight(previous, light).state, "ok");
    previous.warnings.push({kind: "tuning", message: "m"});
    assert.equal(PSL.mergeLight(previous, light).state, "advised");
});

test("an incident's findings sort by the column asked, worst and most seen first on ties", () => {
    const row = (type, severity, endpoint, seen, first) =>
        ({seen_count: seen, first_seen_ms: first, finding: {type, severity, source_endpoint: endpoint}});
    const rows = [
        row("n_plus_one_sql", "warning", "/b", 3, 30),
        row("slow_http", "critical", "/a", 1, 20),
        row("n_plus_one_sql", "warning", "/a", 9, 10),
        row("redundant_sql", "info", "/c", 9, 40)
    ];
    const endpoints = list => list.map(r => r.finding.source_endpoint + r.seen_count);
    assert.deepEqual(endpoints(PSL.sortFindings(rows, "severity", "desc")), ["/a1", "/a9", "/b3", "/c9"]);
    assert.deepEqual(endpoints(PSL.sortFindings(rows, "seen", "desc")), ["/a9", "/c9", "/b3", "/a1"]);
    assert.deepEqual(endpoints(PSL.sortFindings(rows, "endpoint", "asc")), ["/a1", "/a9", "/b3", "/c9"]);
    assert.deepEqual(endpoints(PSL.sortFindings(rows, "first", "asc")), ["/a9", "/a1", "/b3", "/c9"]);
    assert.equal(rows[0].finding.source_endpoint, "/b", "the input is left in its order");
});

test("the folded incident filters say how many of them are in force", () => {
    const filter = {service: "a", kind: "oom_kill", namespace: "", environment: "", source_id: ""};
    assert.equal(PSL.moreFiltersLabel(filter), "More filters", "service and kind stay in sight");
    assert.equal(PSL.moreFiltersLabel(Object.assign({}, filter, {
        environment: "prod",
        source_id: "x"
    })), "More filters, 2 set");
});

test("the ack expiry reads back in UTC, whatever the browser shows the date as", () => {
    const now = Date.parse("2026-10-15T12:00:00Z");
    assert.equal(PSL.ackExpiryLine("", now), "No date: the ack is permanent.");
    assert.equal(PSL.ackExpiryLine("2026-11-30", now), "Holds until 2026-11-30 23:59:59 UTC.");
    assert.equal(PSL.ackExpiryLine("2026-10-14", now), "The expiry is in the past.");
});

test("a frozen finding is told apart by its template, and found by it", () => {
    const row = {
        finding: {
            source_endpoint: "/api/catalog/search",
            pattern: {template: "SELECT * FROM item WHERE id = ?"}
        }
    };
    assert.equal(PSL.findingTemplate(row), "SELECT * FROM item WHERE id = ?");
    assert.equal(PSL.findingTemplate({finding: {}}), "");
    assert.ok(PSL.findingMatches(row, "  FROM ITEM "));
    assert.ok(PSL.findingMatches(row, "catalog"));
    assert.ok(PSL.findingMatches(row, ""));
    assert.ok(!PSL.findingMatches(row, "orders"));
    const sorted = PSL.sortFindings([
        {seen_count: 1, first_seen_ms: 1, finding: {pattern: {template: "b"}}},
        {seen_count: 1, first_seen_ms: 2, finding: {pattern: {template: "a"}}}
    ], "template", "asc");
    assert.deepEqual(sorted.map(PSL.findingTemplate), ["a", "b"]);
});

test("a frozen finding's impact is the dashboard's traces times extra operations, absent when unscored", () => {
    const row = (seen, perTrace) => ({
        seen_count: seen, first_seen_ms: 1,
        finding: perTrace == null ? {} : {green_impact: {estimated_extra_io_ops: perTrace}}
    });
    assert.equal(PSL.findingImpact(row(4, 11)), 44);
    assert.equal(PSL.findingImpact(row(4, 0)), 0);
    assert.equal(PSL.findingImpact(row(4, null)), null);
    assert.equal(PSL.findingImpact({finding: {green_impact: {estimated_extra_io_ops: 3}}}), null);
    const sorted = PSL.sortFindings([row(1, null), row(1, 0), row(2, 5)], "impact", "desc");
    assert.deepEqual(sorted.map(PSL.findingImpact), [10, 0, null]);
});

test("the daemon-side ack names the daemon before the subcommand and quotes what the shell needs", () => {
    const line = PSL.ackCommand({base_url: "http://d:4318"}, "n_plus_one_sql:svc:ep:abc", "posix");
    assert.equal(line, "perf-sentinel ack --daemon http://d:4318 create --signature n_plus_one_sql:svc:ep:abc --reason 'why it can wait'");
});

test("a finding after the incident is placed after the event that kind names", () => {
    assert.equal(PSL.phaseLabel("before", "deploy"), " before the incident");
    assert.equal(PSL.phaseLabel("after", "oom_kill"), " after the restart");
    assert.equal(PSL.phaseLabel("after", "restart"), " after the restart");
    assert.equal(PSL.phaseLabel("after", "deploy"), " after the deploy");
    assert.equal(PSL.phaseLabel("after", "memory_saturation"), " after the incident");
});

// ------------------------------------------------------------ correlations

const episode = (over) => Object.assign({
    source_id: "checkout-prod",
    pair_key: "a".repeat(32),
    started_at_ms: 1_000_000,
    last_seen_ms: 1_060_000,
    ended_at_ms: 1_060_000,
    peak_confidence: 0.8,
    median_lag_ms: 1200,
    source: {finding_type: "n_plus_one_sql", service: "order svc", template: "SELECT 1", grouping_key: null},
    target: {finding_type: "pool_saturation", service: "orders-svc", template: "SELECT 2", grouping_key: null},
    sample_trace_id: "b".repeat(32),
    source_sample_trace_id: "c".repeat(32)
}, over);

test("episodes fold into one row per pair and daemon, the most recently active first", () => {
    const pairs = PSL.correlationPairs([
        episode({started_at_ms: 3_000_000, last_seen_ms: 3_100_000, ended_at_ms: null, peak_confidence: 0.7}),
        episode({pair_key: "d".repeat(32), started_at_ms: 2_000_000, last_seen_ms: 2_050_000, ended_at_ms: 2_050_000}),
        episode({peak_confidence: 0.92}),
        episode({source_id: "orders-prod", started_at_ms: 500_000, last_seen_ms: 560_000, ended_at_ms: 560_000})
    ]);
    assert.deepEqual(pairs.map((pair) => [pair.source_id, pair.pair_key[0], pair.episodes.length, pair.active, pair.peak]),
        [["checkout-prod", "a", 2, true, 0.92], ["checkout-prod", "d", 1, false, 0.8], ["orders-prod", "a", 1, false, 0.8]]);
    assert.equal(pairs[0].last.started_at_ms, 3_000_000, "the last episode is the latest started");
    assert.equal(pairs[0].key, "checkout-prod:" + "a".repeat(32));
});

test("two spans share a moment on their bounds, and a running one reaches every later moment", () => {
    assert.equal(PSL.spansOverlap(0, 10, 10, 20), true, "an end on the other's start");
    assert.equal(PSL.spansOverlap(0, 10, 11, 20), false);
    assert.equal(PSL.spansOverlap(0, null, 1_000, 2_000), true, "still running");
    assert.equal(PSL.spansOverlap(5_000, null, 1_000, 2_000), false, "started after the span");
});

test("an episode's window travels to New analysis a minute wider on each side, with its pair", () => {
    const hash = PSL.episodeHandoffHash(episode({}), 9_000_000);
    assert.equal(hash, "#/new?from=940000&to=1120000&service=order%20svc&pair=" + "a".repeat(32));
    assert.deepEqual(PSL.readHandoff(hash, 9_000_000),
        {fromMs: 940000, toMs: 1120000, service: "order svc", incidentId: "", pairKey: "a".repeat(32)});
});

test("a running episode's window ends now, never in a future the form refuses", () => {
    assert.match(PSL.episodeHandoffHash(episode({ended_at_ms: null}), 1_500_000), /&to=1500000&/);
    assert.match(PSL.episodeHandoffHash(episode({}), 1_100_000), /&to=1100000&/, "a minute past the end is not yet");
});

test("the last pair's traces open New analysis on the first one, the other carried along", () => {
    assert.equal(PSL.pairTraceHash(episode({})), "#/new?trace=" + "c".repeat(32) + "&other=" + "b".repeat(32));
    assert.equal(PSL.pairTraceHash(episode({source_sample_trace_id: null})), "#/new?trace=" + "b".repeat(32));
    assert.equal(PSL.pairTraceHash(episode({source_sample_trace_id: null, sample_trace_id: null})), null);
    assert.deepEqual(PSL.readTraceHandoff("#/new?trace=x1&other=y2"), {traceId: "x1", otherTraceId: "y2"});
    assert.deepEqual(PSL.readTraceHandoff("#/new?trace=x1"), {traceId: "x1", otherTraceId: ""});
    assert.equal(PSL.readTraceHandoff("#/new?from=1&to=2&service=s"), null);
    assert.equal(PSL.readTraceHandoff("#/correlations?trace=x1"), null);
    assert.equal(PSL.readTraceHandoff(null), null);
});

test("a strip places each span on its range, clipped, and never thinner than three", () => {
    assert.deepEqual(PSL.timelineMarks(
            [{from: 250, to: 500}, {from: -100, to: 50}, {from: 900, to: null}, {from: 400, to: 401}], 0, 1000, 640),
        [{x: 160, w: 160}, {x: 0, w: 32}, {x: 576, w: 64}, {x: 256, w: 3}]);
});

test("a lag keeps its tenth of a second, which dur() would round away", () => {
    assert.equal(PSL.lagText(850), "850 ms");
    assert.equal(PSL.lagText(1200), "1.2 s");
    assert.equal(PSL.lagText(90_000), "1 m 30 s");
    assert.equal(PSL.lagText(null), "—");
});

test("an episode's span names its start whole and its end as a clock on the same day", () => {
    const at = (day, hour, minute) => new Date(2026, 9, day, hour, minute).getTime();
    assert.equal(PSL.episodeSpan(at(15, 11, 55), at(15, 12, 25)), "2026-10-15 11:55 → 12:25");
    assert.equal(PSL.episodeSpan(at(15, 23, 50), at(16, 0, 20)), "2026-10-15 23:50 → 2026-10-16 00:20");
});

test("a link to one pair carries its key in the correlations route", () => {
    assert.equal(PSL.readCorrelationPair("#/correlations?pair_key=" + "a".repeat(32)), "a".repeat(32));
    assert.equal(PSL.readCorrelationPair("#/correlations?pair_key=%20"), "", "blank is no filter");
    assert.equal(PSL.readCorrelationPair("#/correlations"), "");
    assert.equal(PSL.readCorrelationPair("#/incidents?pair_key=x"), "");
    assert.equal(PSL.readCorrelationPair(null), "");
    assert.equal(PSL.screenOf("#/correlations?pair_key=x", false, false), "correlations");
});

test("an incident's correlations are cached per window, so a later end reads again", () => {
    assert.equal(PSL.correlationWindowKey("k", 1000, 5000), PSL.correlationWindowKey("k", 1000, 5000));
    assert.notEqual(PSL.correlationWindowKey("k", 1000, 5000), PSL.correlationWindowKey("k", 1000, 9000));
});

test("a pair's accessible name says both finding types and both services", () => {
    const side = (type, service) => ({finding_type: type, service: service, template: "SELECT 1"});
    assert.equal(
        PSL.pairLabel({source: side("n_plus_one_sql", "order-svc"), target: side("pool_saturation", "payment-svc")}),
        "N+1 SQL in order-svc, then Pool saturation in payment-svc");
});

test("a stale episode says so in plain text", () => {
    assert.equal(PSL.episodeEndNote({ended_by: "stale"}), "stale");
    assert.equal(PSL.episodeEndNote({ended_by: null}), "");
});


test("a full Hub database is said to be the Hub's, not the daemon's", () => {
    const now = 10 * 60 * 1000;
    const down = {reachable: false, unreachable_since_ms: 0, last_attempt_ms: now};
    assert.deepEqual(PSL.sourceHealth({...down, last_error_code: "database_full"}, now),
        {tone: "crit", text: "Hub database full"});
    assert.deepEqual(PSL.sourceHealth({...down, last_error_code: "timeout"}, now),
        {tone: "crit", text: "unreachable " + PSL.dur(now)});
    assert.match(PSL.READ_ERRORS.database_full, /Hub's own/);
    assert.match(PSL.READ_ERRORS.storage_error, /Hub's own/);
});

test("a source minting signatures is flagged while it answers", () => {
    const up = {reachable: true, last_attempt_ms: 1};
    assert.deepEqual(PSL.sourceHealth({...up, signature_rate_warning: true, new_signatures_last_hour: 9000}, 1),
        {tone: "warn", text: "9000 new signatures in the last hour"});
    assert.deepEqual(PSL.sourceHealth(up, 1), {tone: "ok", text: "reachable"});
    assert.deepEqual(PSL.sourceHealth({reachable: true, last_attempt_ms: null}, 1),
        {tone: "ok", text: "not yet observed"});
});

test("only a 404 makes a run unknown, any other failure is asked again", () => {
    assert.equal(PSL.runReadOutcome({status: 404}), "unknown");
    for (const status of [502, 503, 504, 500, undefined]) {
        assert.equal(PSL.runReadOutcome(status === undefined ? new TypeError("fetch failed") : {status}), "retry");
    }
});

test("a run read that keeps failing backs off up to fifteen seconds", () => {
    assert.deepEqual([1, 2, 3, 4, 5, 9].map(PSL.retryDelay), [1000, 2000, 4000, 8000, 15000, 15000]);
});

// ------------------------------------------------------------ disclosures

const DAY = 86_400_000;
const disclosure = (id, from, to, extra) => Object.assign({
    id, period: {type: "calendar-quarter", from, to}, confidentiality: "public",
    integrity_level: "signed", withdrawn: null
}, extra);

test("the period rail runs from the first disclosed quarter to the current one", () => {
    const now = Date.UTC(2026, 8, 18);
    const rail = PSL.periodRail([
        disclosure("q1", "2026-01-01", "2026-03-31"),
        disclosure("q4", "2025-10-01", "2025-12-31", {withdrawn: {at_ms: 1}}),
        disclosure("q2", "2026-04-01", "2026-06-30", {confidentiality: "internal", integrity_level: "hash-only"})
    ], now);

    assert.deepEqual(rail.map((cell) => [cell.label, cell.state]), [
        ["2025 Q4", "withdrawn"], ["2026 Q1", "published"], ["2026 Q2", "internal"], ["2026 Q3", "current"]
    ]);
    assert.equal(rail[1].level, "signed");
    assert.equal(rail[1].id, "q1");
    assert.equal(rail[3].daysLeft, 13);
});

test("a gap in the rail is a missing quarter, and a published copy beats a withdrawn one", () => {
    const now = Date.UTC(2026, 6, 2);
    const rail = PSL.periodRail([
        disclosure("old", "2025-07-01", "2025-09-30"),
        disclosure("bad", "2026-01-01", "2026-03-31", {withdrawn: {at_ms: 1}}),
        disclosure("good", "2026-01-01", "2026-03-31")
    ], now);

    assert.deepEqual(rail.map((cell) => cell.state), ["published", "missing", "published", "missing", "current"]);
    assert.equal(rail[2].id, "good");
    assert.deepEqual(PSL.periodRail([], now), []);
});

test("coverage is drawn against the 75 % official notch", () => {
    assert.deepEqual(PSL.coverageNotch(0.91), {percent: 91, below: false});
    assert.deepEqual(PSL.coverageNotch(0.71), {percent: 71, below: true});
    assert.deepEqual(PSL.coverageNotch(0.75), {percent: 75, below: false});
    assert.equal(PSL.coverageNotch(null), null);
});

test("who checked what never says the Hub checked a signature", () => {
    const rows = PSL.checkRows({
        disclosure: {integrity_level: "signed-with-attestation"},
        checks: {content_hash: "checked", binding: "checked", signature: "declared", engine_version: "0.31.0"}
    });

    assert.deepEqual(rows.map((row) => row.outcome), ["checked", "checked", "declared", "none"]);
    assert.match(rows[0].by, /this Hub, perf-sentinel 0\.31\.0/);
    assert.ok(rows.slice(2).every((row) => row.outcome !== "checked"));

    const unchecked = PSL.checkRows({
        disclosure: {integrity_level: "audited"},
        checks: {content_hash: "not_checked", binding: "no_attestation", signature: "absent", engine_version: null}
    });
    assert.deepEqual(unchecked.map((row) => row.outcome), ["not_checked", "absent", "absent", "declared"]);
});

test("host rows carry the database and broker energy the total holds and no application does", () => {
    assert.deepEqual(PSL.hostRows(null), []);
    assert.deepEqual(PSL.hostRows({aggregate: {database_waste: {windows_with_figure: 4, energy_kwh: 3}}}), [],
        "a v1.7 block, or one beside the totals, adds no row");

    const rows = PSL.hostRows({
        aggregate: {
            database_waste: {windows_with_figure: 4, in_totals_windows: 4, in_totals_energy_kwh: 1.25},
            messaging_waste: {windows_with_figure: 4, in_totals_windows: 3, in_totals_energy_kwh: 0.5},
            carbon_breakdown: {database_kgco2eq: 0.4}
        }
    });
    assert.deepEqual(rows, [
        {label: "Database hosts", energy_kwh: 1.25, carbon_kgco2eq: 0.4, partial: null},
        {label: "Broker hosts", energy_kwh: 0.5, carbon_kgco2eq: 0, partial: {windows: 3, of: 4}}
    ]);
});

test("a small in-total host never reads as zero kWh", () => {
    assert.equal(PSL.hostKwh(0), "0.00");
    assert.equal(PSL.hostKwh(0.2), "0.20");
    assert.equal(PSL.hostKwh(0.005), "0.01");
    assert.equal(PSL.hostKwh(0.003), "3.0e-3");
});

test("the verify command names the expected signer only when the Hub knows one", () => {
    assert.equal(PSL.verifyCommand({expected_identity: null, expected_issuer: null}),
        "perf-sentinel verify-hash --report report.pdf");
    assert.equal(
        PSL.verifyCommand({
            expected_identity: "https://github.com/acme/infra/.github/workflows/disclose.yml@refs/heads/main",
            expected_issuer: "https://token.actions.githubusercontent.com"
        }),
        "perf-sentinel verify-hash --report report.pdf \\\n"
        + "  --expected-identity https://github.com/acme/infra/.github/workflows/disclose.yml@refs/heads/main \\\n"
        + "  --expected-issuer https://token.actions.githubusercontent.com");
    assert.equal(
        PSL.verifyCommand({expected_identity: "x'; rm -rf /", expected_issuer: "i"}).split("\n")[1],
        "  --expected-identity 'x'\\''; rm -rf /' \\");
});

test("the publish command posts the three files as multipart to this Hub", () => {
    const command = PSL.publishCommand("https://hub.example");
    assert.match(command, /-F report=@report\.json/);
    assert.match(command, /-F attestation=@attestation\.intoto\.jsonl/);
    assert.match(command, /-F bundle=@bundle\.sig/);
    assert.match(command, /https:\/\/hub\.example\/api\/import\/disclosures$/);
    assert.match(command, /cosign sign-blob --bundle bundle\.sig --new-bundle-format attestation\.intoto\.jsonl/);
});

test("disclosures route to their own screens", () => {
    assert.equal(PSL.screenOf("#/disclosures", false, false), "disclosures");
    assert.equal(PSL.screenOf("#/disclosures/0123456789abcdef", false, false), "disclosure");
    assert.equal(PSL.readDisclosureId("#/disclosures/0123456789abcdef"), "0123456789abcdef");
    assert.equal(PSL.readDisclosureId("#/disclosures/../x"), null);
});

test("a download goes through fetch, and a 401 signs back in instead of saving the body", async () => {
    const saved = [];
    let reloaded = false;
    const env = {
        fetch: async (url) => url.endsWith("expired")
            ? {status: 401, ok: false}
            : {status: 200, ok: true, blob: async () => "bytes"},
        reload: () => {
            reloaded = true;
        },
        save: (blob, name) => saved.push([blob, name])
    };

    assert.equal(await PSL.downloadAs("/api/disclosures/d1/files/report.pdf", "report.pdf", env), true);
    assert.deepEqual(saved, [["bytes", "report.pdf"]]);
    assert.equal(await PSL.downloadAs("/expired", "report.json", env), false);
    assert.equal(reloaded, true);
    assert.equal(saved.length, 1);
});

test("an action owner reads as a name", () => {
    // The SSO display name wins and the full identity stays on hover.
    assert.deepEqual(PSL.ownerLabel("alice.martin@acme.com", null),
        {text: "Alice Martin", title: "alice.martin@acme.com"});
    assert.deepEqual(PSL.ownerLabel("local:alice", null), {text: "Alice", title: "local:alice"});
    assert.deepEqual(PSL.ownerLabel("jean-eric.dupont@acme.fr", null),
        {text: "Jean-Eric Dupont", title: "jean-eric.dupont@acme.fr"});
    assert.deepEqual(PSL.ownerLabel("jean-eric.dupont@acme.fr", "Jean-\u00c9ric Dupont"),
        {text: "Jean-\u00c9ric Dupont", title: "jean-eric.dupont@acme.fr"});
    // Typed by hand, or a machine: as it is.
    assert.deepEqual(PSL.ownerLabel("\u00c9quipe paiement", null),
        {text: "\u00c9quipe paiement", title: null});
    assert.deepEqual(PSL.ownerLabel("machine:ci", null), {text: "machine:ci", title: null});
    assert.deepEqual(PSL.ownerLabel("\u00c9lodie", null), {text: "\u00c9lodie", title: null});
    assert.deepEqual(PSL.ownerLabel("alice_martin", null), {text: "Alice Martin", title: "alice_martin"});
    // Nothing to show.
    assert.deepEqual(PSL.ownerLabel(null, null), {text: "", title: null});
    assert.deepEqual(PSL.ownerLabel("", null), {text: "", title: null});
});

test("the recap route reads its defaults and drops what it does not know", () => {
    assert.deepEqual(PSL.readRecapRoute("#/recap"), {
        period: "week", at: null, environment: null, service: null, grouping: null, view: "tech"
    });
    assert.deepEqual(PSL.readRecapRoute("#/recap?period=month&at=2026-10-29&environment=prod&service=a%2Fb&grouping=pay&view=pilotage"), {
        period: "month", at: "2026-10-29", environment: "prod", service: "a/b", grouping: "pay", view: "pilotage"
    });
    // An unknown value reads as its default, a blank filter as none.
    assert.deepEqual(PSL.readRecapRoute("#/recap?period=year&at=yesterday&view=ceo&service=%20"), {
        period: "week", at: null, environment: null, service: null, grouping: null, view: "tech"
    });
    assert.equal(PSL.readRecapRoute("#/recap?service=a%0Ab").service, null, "control character");
    assert.equal(PSL.readRecapRoute("#/recap?service=" + "a".repeat(257)).service, null, "too long");
    assert.equal(PSL.readRecapRoute("#/new").period, "week", "another route reads as the defaults");
});

test("the recap hash carries only what differs from the defaults", () => {
    assert.equal(PSL.recapHash(PSL.readRecapRoute("#/recap")), "#/recap");
    const route = {
        period: "day",
        at: "2026-10-29",
        environment: "pre prod",
        service: "a/b",
        grouping: null,
        view: "pilotage"
    };
    assert.equal(PSL.recapHash(route),
        "#/recap?period=day&at=2026-10-29&environment=pre%20prod&service=a%2Fb&view=pilotage");
    assert.deepEqual(PSL.readRecapRoute(PSL.recapHash(route)), route);
    assert.equal(PSL.recapHash({
            period: "week",
            at: null,
            environment: null,
            service: null,
            grouping: null,
            view: "tech"
        }),
        "#/recap");
});

test("the recap path always names its period and view", () => {
    assert.equal(PSL.recapPath(PSL.readRecapRoute("#/recap")), "/api/recap?period=week&view=tech");
    assert.equal(PSL.recapPath({
            period: "day",
            at: "2026-10-29",
            environment: "prod",
            service: "a b",
            grouping: "g",
            view: "pilotage"
        }),
        "/api/recap?period=day&view=pilotage&at=2026-10-29&environment=prod&service=a%20b&grouping=g");
});

test("the carbon figure says it is an estimate and what it lacks", () => {
    assert.equal(PSL.co2Text(null, 0, "no_coefficient"), "— no coefficient from the engine");
    assert.equal(PSL.co2Text(null, 0, "no_avoidable_io"), "— no avoidable I/O");
    assert.equal(PSL.co2Text(300, 0, null), "~300 g CO2e, estimate");
    assert.equal(PSL.co2Text(2000, 1, null), "at least ~2 kg CO2e, estimate, 1 source without a coefficient");
    assert.equal(PSL.co2Text(2000, 3, null), "at least ~2 kg CO2e, estimate, 3 sources without a coefficient");
    assert.equal(PSL.co2Text(0, 0, null), "~0 g CO2e, estimate");
    assert.equal(PSL.co2Text(0.0002, 0, null), "< 1 g CO2e, estimate", "a nonzero estimate never reads zero");
    assert.equal(PSL.co2Text(0.4, 1, null), "< 1 g CO2e, estimate, 1 source without a coefficient");
    assert.equal(PSL.co2Text(0.5, 0, null), "< 1 g CO2e, estimate");
    assert.deepEqual(PSL.co2Parts(0.4, 1, null), {lead: "", figure: "< 1 g", unit: "CO2e"});
    assert.deepEqual(PSL.co2Parts(2000, 1, null), {lead: "at least", figure: "~2 kg", unit: "CO2e"});
    assert.deepEqual(PSL.co2Parts(300, 0, null), {lead: "", figure: "~300 g", unit: "CO2e"});
    assert.deepEqual(PSL.co2Parts(null, 0, "no_avoidable_io"), {lead: "", figure: "—", unit: "no avoidable I/O"});
});

test("a zero or an unknown avoidable volume is not a figure to rank", () => {
    assert.equal(PSL.hasAvoidableVolume({avoidable_io_ops: 12}), true);
    assert.equal(PSL.hasAvoidableVolume({avoidable_io_ops: 0}), false, "a known ledger with no avoidable call");
    assert.equal(PSL.hasAvoidableVolume({avoidable_io_ops: null}), false);
    assert.equal(PSL.hasAvoidableVolume({}), false);
});

test("the divider follows a row with a volume, never opens the list", () => {
    const rows = volumes => volumes.map(volume => ({avoidable_io_ops: volume}));
    assert.deepEqual([0, 1].map(index => PSL.dividesBefore(rows([0, null]), index)), [false, false],
        "no row with a volume, nothing to divide");
    assert.deepEqual([0, 1, 2].map(index => PSL.dividesBefore(rows([5, 0, null]), index)), [false, true, false]);
    assert.deepEqual([0, 1].map(index => PSL.dividesBefore(rows([5, 3]), index)), [false, false]);
});

test("a trend reads in a word or a percentage", () => {
    assert.equal(PSL.trendText("new", null), "new");
    assert.equal(PSL.trendText("up", 0.35), "+35%");
    assert.equal(PSL.trendText("down", -0.2), "-20%");
    assert.equal(PSL.trendText("up", null), "up");
    assert.equal(PSL.trendText("flat", 0.01), "stable");
    assert.equal(PSL.trendText("gone", null), "gone");
    assert.equal(PSL.trendText("unknown", null), "");
});

test("an action's confirmation is named, anything else is left out", () => {
    assert.equal(PSL.confirmationLabel("confirmed"), "Confirmed by the engine");
    assert.equal(PSL.confirmationLabel("contradicted"), "Still detected");
    assert.equal(PSL.confirmationLabel("came_back"), "Came back");
    assert.equal(PSL.confirmationLabel("awaiting"), "Waiting for a sealed day");
    assert.equal(PSL.confirmationLabel("whatever"), "");
    assert.equal(PSL.confirmationLabel(null), "");
});

test("a period reads as its first and last day", () => {
    assert.equal(PSL.periodText({kind: "week", from: "2026-10-26", to: "2026-11-01", in_progress: false}),
        "2026-10-26 to 2026-11-01");
    assert.equal(PSL.periodText({kind: "week", from: "2026-10-26", to: "2026-11-01", in_progress: true}),
        "2026-10-26 to 2026-11-01, in progress");
    assert.equal(PSL.periodText({kind: "day", from: "2026-10-29", to: "2026-10-29", in_progress: true}),
        "2026-10-29, in progress");
    assert.equal(PSL.periodText({kind: "day", from: "2026-10-28", to: "2026-10-28", in_progress: false}), "2026-10-28");
    assert.equal(PSL.periodText({kind: "month", from: "2026-10-01", to: "2026-10-31", in_progress: false}),
        "2026-10-01 to 2026-10-31");
});

test("what forbids an exact comparison and what is only worth a line", () => {
    assert.equal(PSL.incompleteText([]), "");
    assert.equal(PSL.incompleteText(["restart", "open_day"]), "Incomplete: a daemon restart, a day still open");
    assert.equal(PSL.notesText([], null), "");
    assert.equal(PSL.notesText(["sampling", "shed"], 0.5),
        "Note: sampled traces (rate 0.5), traces shed under memory pressure");
    assert.equal(PSL.notesText(["sampling"], null), "Note: sampled traces");
});

test("only an http or https address becomes a link", () => {
    assert.equal(PSL.httpUrl("https://x.test/a"), "https://x.test/a");
    assert.equal(PSL.httpUrl("http://x.test"), "http://x.test");
    assert.equal(PSL.httpUrl("javascript:alert(1)"), null);
    assert.equal(PSL.httpUrl("/a"), null);
    assert.equal(PSL.httpUrl(null), null);
    assert.equal(PSL.httpUrl(undefined), null);
});

test("the recap is a screen of its own", () => {
    assert.equal(PSL.screenOf("#/recap?period=day", false, false), "recap");
    assert.equal(PSL.screenOf("#/recap", true, false), "recap");
});

test("Management_labels_carry_no_technical_word", () => {
    const technical = /I\/O|finding|quer(y|ies)|endpoint/i;
    for (const text of Object.values(PSL.RECAP_LABELS.pilotage)) assert.doesNotMatch(text, technical);
    assert.deepEqual(PSL.RECAP_LABELS.tech, {io: "Avoidable I/O", findings: "Active findings", moved: "What moved"});
    assert.deepEqual(PSL.RECAP_LABELS.pilotage,
        {io: "Unnecessary operations", findings: "Open problems", moved: "What changed"});
});

// The engine's words a Management reader must not meet, as RecapSentencesTests.TechnicalWords. "engine" is a
// whole word: "Engineering" names the other view.
const TECHNICAL = /quer(y|ies)|database|endpoint|broker|pool|connection|sql|http|I\/O|finding|signature|\bengine\b|daemon|ledger|coefficient|trace|ingest|sampl|source/i;
const GAP_REASONS = ["uncovered_days", "open_day", "restart", "sampling", "shed", "ingest_rejected", "overflow",
    "multi_instance"];

test("Management_text_matches_the_launcher_helpers", () => {
    assert.deepEqual(GAP_REASONS.concat("brand_new").map((reason) => PSL.gapLabel(reason, "pilotage")), [
        "days with no data", "a day not finished yet", "a restart of the measuring tool",
        "only part of the traffic was looked at", "some activity was dropped under load", "some data was refused",
        "too many problems to count them all", "several copies of one service", "another measurement gap"
    ]);
    assert.equal(PSL.gapLabel("brand_new"), "brand_new");
    assert.equal(PSL.incompleteText(["open_day", "x"], "pilotage"),
        "Incomplete: a day not finished yet, another measurement gap");
    assert.equal(PSL.incompleteText(["open_day", "x"], "tech"), "Incomplete: a day still open, x");
    assert.equal(PSL.notesText(["sampling", "shed"], 0.5, "pilotage"),
        "Note: only part of the traffic was looked at (rate 0.5), some activity was dropped under load");
    assert.equal(PSL.co2Text(2000, 1, null, "pilotage"),
        "at least ~2 kg CO2e, estimate, part of the activity has no CO2 estimate");
    assert.equal(PSL.co2Text(0.4, 2, null, "pilotage"), "< 1 g CO2e, estimate, part of the activity has no CO2 estimate");
    assert.equal(PSL.co2Text(300, 0, null, "pilotage"), "~300 g CO2e, estimate");
    assert.equal(PSL.co2Text(null, 0, "no_coefficient", "pilotage"), "\u2014 not available");
    assert.equal(PSL.co2Text(null, 0, "no_avoidable_io", "pilotage"), "\u2014 not available");
    assert.deepEqual(PSL.co2Parts(null, 0, "no_coefficient", "pilotage"), {
        lead: "",
        figure: "\u2014",
        unit: "not available"
    });
    assert.equal(PSL.co2Tail(1, "pilotage"), ", part of the activity has no CO2 estimate");
    assert.equal(PSL.co2Tail(2, "tech"), ", 2 sources without a coefficient");
    assert.equal(PSL.co2Tail(1, "tech"), ", 1 source without a coefficient");
    assert.equal(PSL.co2Tail(0, "pilotage"), "");
    assert.equal(PSL.confirmationLabel("confirmed", "pilotage"), "Confirmed by the measurements");
    assert.equal(PSL.confirmationLabel("contradicted", "pilotage"), "Still detected");
    assert.equal(PSL.confirmationLabel("came_back", "pilotage"), "Came back");
    assert.equal(PSL.confirmationLabel("awaiting", "pilotage"), "Waiting for a full day of measurements");
    assert.equal(PSL.confirmationLabel("later", "pilotage"), "");
    assert.equal(PSL.confirmationLabel("confirmed", "tech"), "Confirmed by the engine");
    assert.equal(PSL.recapTruncatedText("pilotage"),
        "The Hub read the first 5,000 problems and stopped there. Narrow the filters to see the others.");
    assert.equal(PSL.recapTruncatedText("tech"),
        "The Hub read the first 5,000 findings and stopped there. Narrow the filters to see the others.");
    assert.equal(PSL.recapSourcesNote(["a", "b"], "pilotage"), "No figures came from: a, b.");
    assert.equal(PSL.recapSourcesNote(["a", "b"], "tech"), "Sources without a detection volume: a, b.");
    assert.equal(PSL.recapLateNote(["a", "b"]), "Left out, as they recorded nothing until after this period: a, b.");
});

test("Management_copy_carries_no_technical_word", () => {
    const route = PSL.readRecapRoute("#/recap?period=month&at=2026-10-02&service=checkout&view=pilotage");
    const report = {
        subscription: null, created_by: "alice", nothing_moved: false, complete: false,
        period: {kind: "week", from: "2026-10-19", to: "2026-10-25"}, deliveries: []
    };
    const texts = [
        ...GAP_REASONS.concat("brand_new").map((reason) => PSL.gapLabel(reason, "pilotage")),
        PSL.incompleteText(GAP_REASONS, "pilotage"),
        PSL.notesText(GAP_REASONS, 0.5, "pilotage"),
        PSL.co2Text(2000, 3, null, "pilotage"),
        PSL.co2Text(null, 0, "no_coefficient", "pilotage"),
        PSL.co2Text(null, 0, "no_avoidable_io", "pilotage"),
        ...["confirmed", "contradicted", "came_back", "awaiting"].map((state) => PSL.confirmationLabel(state, "pilotage")),
        PSL.recapTruncatedText("pilotage"),
        PSL.recapSourcesNote(["svc-quiet"], "pilotage"),
        ...[403, 400, 500].map((status) => PSL.recapErrorText(status, "pilotage")),
        ...Object.values(PSL.RECAP_LABELS.pilotage),
        ...Object.values(PSL.RECAP_HELP.pilotage),
        ...Object.values(PSL.RECAP_REPORTS_COPY),
        PSL.recapFreezeText(route),
        PSL.recapFreezeText(PSL.readRecapRoute("#/recap")),
        PSL.recapReportLabel(report),
        PSL.recapReportLabel({...report, subscription: "weekly-shop"}),
        PSL.recapReportStatus(report),
        PSL.recapReportStatus({...report, subscription: "weekly-shop", nothing_moved: true}),
        PSL.recapReportStatus({...report, subscription: "weekly-shop"}),
        PSL.recapDeliveryText({
            channel: "webhook",
            target: "ops-chat",
            state: "delivered",
            attempts: 1,
            last_status: 204
        }),
        PSL.recapDeliveryText({
            channel: "email",
            target: "pm@acme.test",
            state: "failed",
            attempts: 8,
            last_status: null
        }),
        // app.js: the more-priorities note and the Reports row's download links.
        "2 more priorities, lower down, in the Engineering view.", "Management", "Engineering"
    ];
    for (const text of texts) {
        assert.ok(text, "every text is set");
        assert.doesNotMatch(text, TECHNICAL);
    }
    assert.equal(PSL.recapFreezeText(route), "Freezes the month that holds 2026-10-02, with the filters above.");
    assert.equal(PSL.recapFreezeText(PSL.readRecapRoute("#/recap")), "Freezes the last closed week.");
    assert.match(PSL.recapErrorText(403, "tech"), /findings\.read/);
    assert.match(PSL.recapErrorText(500, "tech"), /not any daemon/);
});

test("a named service reads by its name, the technical one beside it", () => {
    assert.deepEqual(PSL.serviceParts("checkout-api", "Payment"), {name: "Payment", code: "checkout-api"});
    assert.deepEqual(PSL.serviceParts("checkout-api", null), {name: "checkout-api", code: null});
    assert.deepEqual(PSL.serviceParts("checkout-api", undefined), {name: "checkout-api", code: null});
    assert.deepEqual(PSL.serviceParts("checkout-api", ""), {name: "checkout-api", code: null});
    // The Management "in ..." line reads the name alone.
    assert.equal("in " + PSL.serviceParts("checkout-api", "Payment").name, "in Payment");
});

test("only the first card of a service offers to name it", () => {
    const rows = [{service: "a"}, {service: "b"}, {service: "a"}, {service: "c"}, {service: "b"}];
    assert.deepEqual(rows.map((row, index) => PSL.namesService(rows, index)), [true, true, false, true, false]);
});

test("the Management help says a team can name its services and steps", () => {
    assert.ok(PSL.RECAP_HELP.pilotage.prio.endsWith(
        " A team can name its services and steps in the Hub, so this report speaks its language."));
    assert.doesNotMatch(PSL.RECAP_HELP.tech.prio, /name its services/);
});

test("Management_help_carries_no_technical_word", () => {
    const technical = TECHNICAL;
    const keys = ["period", "io", "co2", "findings", "actions", "prio", "moved"];
    assert.deepEqual(Object.keys(PSL.RECAP_HELP.tech), keys);
    assert.deepEqual(Object.keys(PSL.RECAP_HELP.pilotage), keys);
    for (const text of Object.values(PSL.RECAP_HELP.pilotage)) assert.doesNotMatch(text, technical);
});

test("a recap report is named by its period and who it came from", () => {
    const week = {
        subscription: "weekly-shop",
        period: {kind: "week", from: "2026-10-19", to: "2026-10-25"},
        complete: true
    };
    assert.equal(PSL.recapReportLabel(week), "Week 2026-10-19 to 2026-10-25 · weekly-shop");
    assert.equal(PSL.recapReportLabel({...week, complete: false}),
        "Week 2026-10-19 to 2026-10-25 · weekly-shop · incomplete");
    assert.equal(PSL.recapReportLabel({
        subscription: null,
        created_by: "alice",
        period: {kind: "day", from: "2026-10-28", to: "2026-10-28"},
        complete: true
    }), "Day 2026-10-28 · on demand by alice");
    assert.equal(PSL.recapReportLabel({
        subscription: null,
        created_by: "bob",
        period: {kind: "month", from: "2026-09-01", to: "2026-09-30"},
        complete: false
    }), "Month 2026-09-01 to 2026-09-30 · on demand by bob · incomplete");
});

test("a recap report says why nothing was sent", () => {
    const sent = {subscription: "weekly-shop", nothing_moved: false, deliveries: [{channel: "webhook"}]};
    assert.equal(PSL.recapReportStatus({...sent, nothing_moved: true, deliveries: []}),
        "Archived, not sent: nothing moved");
    assert.equal(PSL.recapReportStatus({
        subscription: null,
        nothing_moved: false,
        deliveries: []
    }), "On demand, not sent");
    assert.equal(PSL.recapReportStatus({
        subscription: null,
        nothing_moved: true,
        deliveries: []
    }), "On demand, not sent");
    assert.equal(PSL.recapReportStatus({...sent, deliveries: []}), "Archived, no destination");
    assert.equal(PSL.recapReportStatus(sent), "");
});

test("a recap delivery reads as where it went and how it fared", () => {
    assert.equal(PSL.recapDeliveryText({
            channel: "webhook",
            target: "ops-chat",
            state: "delivered",
            attempts: 0,
            last_status: null
        }),
        "webhook ops-chat: delivered");
    assert.equal(PSL.recapDeliveryText({
            channel: "email",
            target: "pm@example.test",
            state: "failed",
            attempts: 1,
            last_status: 550
        }),
        "email pm@example.test: failed (550), 1 try");
    assert.equal(PSL.recapDeliveryText({
            channel: "webhook",
            target: "ops",
            state: "pending",
            attempts: 3,
            last_status: 500
        }),
        "webhook ops: pending (500), 3 tries");
    assert.equal(PSL.recapDeliveryText({channel: "webhook", target: "ops", state: "pending", attempts: 2}),
        "webhook ops: pending, 2 tries");
});

test("a generate request carries the screen's period and filters, and a date only when one is set", () => {
    const route = PSL.readRecapRoute("#/recap?period=month&service=shop&view=pilotage");
    assert.deepEqual(PSL.recapOnDemandBody(route), {
        period: "month",
        environment: null,
        service: "shop",
        grouping: null
    });
    assert.deepEqual(
        PSL.recapOnDemandBody(PSL.readRecapRoute("#/recap?at=2026-10-14&environment=prod&namespace=x&grouping=checkout")),
        {period: "week", at: "2026-10-14", environment: "prod", service: null, grouping: "checkout"});
});

test("a timestamp reads in the browser's zone, in a fixed order", () => {
    const kept = process.env.TZ;
    try {
        process.env.TZ = "Europe/Paris";
        // The clocks went back on 2026-10-25, so the 29th is winter time.
        assert.equal(PSL.localTime(Date.UTC(2026, 9, 29, 13, 32, 59)), "2026-10-29 14:32 CET");
        assert.equal(PSL.localTime(Date.UTC(2026, 6, 14, 13, 32)), "2026-07-14 15:32 CEST");
        // Midnight is 00, never 24.
        assert.equal(PSL.localTime(Date.UTC(2026, 9, 28, 23, 5)), "2026-10-29 00:05 CET");
    } finally {
        if (kept === undefined) delete process.env.TZ; else process.env.TZ = kept;
    }
});

test("app.js declares each of its top-level functions once", () => {
    // app.js is one IIFE: a second declaration of a name silently replaces the
    // first for every caller, as the Disclosures screen's actionButton did to
    // the run page's "Open the dashboard". Top level only, nested scopes may
    // reuse a name.
    const fs = require("node:fs");
    const path = require("node:path");
    const source = fs.readFileSync(
        path.join(__dirname, "..", "PerfSentinelHub", "wwwroot", "app.js"), "utf8");
    const names = [...source.matchAll(/^ {4}function ([A-Za-z0-9_$]+)\(/gm)].map(m => m[1]);
    assert.ok(names.length > 100, "the top-level pattern no longer matches app.js");
    const twice = names.filter((name, i) => names.indexOf(name) !== i);
    assert.deepEqual(twice, []);
});

// ---- share cards

test("a card period reads in words, as the Hub's CardRules.PeriodName", () => {
    assert.equal(PSL.cardPeriodName("month", "2026-10-01"), "October 2026");
    assert.equal(PSL.cardPeriodName("quarter", "2026-07-01"), "Q3 2026");
    assert.equal(PSL.cardPeriodName("semester", "2026-01-01"), "the first half of 2026");
    assert.equal(PSL.cardPeriodName("semester", "2026-07-01"), "the second half of 2026");
    assert.equal(PSL.cardPeriodName("year", "2026-01-01"), "2026");
    assert.equal(PSL.cardPeriodName("week", "2026-10-05"), "the week of 5 October 2026");
    assert.deepEqual(PSL.CARD_PERIODS, ["week", "month", "quarter", "semester", "year"]);
    assert.equal(PSL.CARD_PERIOD_LABELS.semester, "Half-year");
    assert.deepEqual(PSL.CARD_ANGLES, ["story", "curve", "collective"]);
});

test("only a closed card period can be picked", () => {
    assert.equal(PSL.lastClosedPeriod("month", "2026-11-07"), "2026-10-01");
    assert.equal(PSL.lastClosedPeriod("quarter", "2026-11-07"), "2026-07-01");
    assert.equal(PSL.lastClosedPeriod("semester", "2026-11-07"), "2026-01-01");
    assert.equal(PSL.lastClosedPeriod("year", "2026-11-07"), "2025-01-01");
    // 2026-11-07 is a Saturday: the last closed week ran from Monday 26 October.
    assert.equal(PSL.lastClosedPeriod("week", "2026-11-07"), "2026-10-26");
    assert.equal(PSL.lastClosedPeriod("month", "2026-11-01"), "2026-10-01");
    assert.equal(PSL.canStepForward("month", "2026-10-01", "2026-11-07"), false);
    assert.equal(PSL.canStepForward("month", "2026-09-01", "2026-11-07"), true);
    assert.equal(PSL.stepPeriod("semester", "2026-07-01", -1), "2026-01-01");
    assert.equal(PSL.stepPeriod("month", "2026-01-01", -1), "2025-12-01");
    assert.equal(PSL.stepPeriod("quarter", "2025-10-01", 1), "2026-01-01");
    assert.equal(PSL.stepPeriod("week", "2026-10-26", -1), "2026-10-19");
    assert.equal(PSL.cardPeriodStart("quarter", "2026-08-14"), "2026-07-01");
});

test("a card period names its days", () => {
    assert.equal(PSL.cardRangeText("2026-10-01", "2026-10-31"), "1 to 31 October 2026, UTC");
    assert.equal(PSL.cardRangeText("2026-07-01", "2026-09-30"), "1 July to 30 September 2026, UTC");
    assert.equal(PSL.cardRangeText("2025-12-29", "2026-01-04"), "29 December 2025 to 4 January 2026, UTC");
});

test("a card body carries only the values set, the story's only on a story", () => {
    assert.deepEqual(PSL.cardBody({angle: "curve", period: "month", at: "", label: "x", title: " ", format: "png"}),
        {angle: "curve", period: "month", format: "png"});
    assert.deepEqual(PSL.cardBody({
        angle: "story", period: "month", at: "2026-10-01", environment: "prod", source_id: "",
        signature: "n_plus_one_sql:x", label: " Showing a cart ", title: "Lighter", team: "", lang: "fr", format: "pdf"
    }), {
        angle: "story", period: "month", at: "2026-10-01", environment: "prod", signature: "n_plus_one_sql:x",
        label: "Showing a cart", title: "Lighter", lang: "fr", format: "pdf"
    });
    assert.deepEqual(PSL.cardBody({angle: "collective", period: "month", format: "svg"}),
        {angle: "collective", period: "month", format: "svg"});
    assert.equal(PSL.cardOptionsPath({period: "month", at: "2026-10-01", environment: "a b&c"}),
        "/api/cards/options?period=month&at=2026-10-01&environment=a%20b%26c");
    assert.equal(PSL.cardOptionsPath({period: "year", at: "", environment: null, source_id: "s"}),
        "/api/cards/options?period=year&source_id=s");
});

test("a name is remembered only by someone allowed to name actions, once typed or changed", () => {
    assert.equal(PSL.canRememberLabel([{permissions: ["findings.read"]}, {permissions: ["labels.write"]}]), true);
    assert.equal(PSL.canRememberLabel([{permissions: ["findings.read"]}]), false);
    assert.equal(PSL.canRememberLabel([{permissions: null}]), true);
    assert.equal(PSL.canRememberLabel(null), false);
    assert.deepEqual(PSL.rememberState("", null, "this action"),
        {text: "Remember this name for this action", enabled: false});
    assert.deepEqual(PSL.rememberState("Showing a cart", null, "this action"),
        {text: "Remember this name for this action", enabled: true});
    assert.deepEqual(PSL.rememberState(" Showing a cart ", "Showing a cart", "GET /cart"),
        {text: "Update the remembered name with this one", enabled: false});
    assert.deepEqual(PSL.rememberState("Opening a cart", "Showing a cart", "GET /cart"),
        {text: "Update the remembered name with this one", enabled: true});
});

test("a free field names the first character the card cannot draw", () => {
    assert.equal(PSL.cardFieldProblem("Lighter cart \u{1F331}"),
        "The card's font cannot draw \"\u{1F331}\". Remove it, or write it another way.");
    assert.equal(PSL.cardFieldProblem("Paniers plus légers"), null);
    assert.equal(PSL.cardFieldProblem("a\u200Bb"),
        "The card's font cannot draw \"\u200B\". Remove it, or write it another way.");
    assert.equal(PSL.cardFieldProblem("Acme® checkout™"), null);
    assert.equal(PSL.cardFieldProblem(""), null);
});

test("the attachment check names the hosts it reaches, plainly for management", () => {
    assert.equal(PSL.cardContactText("acme.example", "tech"),
        "Checking contacts cloudflare-dns.com (DNS over HTTPS) and acme.example.");
    assert.equal(PSL.cardContactText("acme.example", "pilotage"),
        "Checking contacts cloudflare-dns.com and acme.example.");
});

test("an attachment verdict reads as one sentence, the raw reason in the title only", () => {
    const base = {integrity: "ok", level: 1, domain: "acme.example", key: "unknown", reason: "key_not_published"};
    assert.deepEqual(PSL.attachmentText({...base, integrity: "tampered", reason: "signature_invalid"}, true),
        {text: "This card does not verify: its figures changed after signing.", title: null});
    assert.deepEqual(PSL.attachmentText({...base, level: 2, key: "current", reason: "key_published"}, false),
        {text: "Attached: cards read \"Signed by acme.example\".", title: null});
    assert.deepEqual(PSL.attachmentText({...base, reason: "binding_check_failed"}, true), {
        text: "Could not check: the places that publish the key under acme.example did not answer.",
        title: "binding_check_failed"
    });
    assert.deepEqual(PSL.attachmentText({...base, reason: "something_new"}, true),
        {text: "Could not check: an unexpected answer.", title: "something_new"});
    assert.deepEqual(PSL.attachmentText(base, true),
        {text: "Not attached yet. Publish one of the lines below, then check again.", title: null});
    assert.deepEqual(PSL.attachmentText(base, false), {
        text: "Not attached yet. An administrator publishes one line under acme.example, then you can check again.",
        title: null
    });
    for (const reason of ["binding_check_failed", "key_not_published", "binding_partial", "no_domain"]) {
        assert.ok(PSL.ATTACH_REASONS[reason], reason);
    }
    assert.equal(PSL.ATTACH_REASONS.signed_after_revocation, undefined);
    assert.equal(PSL.attachmentText({...base, reason: "key_not_published", level: 1, integrity: "ok"}, true).title, null);
});

test("a revoked key reads as a verdict, not as a check that failed", () => {
    assert.deepEqual(PSL.attachmentText(
        {integrity: "ok", level: 1, domain: "acme.example", key: "revoked", reason: "key_revoked"}, true),
    {text: "This card was signed with a key that is now revoked.", title: "key_revoked"});
});

test("a mismatched key and a date outside the key's window read as verdicts", () => {
    const base = {integrity: "ok", content: "intact", level: 1, domain: "acme.example", key: "unknown"};
    assert.deepEqual(PSL.attachmentText({...base, reason: "public_key_mismatch"}, true), {
        text: "This card was signed with a key other than the one published under acme.example.",
        title: "public_key_mismatch"
    });
    assert.deepEqual(PSL.attachmentText({...base, reason: "signed_outside_validity"}, false), {
        text: "This card is dated outside the period its key was published for.",
        title: "signed_outside_validity"
    });
    assert.equal(PSL.ATTACH_REASONS.public_key_mismatch, undefined);
    assert.equal(PSL.ATTACH_REASONS.signed_outside_validity, undefined);
});

test("a modified file never reads attached", () => {
    const result = PSL.attachmentText({
        integrity: "ok", content: "modified", level: 2, domain: "acme.example", key: "current",
        reason: "content_modified"
    }, true);
    assert.deepEqual(result, {
        text: "This card file was changed after it was signed. Make the card again before sharing it.",
        title: "content_modified"
    });
    assert.ok(!result.text.includes("Attached"));
});

test("the content line says the file is the one signed, and only then", () => {
    assert.equal(PSL.contentLine({content: "intact"}), "File intact: this is the file that was signed.");
    assert.equal(PSL.contentLine({content: "modified"}), null);
    assert.equal(PSL.contentLine({content: "not_checked"}), null);
});

test("the signed figures read in the label order, exact", () => {
    assert.deepEqual(PSL.signedFigureLines({
        angle: "story",
        period: {kind: "month", from: "2026-09-01", to: "2026-09-30"},
        before_fix_period: {from: "2026-07-01", to: "2026-07-31"},
        cited_ops: 41,
        before_fix_ops: 1937000
    }), [
        {label: "Period", value: "September 2026"},
        {label: "Period before the fix", value: "July 2026"},
        {label: "Headline figure on the card", value: "41"},
        {label: "Unnecessary operations before the fix", value: "1,937,000"}
    ]);
    const month = {kind: "month", from: "2026-09-01", to: "2026-09-30"};
    assert.deepEqual(PSL.signedFigureLines(
        {angle: "curve", period: month, last_waste_permille: 31, previous_waste_permille: 48}).slice(1), [
        {label: "Share of the work that was avoidable, period of the card", value: "3.1%"},
        {label: "Share of the work that was avoidable, period before", value: "4.8%"}
    ]);
    assert.deepEqual(PSL.signedFigureLines(
        {angle: "story", period: month, cited_ms: 2349, threshold_ms: 500}).slice(1), [
        {label: "Headline figure on the card", value: "2,349 milliseconds"},
        {label: "Alert threshold the card cites", value: "500 milliseconds"}
    ]);
    assert.deepEqual(PSL.signedFigureLines(
        {angle: "collective", period: month, confirmed_fixes_count: 1200, services_count: 3}).slice(1), [
        {label: "Fixes confirmed by measurement", value: "1,200"},
        {label: "Services concerned", value: "3"}
    ]);
    assert.deepEqual(PSL.signedFigureLines(null), []);
});

test("a partial lookup reads as could not check", () => {
    const result = PSL.attachmentText(
        {integrity: "ok", level: 1, domain: "acme.example", key: "unknown", reason: "binding_partial"}, true);
    assert.deepEqual(result, {
        text: "Could not check: one of the two places that publish the key under acme.example did not answer.",
        title: "binding_partial"
    });
    assert.ok(!result.text.includes("Attached"));
});

test("the revoke confirmation says every card reads revoked", () => {
    // The consequence is the alert; the DNS steps read apart from it, in plain text.
    const said = PSL.revokeText("2026-10-01", false, "acme.example");
    assert.equal(said.alert, "Every card this key signed will read as revoked on the check page, whatever its "
        + "date. The check page will say the key may have been exposed since 2026-10-01. This cannot be undone.");
    assert.equal(said.step, "Republish this key's DNS line under acme.example: the check page reads the DNS line "
        + "only, and keeps showing these cards as signed until the line says revoked.");
    assert.ok(PSL.revokeText("2026-10-01", false, null).step.startsWith("Republish this key's DNS line:"));
    assert.ok(PSL.revokeText("2026-10-01", true, "acme.example").step.endsWith("publish its line too, under acme.example."));
    assert.ok(PSL.revokeText("2026-10-01", true, null).step.endsWith("makes a new key."));
});

test("the level line says what the check page shows", () => {
    assert.equal(PSL.levelLine(false, "acme.example"),
        "On the check page, this card reads: Figures intact since their signature. Signer not verified.");
    assert.equal(PSL.levelLine(true, "acme.example"), "On the check page, this card reads: Signed by acme.example.");
});

test("a fix row says what the card shows when its problem has no volume", () => {
    assert.deepEqual(PSL.cardFixLine({finding_type: "n_plus_one_sql", before_fix_ops: 1_940_000,
        before_period_name: "September 2026"}),
    {figure: "1.9 million", text: " unnecessary operations in September 2026, before the fix"});
    assert.equal(PSL.cardFixLine({finding_type: "redundant_http", before_fix_ops: 410_500,
        before_period_name: "September 2026"}).figure, "410 thousand");
    assert.equal(PSL.cardFixLine({finding_type: "redundant_http", before_fix_ops: 9_999,
        before_period_name: "September 2026"}).figure, "9,999");
    assert.deepEqual(PSL.cardFixLine({finding_type: "slow_sql", before_fix_ops: null}),
        {figure: null, text: "the card shows how long its slowest runs took"});
    assert.deepEqual(PSL.cardFixLine({finding_type: "chatty_service", before_fix_ops: null}),
        {figure: null, text: "the card shows how many calls one action made"});
});

test("a card downloads as a PNG, an SVG, a JPEG XL or a PDF, each with its label", () => {
    assert.deepEqual(PSL.CARD_FORMATS, ["png", "svg", "jxl", "pdf"]);
    for (const format of PSL.CARD_FORMATS) assert.ok(PSL.CARD_FORMAT_LABELS[format], format);
    assert.deepEqual(Object.keys(PSL.CARD_FORMAT_LABELS), PSL.CARD_FORMATS);
    // The preview's note: an SVG has no fixed pixel size.
    for (const format of PSL.CARD_FORMATS) assert.ok(PSL.CARD_COPY[PSL.previewFormat(format) + "Note"], format);
    assert.ok(!PSL.CARD_COPY.svgNote.includes("pixels"));
    // Only the JPEG XL carries a note under the choice: where it does not go.
    assert.deepEqual(Object.keys(PSL.CARD_FORMAT_NOTES), ["jxl"]);
    assert.ok(PSL.CARD_FORMAT_NOTES.jxl.includes("LinkedIn"));
});

test("the preview of a JPEG XL choice renders a PNG", () => {
    assert.equal(PSL.previewFormat("jxl"), "png");
    assert.equal(PSL.previewFormat("png"), "png");
    assert.equal(PSL.previewFormat("svg"), "svg");
    assert.equal(PSL.previewFormat("pdf"), "pdf");
    assert.deepEqual(PSL.cardBody({angle: "collective", period: "month", format: "jxl"}),
        {angle: "collective", period: "month", format: "jxl"});
});

test("a busy render gate says so and when to try again", () => {
    assert.deepEqual(PSL.cardBusy(503, "5"),
        {text: "Another card is being made. Trying again in a few seconds.", delayMs: 5000});
    // The engine-off 503 carries no Retry-After.
    assert.equal(PSL.cardBusy(503, null), null);
    assert.equal(PSL.cardBusy(422, "5"), null);
    assert.equal(PSL.cardBusy(503, "600").delayMs, 10000);
    assert.equal(PSL.cardBusy(503, "0").delayMs, 1000);
    assert.equal(PSL.cardBusy(503, "Wed, 21 Oct 2026 07:28:00 GMT").delayMs, 1000);
});

test("Card_copy_carries_no_technical_word", () => {
    const words = ["I/O", "finding", "query", "endpoint", "signature", "DNS"];
    const types = Object.keys(PSL.FINDING_TYPE_LABEL);
    const texts = [
        ...Object.values(PSL.CARD_ANGLE_LINES),
        ...Object.values(PSL.CARD_PERIOD_LABELS),
        ...Object.values(PSL.CARD_FORMAT_LABELS),
        ...Object.values(PSL.CARD_FORMAT_NOTES),
        PSL.cardEnvironmentLine("prod", "pilotage"),
        PSL.cardEnvironmentLine(null, "pilotage"),
        PSL.cardContactText("acme.example", "pilotage"),
        ...types.map((type) => PSL.cardFixLine({finding_type: type, before_fix_ops: null}).text),
        PSL.cardFixLine({finding_type: "n_plus_one_sql", before_fix_ops: 5, before_period_name: "May 2026"}).text,
        ...Object.values(PSL.SIGNED_FIGURE_LABELS),
        PSL.contentLine({content: "intact"}),
        PSL.attachmentText({integrity: "ok", content: "modified", level: 1, domain: null, reason: "content_modified"},
            false).text,
        PSL.CARD_COPY.busy
    ];
    for (const text of texts) {
        for (const word of words) assert.ok(!text.includes(word), `"${text}" holds "${word}"`);
    }
    assert.equal(PSL.cardEnvironmentLine("prod", "tech"),
        "Environment prod, taken from the recap filter above. A card covers every service in it: "
        + "the service and namespace filters do not apply.");
    assert.equal(PSL.cardEnvironmentLine("prod", "pilotage"),
        "Environment prod, taken from the recap filter above. A card covers every service in it.");
});

test("only the refusal of the period before offers the other two angles", () => {
    // CardRules.Explain's previous_ sentence, as a curve render's 422 carries it.
    assert.equal(PSL.isPreviousRefusal("No curve for October 2026. The curve compares with September 2026, and "
        + "some of its days were not recorded. A story or a collective card still works for October 2026."), true);
    assert.equal(PSL.isPreviousRefusal("The curve covers whole sources, and you read only part of one."), false);
    assert.equal(PSL.isPreviousRefusal(null), false);
});

// ---- the shell in two languages

const SHELL_FUNCTIONS = ["renderShell", "renderUpdates", "renderFleetSkew", "applyTheme", "helpDot",
    "terminalBlock", "downloadButton", "engineNeed", "tokenStep", "stepsBlock", "shellTabs", "applyStaticText", "engineText", "initLang"];

function wwwroot(file) {
    return require("node:fs").readFileSync(
        require("node:path").join(__dirname, "..", "PerfSentinelHub", "wwwroot", file), "utf8");
}

test("the shell holds no prose literal", () => {
    const source = wwwroot("app.js");
    for (const name of SHELL_FUNCTIONS) {
        assert.ok(source.includes("function " + name + "("), "app.js has no " + name);
    }
    assert.deepEqual(PSL.literalsIn(source, SHELL_FUNCTIONS), []);
});

test("every data-i18n key of index.html exists", () => {
    const html = wwwroot("index.html");
    const keys = [...html.matchAll(/data-i18n="([^"]+)"/g)].map(m => m[1]);
    for (const m of html.matchAll(/data-i18n-attr="([^"]+)"/g)) {
        for (const pair of m[1].split(";")) keys.push(pair.split(":")[1]);
    }
    // The tabs, the footer and the language button are all read from the tables.
    assert.ok(keys.length >= 12, "index.html marks only " + keys.length + " texts");
    assert.match(html, /id="lang-toggle"/);
    const missing = keys.filter(key => !Object.prototype.hasOwnProperty.call(globalThis.PSL_TEXT.en, key));
    assert.deepEqual(missing, []);
});

test("boot adopts the page's language without storing it", () => {
    // Only the button's click may call setLang with storage on.
    assert.match(wwwroot("app.js"), /PSL\.setLang\(document\.documentElement\.lang, false\)/);
});
