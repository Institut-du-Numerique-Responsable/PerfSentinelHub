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

test("a Tempo source carries its reads count after the cap, a Jaeger source never does", () => {
    const request = {service: "orders", max_traces: 100, lookback: "1h", fetch_concurrency: 4};

    assert.equal(
        PSL.analysisCommand(tempo, request),
        "perf-sentinel tempo --endpoint http://tempo.obs.svc:3200 --service orders \\\n"
        + "  --lookback 1h --max-traces 100 --fetch-concurrency 4");
    // `jaeger-query` has no such flag: the engine would stop on it.
    const jaeger = {kind: "jaeger_query", engine_subcommand: "jaeger-query", base_url: "http://jaeger:16686"};
    assert.ok(!PSL.analysisCommand(jaeger, request).includes("--fetch-concurrency"));
    // A request without the field, a trace run or an older Hub, prints none either.
    assert.ok(!PSL.analysisCommand(tempo, {service: "orders", max_traces: 100, lookback: "1h"})
        .includes("--fetch-concurrency"));
});

test("data-over is written as the string the stylesheet matches, never as a bare presence", () => {
    const read = file => require("node:fs").readFileSync(
        require("node:path").join(__dirname, "..", "PerfSentinelHub", "wwwroot", file), "utf8");
    const app = read("app.js");
    const css = read("launcher.css");

    // toggleAttribute writes data-over="", which `[data-over="true"]` never matches.
    assert.ok(!/toggleAttribute\(\s*'data-over'/.test(app));
    assert.ok(app.includes("number.setAttribute('data-over', bad ? 'true' : 'false')"), "the reads field");
    assert.ok(app.includes("number.setAttribute('data-over', over ? 'true' : 'false')"), "the traces field");
    const selectors = [...css.matchAll(/\[data-over[^\]]*\]/g)].map(match => match[0]);
    assert.ok(selectors.length > 0);
    assert.deepEqual(selectors.filter(selector => selector !== '[data-over="true"]'), []);
});

test("a count that is the engine's own default is left out of the printed command, any other is printed", () => {
    const withCount = count => PSL.analysisCommand(tempo,
        {service: "orders", max_traces: 100, lookback: "1h", fetch_concurrency: count});

    // The run is the same, and a binary older than 0.43.0 would refuse a flag nobody set.
    assert.ok(!withCount(16).includes("--fetch-concurrency"));
    assert.ok(withCount(4).endsWith("--fetch-concurrency 4"));
    assert.ok(withCount(17).endsWith("--fetch-concurrency 17"));
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

test("a settings group opened under its old English heading stays open under its key", () => {
    assert.deepEqual(PSL.daemonGroupFolds({
        "s1|// declared": true,
        "s1|// ingestion and memory": true,
        "s2|// what it keeps for readers": true,
        "s2|// metrics labels": false,
        "s3|// listeners": true,
        "s3|// sub-systems": true,
        "s3|// correlation": true,
        "s3|labels": true,
        "a|b|// declared": true,
        "s4|// unknown": true,
    }), {
        "s1|declared": true,
        "s1|ingestion": true,
        "s2|readers": true,
        "s3|listeners": true,
        "s3|subsystems": true,
        "s3|correlation": true,
        "s3|labels": true,
        "a|b|declared": true,
        "s4|// unknown": true,
    });
    assert.deepEqual(PSL.daemonGroupFolds(null), {});
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
        source_id: "shop prod",
        service: "shop svc",
        window_from_ms: 1000,
        window_to_ms: 5000
    };
    const hash = PSL.incidentHandoffHash(incident, 9000);
    assert.equal(hash,
        "#/new?from=1000&to=5000&service=shop%20svc&incident=d650edad80ac5c2d99b8d1dde07100c2&source=shop%20prod");
    assert.deepEqual(PSL.readHandoff(hash, 9000), {
        fromMs: 1000, toMs: 5000, service: "shop svc", incidentId: "d650edad80ac5c2d99b8d1dde07100c2", pairKey: "",
        sourceId: "shop prod"
    });
});

test("a handoff link from before the source rode along still reads, with no source", () => {
    const old = "#/new?from=1000&to=5000&service=svc&incident=abc";
    assert.deepEqual(PSL.readHandoff(old, 9000),
        {fromMs: 1000, toMs: 5000, service: "svc", incidentId: "abc", pairKey: "", sourceId: null});
    assert.doesNotMatch(PSL.incidentHandoffHash({id: "abc", service: "svc", window_from_ms: 1000, window_to_ms: 5000}, 9000),
        /source=/, "an incident that names no source writes none");
    assert.equal(PSL.readHandoff("#/new?from=1000&to=5000&service=svc&source=", 9000).sourceId, null, "blank source");
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
    assert.equal(window.fetchConcurrency, undefined, "a request without the count leaves the form's own");
    assert.equal(PSL.formFromRequest({service: "orders", fetch_concurrency: 4}).fetchConcurrency, 4);
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

test("a reads count is comfortable up to the warning, confirmed above it and refused above the max", () => {
    assert.deepEqual(PSL.fetchBand(16, 16, 64), {key: "ok", needsAck: false});
    assert.deepEqual(PSL.fetchBand(17, 16, 64), {key: "over", needsAck: true});
    assert.deepEqual(PSL.fetchBand(64, 16, 64), {key: "over", needsAck: true});
    assert.deepEqual(PSL.fetchBand(8, 4, 8), {key: "over", needsAck: true});
    // Past the max there is nothing to confirm: the run is refused, not risked.
    assert.deepEqual(PSL.fetchBand(9, 4, 8), {key: "blocked", needsAck: false});
    assert.deepEqual(PSL.fetchBand(1, 1, 1), {key: "ok", needsAck: false});
});

test("an empty, negative, decimal or unreadable reads count is invalid, and nothing to confirm", () => {
    // Number("") is 0, which is what an emptied number field reports.
    for (const typed of [0, -3, 3.5, NaN, Infinity, Number("")]) {
        assert.deepEqual(PSL.fetchBand(typed, 16, 64), {key: "invalid", needsAck: false}, String(typed));
    }
});

test("the reads count starts at the engine's 16, lowered to the warning or the max", () => {
    assert.equal(PSL.fetchDefault(16, 64), 16);
    assert.equal(PSL.fetchDefault(4, 8), 4);
    assert.equal(PSL.fetchDefault(32, 8), 8);
    assert.equal(PSL.fetchDefault(64, 64), 16);
});

const fetchLimits = {fetch_concurrency_warn: 4, fetch_concurrency_max: 8, fetch_concurrency_supported: true};
const jaegerSource = {kind: "jaeger_query", engine_subcommand: "jaeger-query"};

test("the reads count is on screen for a Tempo service run on an engine that reads it, nowhere else", () => {
    assert.equal(PSL.fetchAccess(tempo, "service", fetchLimits), "shown");
    assert.equal(PSL.fetchAccess(jaegerSource, "service", fetchLimits), "hidden");
    assert.equal(PSL.fetchAccess(tempo, "trace", fetchLimits), "hidden");
    assert.equal(PSL.fetchAccess({kind: "daemon"}, "service", fetchLimits), "hidden");
    assert.equal(PSL.fetchAccess(null, "service", fetchLimits), "hidden");
});

test("an engine that cannot apply the count leaves it out, and refuses only a max under its default", () => {
    const old = {fetch_concurrency_warn: 4, fetch_concurrency_max: 8, fetch_concurrency_supported: false};
    assert.equal(PSL.fetchAccess(tempo, "service", old), "refused");
    assert.equal(PSL.fetchAccess(tempo, "service", {...old, fetch_concurrency_max: 16}), "hidden");
    assert.equal(PSL.fetchAccess(tempo, "service", {...old, fetch_concurrency_max: 64}), "hidden");
    // Nothing is refused on a run that reads no count: Jaeger, or one trace by ID.
    assert.equal(PSL.fetchAccess(jaegerSource, "service", old), "hidden");
    assert.equal(PSL.fetchAccess(tempo, "trace", old), "hidden");
});

test("a status without the fields, an older Hub or capture, reads as not supported and does not throw", () => {
    const limits = {max_traces_cap: 2000, analysis_timeout_seconds: 300};
    assert.equal(PSL.fetchAccess(tempo, "service", limits), "hidden");
    assert.equal(PSL.fetchRequested({mode: "service", fetchConcurrency: null}, tempo, limits), undefined);
    assert.equal(PSL.fetchAccess(tempo, "service", {}), "hidden");
});

test("the request carries the reads count only while the block is on screen", () => {
    const form = {mode: "service", fetchConcurrency: null};
    // Untouched: the default, lowered to the warning.
    assert.equal(PSL.fetchRequested(form, tempo, fetchLimits), 4);
    assert.equal(PSL.fetchRequested(form, tempo, {...fetchLimits, fetch_concurrency_warn: 16, fetch_concurrency_max: 64}), 16);
    // Typed: what was typed, even a value that will be refused, so the screen can say so.
    assert.equal(PSL.fetchRequested({...form, fetchConcurrency: 40}, tempo, fetchLimits), 40);
    assert.equal(PSL.fetchRequested({...form, fetchConcurrency: 0}, tempo, fetchLimits), 0);
    // Moving to a Jaeger source, or to a trace ID, takes the field off the request, typed value or not.
    const typed = {mode: "service", fetchConcurrency: 6};
    assert.equal(PSL.fetchRequested(typed, jaegerSource, fetchLimits), undefined);
    assert.equal(PSL.fetchRequested({...typed, mode: "trace"}, tempo, fetchLimits), undefined);
});

test("the summary line of a run restates its reads count", () => {
    assert.match(PSL.argsLine({request: {service: "orders", max_traces: 100, fetch_concurrency: 4}}),
        /max_traces = 100 {3}· {3}fetch_concurrency = 4/);
    assert.ok(!PSL.argsLine({request: {service: "orders", max_traces: 100}}).includes("fetch_concurrency"));
});

/** The text of a top-level function of app.js, to its closing brace. */
function appFunction(name) {
    const source = wwwroot("app.js");
    const start = source.indexOf("\n    function " + name + "(");
    assert.ok(start >= 0, "app.js has no " + name);
    return source.slice(start, source.indexOf("\n    }\n", start));
}

test("choosing another source or another mode withdraws the confirmation of the reads count", () => {
    // The tick belongs to one run's one count. app.js cannot run under Node, so this reads it:
    // both paths that change what the block shows reset the box in the same breath as ackHeavy.
    assert.match(appFunction("switchSource"), /state\.form\.ackFetch = false/);
    assert.match(appFunction("setMode"), /state\.form\.ackFetch = false/);
    assert.match(appFunction("rerun"), /ackFetch: false/);
    // And the request takes the count from the one function the block itself reads.
    assert.match(appFunction("buildRequest"), /PSL\.fetchRequested\(/);
});

test("a link moves the form to a trace backend through the same switch as a click, and saves nothing", () => {
    // The reader's saved source is a preference: only their own click writes it. Same reading of app.js as above.
    const click = appFunction("selectSource");
    assert.match(click, /saveSource\(id\)/);
    assert.match(click, /switchSource\(id\)/);
    const link = appFunction("openHandoffSource");
    assert.match(link, /PSL\.handoffSourceId\(/);
    assert.match(link, /switchSource\(id\)/);
    assert.doesNotMatch(link, /saveSource/);
    assert.doesNotMatch(appFunction("switchSource"), /saveSource/);
    // Both ways a link reaches the form, a window and a trace.
    assert.match(appFunction("applyHandoff"), /openHandoffSource\(handoff\.sourceId\)/);
    assert.match(appFunction("applyTraceHandoff"), /openHandoffSource\(handoff\.sourceId\)/);
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
    assert.equal(hash,
        "#/new?from=940000&to=1120000&service=order%20svc&pair=" + "a".repeat(32) + "&source=checkout-prod");
    assert.deepEqual(PSL.readHandoff(hash, 9_000_000), {
        fromMs: 940000, toMs: 1120000, service: "order svc", incidentId: "", pairKey: "a".repeat(32),
        sourceId: "checkout-prod"
    });
    assert.doesNotMatch(PSL.episodeHandoffHash(episode({source_id: undefined}), 9_000_000), /source=/);
});

test("a running episode's window ends now, never in a future the form refuses", () => {
    assert.match(PSL.episodeHandoffHash(episode({ended_at_ms: null}), 1_500_000), /&to=1500000&/);
    assert.match(PSL.episodeHandoffHash(episode({}), 1_100_000), /&to=1100000&/, "a minute past the end is not yet");
});

test("the last pair's traces open New analysis on the first one, the other carried along", () => {
    assert.equal(PSL.pairTraceHash(episode({})),
        "#/new?trace=" + "c".repeat(32) + "&other=" + "b".repeat(32) + "&source=checkout-prod");
    assert.equal(PSL.pairTraceHash(episode({source_sample_trace_id: null})),
        "#/new?trace=" + "b".repeat(32) + "&source=checkout-prod");
    assert.equal(PSL.pairTraceHash(episode({source_sample_trace_id: null, sample_trace_id: null})), null);
    assert.deepEqual(PSL.readTraceHandoff("#/new?trace=x1&other=y2&source=s1"),
        {traceId: "x1", otherTraceId: "y2", sourceId: "s1"});
    assert.deepEqual(PSL.readTraceHandoff("#/new?trace=x1"), {traceId: "x1", otherTraceId: "", sourceId: null},
        "a link from before the source rode along");
    assert.equal(PSL.readTraceHandoff("#/new?from=1&to=2&service=s"), null);
    assert.equal(PSL.readTraceHandoff("#/correlations?trace=x1"), null);
    assert.equal(PSL.readTraceHandoff(null), null);
});

// -------------------------------------------------- the source a handoff opens on

const handoffSource = (id, kind, environment, extra) => Object.assign({id: id, kind: kind, environment: environment}, extra);
const dProd = handoffSource("d-prod", "daemon", "production");
const dStaging = handoffSource("d-staging", "daemon", "staging");
const tProd = handoffSource("t-prod", "tempo", "production");
const tProd2 = handoffSource("t-prod-2", "jaeger_query", "production");
const tStaging = handoffSource("t-staging", "tempo", "staging");

test("a handed-over window leaves a daemon for the trace backend of the environment it came from", () => {
    const sources = [dProd, dStaging, tStaging, tProd];
    assert.equal(PSL.handoffSourceId(sources, "d-prod", "d-prod"), "t-prod");
    // A staging backend that happened to be selected is not where a production window belongs.
    assert.equal(PSL.handoffSourceId(sources, "t-staging", "d-prod"), "t-prod");
});

test("a selection that is already a backend of that environment stays", () => {
    assert.equal(PSL.handoffSourceId([dProd, tProd, tProd2], "t-prod-2", "d-prod"), "t-prod-2");
    assert.equal(PSL.handoffSourceId([dProd, tProd, tProd2], "t-prod", "d-prod"), "t-prod");
    // Not the one selected, so the first of the environment, in the list's order.
    assert.equal(PSL.handoffSourceId([dProd, tProd2, tProd], "d-prod", "d-prod"), "t-prod-2");
});

test("an unknown origin keeps a backend selection and leaves a daemon for the first backend", () => {
    const sources = [dProd, tStaging, tProd];
    assert.equal(PSL.handoffSourceId(sources, "t-prod", null), "t-prod");
    assert.equal(PSL.handoffSourceId(sources, "d-prod", null), "t-staging");
    // An id the list does not hold is no more known than none.
    assert.equal(PSL.handoffSourceId(sources, "t-prod", "gone"), "t-prod");
    assert.equal(PSL.handoffSourceId(sources, null, "gone"), "t-staging", "nothing selected yet");
});

test("an environment with no backend falls back to the selection, then to the first backend", () => {
    const sources = [dProd, dStaging, tStaging];
    assert.equal(PSL.handoffSourceId(sources, "t-staging", "d-prod"), "t-staging");
    assert.equal(PSL.handoffSourceId(sources, "d-prod", "d-prod"), "t-staging");
});

test("with no backend there is nothing to pick, and the selection stays", () => {
    assert.equal(PSL.handoffSourceId([dProd, dStaging], "d-prod", "d-prod"), null);
    assert.equal(PSL.handoffSourceId([], "d-prod", "d-prod"), null);
});

test("a source the caller may not run an analysis on is never the pick", () => {
    const readOnly = handoffSource("t-ro", "tempo", "production", {permissions: ["findings.read"]});
    const allowed = handoffSource("t-ok", "tempo", "production", {permissions: ["analyses.run"]});
    assert.equal(PSL.handoffSourceId([dProd, readOnly], "d-prod", "d-prod"), null);
    assert.equal(PSL.handoffSourceId([dProd, readOnly, allowed], "d-prod", "d-prod"), "t-ok");
    // Nor is it kept for being selected.
    assert.equal(PSL.handoffSourceId([readOnly, allowed], "t-ro", null), "t-ok");
    // The origin is still known from a daemon the caller cannot run on.
    const quietDaemon = handoffSource("d-prod", "daemon", "production", {permissions: ["findings.read"]});
    assert.equal(PSL.handoffSourceId([quietDaemon, tStaging, tProd], "t-staging", "d-prod"), "t-prod");
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
    const none = {period: "week", at: null, environment: null, services: [], groupings: [], view: "tech"};
    assert.deepEqual(PSL.readRecapRoute("#/recap"), none);
    assert.deepEqual(PSL.readRecapRoute("#/recap?period=month&at=2026-10-29&environment=prod&service=a%2Fb&grouping=pay&view=pilotage"), {
        period: "month", at: "2026-10-29", environment: "prod", services: ["a/b"], groupings: ["pay"], view: "pilotage"
    });
    // An unknown value reads as its default, a blank filter as none.
    assert.deepEqual(PSL.readRecapRoute("#/recap?period=year&at=yesterday&view=ceo&service=%20"), none);
    assert.deepEqual(PSL.readRecapRoute("#/recap?service=a%0Ab").services, [], "control character");
    assert.deepEqual(PSL.readRecapRoute("#/recap?service=" + "a".repeat(257)).services, [], "too long");
    assert.deepEqual(PSL.readRecapRoute("#/recap?service=" + "a".repeat(256)).services, ["a".repeat(256)], "just short enough");
    assert.equal(PSL.readRecapRoute("#/new").period, "week", "another route reads as the defaults");
    // Well formed but no day of the calendar: the Hub refuses it, and no period can be worked out from it.
    assert.equal(PSL.readRecapRoute("#/recap?at=2026-13-45").at, null);
    assert.equal(PSL.readRecapRoute("#/recap?at=2026-02-31").at, null);
    assert.equal(PSL.readRecapRoute("#/recap?at=2026-02-28").at, "2026-02-28");
    assert.doesNotThrow(() => PSL.recapFiltersPath(PSL.readRecapRoute("#/recap?at=2026-13-45")));
    assert.doesNotThrow(() => PSL.recapJobKey(PSL.readRecapRoute("#/recap?at=2026-13-45")));
});

test("an old recap link with one service and one namespace still reads as one value each", () => {
    const route = PSL.readRecapRoute("#/recap?service=a&grouping=b");
    assert.deepEqual(route.services, ["a"]);
    assert.deepEqual(route.groupings, ["b"]);
    assert.equal(PSL.recapPath(route), "/api/recap?period=week&view=tech&service=a&grouping=b");
});

test("a repeated key reads every value, each once, in the order the Hub keys a job on", () => {
    assert.deepEqual(PSL.readRecapRoute("#/recap?service=a&service=b").services, ["a", "b"]);
    assert.deepEqual(PSL.readRecapRoute("#/recap?grouping=y&grouping=x&grouping=y").groupings, ["x", "y"]);
    // One bad value costs only itself, and the others keep their place.
    assert.deepEqual(PSL.readRecapRoute("#/recap?service=a%0Ab&service=ok&service=%20&service=" + "a".repeat(257)).services,
        ["ok"]);
    // The same choices in another order are one job, as the Hub's canonical list makes them.
    const forward = PSL.readRecapRoute("#/recap?service=a&service=b");
    const backward = PSL.readRecapRoute("#/recap?service=b&service=a");
    assert.deepEqual(forward, backward);
    assert.equal(PSL.recapJobKey(forward), PSL.recapJobKey(backward));
    // A name is matched whole by the Hub: a space around it is part of it.
    assert.deepEqual(PSL.readRecapRoute("#/recap?service=%20a%20").services, [" a "]);
});

test("the recap hash carries only what differs from the defaults", () => {
    assert.equal(PSL.recapHash(PSL.readRecapRoute("#/recap")), "#/recap");
    const route = {
        period: "day",
        at: "2026-10-29",
        environment: "pre prod",
        services: ["a/b"],
        groupings: [],
        view: "pilotage"
    };
    assert.equal(PSL.recapHash(route),
        "#/recap?period=day&at=2026-10-29&environment=pre%20prod&service=a%2Fb&view=pilotage");
    assert.deepEqual(PSL.readRecapRoute(PSL.recapHash(route)), route);
    assert.equal(PSL.recapHash({
            period: "week",
            at: null,
            environment: null,
            services: [],
            groupings: [],
            view: "tech"
        }),
        "#/recap");
    assert.equal(PSL.recapHash(PSL.readRecapRoute("#/recap?grouping=x&service=b&service=a")),
        "#/recap?service=a&service=b&grouping=x");
});

test("a name with an ampersand, a comma or an accent survives the recap hash", () => {
    const names = ["R&D", "a,b", "caf\u00e9-\u00c9quipe", "x=y+z%", "100 % #1", "o'reilly \"q\"", "\u65e5\u672c"];
    const route = {period: "week", at: null, environment: null, services: names.slice().sort(), groupings: ["a&b,c"], view: "tech"};
    const hash = PSL.recapHash(route);
    assert.deepEqual(PSL.readRecapRoute(hash), route);
    // Each is one value of the key, none split on the characters a query uses.
    assert.equal(new URLSearchParams(hash.slice("#/recap?".length)).getAll("service").length, names.length);
    // The browser's address bar may re-encode the fragment, and the name must still read back whole.
    const url = new URL("http://hub.test/" + hash);
    assert.deepEqual(PSL.readRecapRoute(url.hash), route);
});

test("a long name breaks at its separators and word boundaries, never inside a parameter", () => {
    assert.deepStrictEqual(PSL.breakParts("/api/entreprises/{entrepriseId}/documents"),
        ["/", "api/", "entreprises/", "{entrepriseId}/", "documents"]);
    assert.deepStrictEqual(PSL.breakParts("repo.ClientRepository.findByIdIsNotNull"),
        ["repo.", "Client", "Repository.", "find", "By", "Id", "Is", "Not", "Null"]);
    assert.deepStrictEqual(PSL.breakParts("lettres-missions"), ["lettres-", "missions"]);
    assert.deepStrictEqual(PSL.breakParts("{a.b}/c"), ["{a.b}/", "c"]);
    assert.deepStrictEqual(PSL.breakParts("x"), ["x"]);
    // The pieces put back together are the name as sent: a copy reads it whole.
    const name = "fr.nexcer.devis.ClientRepository.findByDossierIdAndClientOriginalIdIsNotNull";
    assert.equal(PSL.breakParts(name).join(""), name);
});

test("the recap path always names its period and view", () => {
    assert.equal(PSL.recapPath(PSL.readRecapRoute("#/recap")), "/api/recap?period=week&view=tech");
    assert.equal(PSL.recapPath({
            period: "day",
            at: "2026-10-29",
            environment: "prod",
            services: ["a b"],
            groupings: ["g"],
            view: "pilotage"
        }),
        "/api/recap?period=day&view=pilotage&at=2026-10-29&environment=prod&service=a%20b&grouping=g");
    // Several names repeat the key, which is how the Hub reads a list.
    assert.equal(PSL.recapPath(PSL.readRecapRoute("#/recap?service=a%26b&service=c%2Cd&grouping=x&grouping=y")),
        "/api/recap?period=week&view=tech&service=a%26b&service=c%2Cd&grouping=x&grouping=y");
});

test("a recap job is keyed on everything but the view and started with the same filters", () => {
    const route = {period: "day", at: "2026-10-29", environment: "prod", services: ["a b"], groupings: [], view: "tech"};
    assert.equal(PSL.recapJobKey(route), PSL.recapJobKey(Object.assign({}, route, {view: "pilotage"})));
    assert.notEqual(PSL.recapJobKey(route), PSL.recapJobKey(Object.assign({}, route, {services: ["c"]})));
    assert.notEqual(PSL.recapJobKey(route), PSL.recapJobKey(Object.assign({}, route, {services: ["a b", "c"]})));
    // No day named reads as today in UTC, as the Hub does: past midnight UTC it is another day's job.
    const today = PSL.readRecapRoute("#/recap?period=day");
    const beforeMidnight = Date.UTC(2026, 10, 3, 23, 59);
    assert.equal(PSL.recapJobKey(today, beforeMidnight),
        PSL.recapJobKey(Object.assign({}, today, {at: "2026-11-03"}), beforeMidnight));
    assert.notEqual(PSL.recapJobKey(today, beforeMidnight), PSL.recapJobKey(today, beforeMidnight + 120_000));
    // The Hub keys a job on its period's first day: two days of one week or month read the same job.
    const week = PSL.readRecapRoute("#/recap");
    assert.equal(PSL.recapJobKey(week, beforeMidnight), PSL.recapJobKey(week, beforeMidnight + 120_000));
    assert.equal(PSL.recapJobKey(Object.assign({}, week, {at: "2026-10-27"})),
        PSL.recapJobKey(Object.assign({}, week, {at: "2026-11-01"})));
    assert.notEqual(PSL.recapJobKey(Object.assign({}, week, {at: "2026-11-01"})),
        PSL.recapJobKey(Object.assign({}, week, {at: "2026-11-02"})));
    const month = PSL.readRecapRoute("#/recap?period=month&at=2026-10-03");
    assert.equal(PSL.recapJobKey(month), PSL.recapJobKey(Object.assign({}, month, {at: "2026-10-31"})));
    assert.equal(PSL.recapJobPath(route, false),
        "/api/recap/jobs?period=day&at=2026-10-29&environment=prod&service=a%20b");
    // A job takes every name as a repeated key, none squeezed into one value.
    assert.equal(PSL.recapJobPath(Object.assign({}, route, {services: ["a", "b&c"], groupings: ["x", "y"]}), true),
        "/api/recap/jobs?period=day&at=2026-10-29&environment=prod&service=a&service=b%26c&grouping=x&grouping=y&refresh=true");
    assert.equal(PSL.recapJobPath(PSL.readRecapRoute("#/recap"), true), "/api/recap/jobs?period=week&refresh=true");
    assert.equal(PSL.recapJobResultPath("0123abcd", "pilotage", false),
        "/api/recap/jobs/0123abcd/result?view=pilotage");
    assert.equal(PSL.recapJobResultPath("0123abcd", "tech", true),
        "/api/recap/jobs/0123abcd/result?view=tech&moves=all");
});

test("the names a list offers are read for the period and the environment, never for the names chosen", () => {
    const route = PSL.readRecapRoute("#/recap?period=month&at=2026-10-17&environment=pre%20prod&service=a&grouping=b&view=pilotage");
    assert.equal(PSL.recapFiltersPath(route), "/api/recap/filters?period=month&at=2026-10-01&environment=pre%20prod");
    assert.equal(PSL.recapFiltersPath(PSL.readRecapRoute("#/recap?period=day&at=2026-10-17")),
        "/api/recap/filters?period=day&at=2026-10-17");
    // No day named is today in UTC, and the path names the period's first day, so it holds for the whole period.
    const week = PSL.readRecapRoute("#/recap?service=a");
    const wednesday = Date.UTC(2026, 10, 4, 10);
    assert.equal(PSL.recapFiltersPath(week, wednesday), "/api/recap/filters?period=week&at=2026-11-02");
    assert.equal(PSL.recapFiltersPath(week, wednesday + 86_400_000), PSL.recapFiltersPath(week, wednesday));
    assert.notEqual(PSL.recapFiltersPath(week, wednesday + 5 * 86_400_000), PSL.recapFiltersPath(week, wednesday));
    // Fifty long names would push the request line past what the server accepts: none goes in.
    const many = {...week, services: Array.from({length: 50}, (_, index) => String(index).padEnd(256, "x"))};
    assert.equal(PSL.recapFiltersPath(many, wednesday), PSL.recapFiltersPath(week, wednesday));
});

test("a checkbox list holds the names offered, then the chosen ones the period lacks, and a search keeps some", () => {
    const row = (name, checked, absent) => ({name, checked, absent});
    assert.deepEqual(PSL.recapPickRows(["a", "b", "c"], ["b", "z"], ""),
        [row("a", false, false), row("b", true, false), row("c", false, false), row("z", true, true)]);
    assert.deepEqual(PSL.recapPickRows([], [], ""), []);
    // The Hub's order is kept, not sorted again.
    assert.deepEqual(PSL.recapPickRows(["b", "a"], [], "").map((entry) => entry.name), ["b", "a"]);
    // A name chosen and offered is one row.
    assert.equal(PSL.recapPickRows(["a"], ["a"], "").length, 1);
});

test("a search in a checkbox list is a plain, case-blind part of the name", () => {
    const names = (query, offered, ticked) => PSL.recapPickRows(offered, ticked || [], query).map((entry) => entry.name);
    assert.deepEqual(names("WEB", ["web-shop", "api", "Webhook"]), ["web-shop", "Webhook"]);
    assert.deepEqual(names("  api ", ["web-shop", "api"]), ["api"]);
    // Not a pattern: a dot or a star is itself.
    assert.deepEqual(names(".", ["a.b", "ab"]), ["a.b"]);
    assert.deepEqual(names("a*", ["a*b", "aab"]), ["a*b"]);
    assert.deepEqual(names("\u00e9quipe", ["\u00c9quipe-A", "team"]), ["\u00c9quipe-A"]);
    // A chosen name the period lacks is searched like the others, and nothing is left when nothing matches.
    assert.deepEqual(names("old", ["new"], ["old-name"]), ["old-name"]);
    assert.deepEqual(names("zzz", ["new"], ["old-name"]), []);
});

test("a recap that the Hub could not read for its filters says so, whether it refused or the request was too long", () => {
    // Fifty long names make a request line the server refuses before any route reads it.
    [400, 414, 431].forEach((status) => assert.equal(PSL.recapErrorText(status, "tech"), PSL.recapErrorText(400, "tech")));
    assert.match(PSL.recapErrorText(414, "tech"), /filters/);
    assert.match(PSL.recapErrorText(500, "tech"), /not any daemon/);
    assert.equal(PSL.RECAP_FILTER_MAX_VALUES, 50, "the Hub's cap on the names of one filter");
});

test("what a frozen report says it freezes names the filters whichever way they were set", () => {
    const several = PSL.readRecapRoute("#/recap?service=a&service=b");
    assert.match(PSL.recapFreezeText(several), /with the filters above/);
    assert.match(PSL.recapFreezeText(PSL.readRecapRoute("#/recap?grouping=g")), /with the filters above/);
    assert.doesNotMatch(PSL.recapFreezeText(PSL.readRecapRoute("#/recap")), /filters/);
});

test("a screen id is 32 hex characters, drawn anew each time", () => {
    const one = PSL.screenId();
    assert.match(one, /^[0-9a-f]{32}$/);
    assert.notEqual(one, PSL.screenId());
});

test("a recap job reads 100 % only once it succeeded", () => {
    assert.equal(PSL.recapJobPercent({status: "running", progress: 0}), 0);
    assert.equal(PSL.recapJobPercent({status: "running", progress: 0.4211}), 42);
    assert.equal(PSL.recapJobPercent({status: "running", progress: 1}), 99);
    assert.equal(PSL.recapJobPercent({status: "running", progress: "x"}), 0);
    assert.equal(PSL.recapJobPercent({status: "succeeded", progress: 1}), 100);
});

test("a recap's freshness says the time alone on the same day and the day too otherwise", () => {
    const now = new Date(2026, 10, 4, 14, 40).getTime();
    const at = new Date(2026, 10, 4, 14, 32).getTime();
    assert.deepEqual(PSL.recapFreshness({finished_at_ms: at, expires_at_ms: at + 600_000}, now),
        {at: "14:32", until: "14:42"});
    const tomorrow = PSL.recapFreshness({finished_at_ms: at, expires_at_ms: at + 86_400_000}, now);
    assert.equal(tomorrow.at, "14:32");
    assert.equal(tomorrow.until, PSL.dateTime(at + 86_400_000));
    assert.equal(PSL.recapFreshness({finished_at_ms: null, expires_at_ms: null}, now), null);
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
        ...[403, 400, 414, 500].map((status) => PSL.recapErrorText(status, "pilotage")),
        ...Object.values(PSL.RECAP_LABELS.pilotage),
        ...Object.values(PSL.RECAP_HELP.pilotage),
        ...Object.values(PSL.RECAP_REPORTS_COPY),
        PSL.recapFreezeText(route),
        PSL.recapFreezeText(PSL.readRecapRoute("#/recap")),
        ...Object.values(PSL.RECAP_PICK_COPY),
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

test("a generate request sends one name as a text and several as a list, each filter on its own", () => {
    const several = PSL.recapOnDemandBody(PSL.readRecapRoute("#/recap?service=b&service=a&grouping=x"));
    assert.deepEqual(several, {period: "week", environment: null, services: ["a", "b"], grouping: "x"});
    // Never both forms of one filter: the Hub refuses that with a 400.
    assert.ok(!("service" in several));
    assert.ok(!("groupings" in several));
    const grouped = PSL.recapOnDemandBody(PSL.readRecapRoute("#/recap?grouping=x&grouping=y"));
    assert.deepEqual(grouped, {period: "week", environment: null, service: null, groupings: ["x", "y"]});
    assert.ok(!("services" in grouped));
    assert.ok(!("grouping" in grouped));
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
    const names = [...source.matchAll(/^ {4}(?:async )?function ([A-Za-z0-9_$]+)\(/gm)].map(m => m[1]);
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
        "On the check page, this card reads: Figures intact since they were signed. Signer not verified.");
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

test("the language button rests on the active language and slides to the other on hover", () => {
    // Same behaviour as perf-sentinel.dev. Track order stays FR then EN: an English page rests
    // on EN (-19px) and slides to FR on hover, a French page rests on FR and slides to EN.
    const css = wwwroot("launcher.css");
    const rule = selector => {
        const m = css.match(new RegExp(selector.replace(/[.[\]"]/g, "\\$&") + "\\s*\\{([^}]*)\\}"));
        return m ? m[1].replace(/\s+/g, " ").trim() : null;
    };
    assert.match(rule(".shell-lang-track") || "", /transform: translateX\(-19px\)/);
    assert.match(rule(':root[lang="fr"] .shell-lang-track') || "", /transform: none/);
    assert.match(rule(".shell-lang:hover .shell-lang-track") || "", /transform: none/);
    assert.match(rule(':root[lang="fr"] .shell-lang:hover .shell-lang-track') || "", /translateX\(-19px\)/);
});

// ---- the SQL of a recap priority, laid out and coloured

const SCREENSHOT_SQL = "select md1_0.id,md1_0.code,md1_0.created_by,md1_0.creation_date,md1_0.dossier_id,"
    + "md1_0.libelle,md1_0.modification_date,md1_0.modified_by,md1_0.trace_id,md1_0.type,md1_0.valeur "
    + "from mykinexo.meta_dossier md1_0 where md1_0.dossier_id=?";
const sqlText = tokens => tokens.map(token => token.text).join("");
const noBlanks = text => text.replace(/\s/g, "");

test("the query of the screenshot reads select, grouped indented columns, from, where", () => {
    const tokens = PSL.sqlTokens(SCREENSHOT_SQL);

    assert.equal(sqlText(tokens), [
        "select",
        "  md1_0.id, md1_0.code, md1_0.created_by, md1_0.creation_date, md1_0.dossier_id, md1_0.libelle,",
        "  md1_0.modification_date, md1_0.modified_by, md1_0.trace_id, md1_0.type, md1_0.valeur",
        "from mykinexo.meta_dossier md1_0",
        "where md1_0.dossier_id = ?"
    ].join("\n"));
    const word = (kind, text) => tokens.find(token => token.kind === kind && token.text.includes(text));
    assert.ok(word("keyword", "select") && word("keyword", "from") && word("keyword", "where"));
    assert.equal(word("keyword", "=").text, "=");
    assert.equal(word("table", "mykinexo.meta_dossier").text, "mykinexo.meta_dossier");
    assert.equal(word("param", "?").text, "?");
    assert.ok(word("column", "md1_0.valeur"));
    assert.ok(tokens.some(token => token.kind === "text" && token.text.includes("\n")),
        "a line break is a text token, never part of a coloured one");
});

test("a clause opens a line, whatever its case", () => {
    for (const clause of ["join", "left join", "left outer join", "where", "group by", "having", "order by", "limit",
        "union"]) {
        for (const written of [clause, clause.toUpperCase()]) {
            const lines = sqlText(PSL.sqlTokens("select a from t " + written + " x")).split("\n");
            assert.ok(lines.includes(written + " x"), written + " should open a line: " + JSON.stringify(lines));
        }
    }
    assert.deepEqual(sqlText(PSL.sqlTokens("SELECT a FROM t")).split("\n"), ["SELECT", "  a", "FROM t"]);
});

test("a clause inside parentheses, a function and a delete stay on their line", () => {
    const sub = sqlText(PSL.sqlTokens("select a from t where b in (select c from u where d=?)"));
    assert.equal(sub, "select\n  a\nfrom t\nwhere b in (select c from u where d = ?)");
    assert.equal(sqlText(PSL.sqlTokens("select left(a, 2), count(*) from t")), "select\n  left(a, 2), count(*)\nfrom t");
    assert.equal(sqlText(PSL.sqlTokens("delete from t where a=?")), "delete from t\nwhere a = ?");
});

test("laying a query out never changes a token", () => {
    const inputs = [
        "select a from t where b = 'select from x' and c=?",
        "select a from t -- from here\nwhere b=1",
        "select a /* where */ from t where b=1 /* order by */ order by a",
        'select "from", "a b" from "where" where "x"=?',
        "select café, 名前, 😀 from täble where naïve='ñ ü' and 日本=?",
        "select a from t where j@>b and c<@d and e<-1 and f->>g=? and h<=>i and k!=l and m<>n and o>=p",
        "select a from t where b='unterminated",
        "select a from t where b=1 /* unterminated",
        "select a from t where b=$1 and c=$2",
        "select a from t where x=$? and y=$?",
        "",
        "   "
    ];
    for (const input of inputs) {
        for (const padded of [input, input + " ".repeat(601)]) {
            assert.equal(noBlanks(sqlText(PSL.sqlTokens(padded))), noBlanks(padded), JSON.stringify(padded.slice(0, 80)));
        }
    }
});

test("a Postgres parameter is one parameter token, as the engine writes it with `$?` or as `$1`", () => {
    const params = sql => PSL.sqlTokens(sql).filter(token => token.kind === "param").map(token => token.text);

    assert.deepEqual(params("select a from t where x=$? and y=$?"), ["$?", "$?"]);
    assert.deepEqual(params("select a from t where x=$1 and y=$12"), ["$1", "$12"]);
    assert.deepEqual(params("select a from t where x=?"), ["?"]);
});

test("the inside of a string, a comment and a quoted name keeps its blanks and its operators", () => {
    const text = sqlText(PSL.sqlTokens("select a from t where b='x=y  from z' and \"c=d  e\"=? -- g=h  from\n"));
    assert.ok(text.includes("'x=y  from z'"));
    assert.ok(text.includes('"c=d  e" = ?'));
    assert.ok(text.includes("-- g=h  from"));
    assert.ok(!/\nfrom z/.test(text));
});

test("an operator that is part of a longer one is not split by its blanks", () => {
    const text = sqlText(PSL.sqlTokens("select a from t where j@>b and c<@d and e<-1 and f->>g=? and x=-1"));
    for (const whole of ["@>", "<@", "<-", "->>", "=-"]) assert.ok(text.includes(whole), whole + " in " + text);
});

test("past 600 characters the layout stops and only the colours stay", () => {
    const query = length => "select " + "c".repeat(length - "select  from t where x=?".length) + " from t where x=?";
    const long = query(601);
    assert.equal(long.length, 601);
    const tokens = PSL.sqlTokens(long);

    assert.equal(sqlText(tokens), long, "not one blank changes, not even around the =");
    assert.ok(!sqlText(tokens).includes("\n"));
    assert.deepEqual([...new Set(tokens.map(token => token.kind))].sort(),
        ["column", "keyword", "param", "table", "text"]);

    const exactly = query(600);
    assert.equal(exactly.length, 600);
    assert.ok(sqlText(PSL.sqlTokens(exactly)).includes("\nfrom t\nwhere x = ?"), "600 is still laid out");
    assert.equal(PSL.RECAP_SQL_LAYOUT_MAX, 600);
});

test("only a finding on SQL gets the coloured block", () => {
    assert.equal(PSL.isSqlFinding("redundant_sql"), true);
    assert.equal(PSL.isSqlFinding("n_plus_one_sql"), true);
    assert.equal(PSL.isSqlFinding("slow_sql"), true);
    assert.equal(PSL.isSqlFinding("redundant_http"), false);
    assert.equal(PSL.isSqlFinding("slow_messaging"), false);
    assert.equal(PSL.isSqlFinding(undefined), false);
    assert.match(wwwroot("app.js"), /PSL\.isSqlFinding\(priority\.finding_type\)/);
});

test("the SQL colours stay readable on the inset, in both themes", () => {
    // WCAG 2.x contrast. The inset is --inset-bg laid over the card ground, --surface.
    const tokens = wwwroot("tokens.css");
    const rgba = value => {
        const hex = value.match(/^#([0-9a-f]{6})$/i);
        if (hex) return [0, 2, 4].map(at => parseInt(hex[1].substr(at, 2), 16)).concat(1);
        const parts = value.match(/^rgba\((\d+),\s*(\d+),\s*(\d+),\s*([\d.]+)\)$/);
        return [1, 2, 3].map(at => Number(parts[at])).concat(Number(parts[4]));
    };
    const luminance = rgb => {
        const [r, g, b] = rgb.slice(0, 3).map(channel => {
            const c = channel / 255;
            return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
        });
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    };
    const ratio = (a, b) => {
        const [high, low] = [luminance(a), luminance(b)].sort((x, y) => y - x);
        return (high + 0.05) / (low + 0.05);
    };
    const css = wwwroot("launcher.css");
    const used = {keyword: "brand-text", table: "info-fg", param: "warn-fg", column: "text-2"};
    for (const [kind, name] of Object.entries(used)) {
        assert.match(css, new RegExp("\\.sql-" + kind + " \\{[^}]*color: var\\(--" + name + "\\);"));
    }
    for (const theme of ["dark", "light"]) {
        const block = tokens.match(new RegExp(':root\\[data-theme="' + theme + '"\\] \\{([^}]*)\\}'))[1];
        const token = name => rgba(block.match(new RegExp("--" + name + ":\\s*([^;]+);"))[1].trim());
        const [ground, inset] = [token("surface"), token("inset-bg")];
        const floor = ground.slice(0, 3).map((channel, at) => channel * (1 - inset[3]) + inset[at] * inset[3]);
        for (const name of ["text", ...Object.values(used)]) {
            assert.ok(ratio(token(name), floor) >= 4.5, theme + " --" + name + " on the inset: " + ratio(token(name), floor));
        }
    }
});

// ---- the pilotage guide's steps, worked out from the snapshot the screen has loaded

/** Runs `body` in `lang`, then puts English back, so no test leaks its language. */
function inLang(lang, body) {
    PSL.setLang(lang);
    try {
        body();
    } finally {
        PSL.setLang("en");
    }
}

/** Overlays `over` on `base`, object by object, so a test names only the field it changes. */
function overlay(base, over) {
    const out = {...base};
    for (const [key, value] of Object.entries(over || {})) {
        const plain = value && typeof value === "object" && !Array.isArray(value);
        out[key] = plain && base[key] && typeof base[key] === "object" ? overlay(base[key], value) : value;
    }
    return out;
}

const pilotagePeriod = (from, to) => ({
    kind: "week", from, to, in_progress: false, incomplete: [], notes: [], sampling_rate: null, truncated_to_days: null
});

const pilotageRow = (over) => Object.assign({
    rank: 1, finding_type: "n_plus_one_sql", severity: "warning", service: "shop-svc", plain: "Loads each order line apart.",
    detections: 12, avoidable_io_ops: 1200, co2_grams: null, co2_partial: false, volume_known: true, trend: "up",
    change: 0.2, label: null, service_label: null, action: null
}, over);

function pilotageSnapshot(over) {
    return overlay({
        view: "pilotage",
        period: pilotagePeriod("2026-10-26", "2026-11-01"),
        previous: pilotagePeriod("2026-10-19", "2026-10-25"),
        filters: {environment: null, source_id: null, service: null, grouping: null, service_label: null},
        comparison_exact: true,
        tiles: {
            avoidable_io_ops: 168900, previous_avoidable_io_ops: 150000, co2_grams: 2000, previous_co2_grams: null,
            co2_sources_without_coefficient: 0, co2_missing_reason: null, active_findings: 4, previous_active_findings: 5,
            acked_findings: 0, actions: {todo: 1, in_progress: 0, done: 2, confirmed: 1, contradicted: 0, came_back: 0}
        },
        priorities: [pilotageRow(), pilotageRow({rank: 2, avoidable_io_ops: 300})],
        more_priorities: 0,
        movements: {new: [], gone: [], gone_with_confirmed_action: 0, up: [], down: [], came_back: [], done_still_detected: []},
        nothing_moved: false,
        sources_without_volume: [], truncated: false, late_sources: null
    }, over);
}

/** The steps of a snapshot, by id. */
function steps(over) {
    return Object.fromEntries(PSL.recapPilotageSteps(pilotageSnapshot(over)).map((step) => [step.id, step]));
}

/** Every title, body paragraph and mark of every step, for a set of snapshots that cover the variants. */
function allVariantsText() {
    const variants = [
        {},
        {period: {in_progress: true}},
        {period: {incomplete: ["restart"]}, previous: {incomplete: ["sampling"]}},
        {filters: {environment: "production", services: ["a", "b"], groupings: ["x", "y"]}},
        {tiles: {avoidable_io_ops: null, co2_grams: null, co2_missing_reason: "no_coefficient"}},
        {tiles: {avoidable_io_ops: 1, co2_grams: null, co2_missing_reason: "no_avoidable_io", active_findings: 1,
            actions: {done: 1}}},
        {tiles: {co2_grams: 1200, co2_sources_without_coefficient: 2}},
        {priorities: []},
        {priorities: [pilotageRow({avoidable_io_ops: null, action: {status: "done"}})]},
        {movements: {gone_with_confirmed_action: 2}},
        {nothing_moved: true},
        {previous: {truncated_to_days: 0}}
    ];
    return variants.flatMap((over) => PSL.recapPilotageSteps(pilotageSnapshot(over)))
        .flatMap((step) => [step.title, ...step.body, ...(step.marks || []).map((mark) => mark.text)]).join("\n");
}

/** A phrase of each variant sentence, to tell which ones a step carries. */
const SAYS = {
    fr: {
        notOver: "pas finie", closedDays: "déjà terminés", inexact: "pas exacte", wholePeriod: "toute la période",
        filtered: "que les filtres retiennent", cite: "ne la citez pas", atLeast: "Au moins", noCoefficient: "de quoi estimer",
        noIo: "Aucune opération inutile", nothingCounted: "Rien n'a été compté", confirmed: "correctif est confirmé",
        nothingMoved: "Rien n'a bougé", noClosedDay: "Aucun jour de cette période", nobody: "personne n'a encore pris",
        nothingToFix: "Rien à corriger", dash: "Un tiret", ioNone: "Opérations inutiles", ioOne: "1 opération inutile",
        ioOther: " opérations inutiles"
    },
    en: {
        notOver: "not over", closedDays: "already finished", inexact: "not exact", wholePeriod: "whole period",
        filtered: "only what the filters keep", cite: "do not quote", atLeast: "At least", noCoefficient: "basis to estimate",
        noIo: "No unnecessary operations", nothingCounted: "Nothing was counted", confirmed: "fix is confirmed",
        nothingMoved: "Nothing has moved", noClosedDay: "No day of this period", nobody: "nobody has taken",
        nothingToFix: "Nothing to fix", dash: "A dash", ioNone: "Unnecessary operations", ioOne: "1 unnecessary operation",
        ioOther: " unnecessary operations"
    }
};

/** Each guide test runs in both languages, with the phrases that language says. */
function guideTest(name, body, says = SAYS) {
    for (const lang of ["fr", "en"]) {
        test(name + " (" + lang + ")", () => {
            inLang(lang, () => body(says[lang]));
            // The run leaves no language behind it, so the next test starts in English as it expects.
            assert.equal(PSL.lang(), "en");
        });
    }
}

test("the recap guide follows the view on screen", () => {
    assert.equal(PSL.guideIdFor("recap", "pilotage"), "recap.pilotage");
    assert.equal(PSL.guideIdFor("recap", "tech"), "recap.tech");
    assert.equal(PSL.guideIdFor("recap", null), null);
    assert.equal(PSL.guideIdFor("ack", null), null);
});

test("the recap count is the tile's: separated thousands, nothing without a value", () => {
    assert.equal(PSL.recapCount(1234), PSL.num(1234));
    assert.equal(PSL.recapCount(0), "0");
    assert.equal(PSL.recapCount(null), null);
    assert.equal(PSL.recapCount(undefined), null);
});

test("the steps come in the screen's order, each on its anchors", () => {
    const list = PSL.recapPilotageSteps(pilotageSnapshot());
    assert.deepEqual(list.map((step) => [step.id, step.anchors]), [
        ["view", ["recap.view"]], ["period", ["recap.period"]], ["io", ["recap.io"]], ["co2", ["recap.co2"]],
        ["open", ["recap.findings", "recap.actions"]], ["table", ["recap.table"]], ["row", ["recap.row"]],
        ["below", ["recap.below"]], ["moved", ["recap.moved"]], ["reports", ["recap.reports"]],
        ["card", ["recap.card"]], ["end", []]]);
    assert.deepEqual(list.find((step) => step.id === "row").marks.map((mark) => mark.anchor),
        ["recap.row.rank", "recap.row.what", "recap.row.where", "recap.row.figure", "recap.row.trend", "recap.row.state"]);
    assert.ok(list.every((step) => step.title && Array.isArray(step.body)));
    assert.ok(list.filter((step) => step.id !== "row").every((step) => step.body.length && !step.marks));
});

guideTest("a closed period says nothing of days still to finish", (says) => {
    const body = steps({period: {in_progress: false, incomplete: []}}).period.body.join(" ");
    assert.ok(!body.includes(says.notOver));
    assert.ok(body.includes(PSL.periodText(pilotageSnapshot().period)), "the period reads as the screen writes it");
});

guideTest("a period in progress says its comparisons use the days already finished", (says) => {
    const body = steps({period: {in_progress: true}}).period.body.join(" ");
    assert.ok(body.includes(says.notOver));
    assert.ok(body.includes(says.closedDays));
    assert.ok(!body.includes(says.inexact));
    assert.ok(body.includes(PSL.periodText({...pilotageSnapshot().period, in_progress: true})));
});

guideTest("an incomplete period drops the closed-days claim and warns", (says) => {
    const inProgress = steps({period: {in_progress: true, incomplete: ["restart"]}});
    const body = inProgress.period.body.join(" ");
    assert.ok(!body.includes(says.closedDays));
    assert.ok(!body.includes(says.notOver));
    assert.ok(body.includes(says.inexact));
    assert.ok(inProgress.io.body.join(" ").includes(says.wholePeriod));
});

guideTest("an incomplete previous period alone also warns", (says) => {
    const found = steps({period: {in_progress: true}, previous: {incomplete: ["sampling"]}});
    const body = found.period.body.join(" ");
    assert.ok(body.includes(says.inexact));
    assert.ok(!body.includes(says.closedDays));
    // The figure itself is the current period's: only its own gaps make it partial.
    assert.ok(!found.io.body.join(" ").includes(says.wholePeriod));
});

guideTest("an active filter is said, none is not", (says) => {
    assert.ok(steps({filters: {services: ["a", "b"]}}).period.body.join(" ").includes(says.filtered));
    for (const filter of [{environment: "production"}, {source_id: "daemon-a"}, {service: "a"}, {grouping: "g"},
        {groupings: ["g", "h"]}]) {
        assert.ok(steps({filters: filter}).period.body.join(" ").includes(says.filtered), JSON.stringify(filter));
    }
    // A service's display label is not a filter of its own, and an empty list names nothing.
    assert.ok(!steps({filters: {service_label: "Shop", services: [], groupings: []}}).period.body.join(" ")
        .includes(says.filtered));
    assert.ok(!steps({}).period.body.join(" ").includes(says.filtered));
});

guideTest("the title carries the figure the tile shows", (says) => {
    assert.ok(steps({tiles: {avoidable_io_ops: 1234}}).io.title.includes(PSL.recapCount(1234)));
    assert.equal(steps({tiles: {avoidable_io_ops: 1}}).io.title, says.ioOne);
    assert.ok(steps({tiles: {avoidable_io_ops: 2}}).io.title.endsWith(says.ioOther));
});

guideTest("no figure of unnecessary operations is a title without a count, and a body that says nothing was counted", (says) => {
    const none = steps({tiles: {avoidable_io_ops: null}}).io;
    const counted = steps({tiles: {avoidable_io_ops: 5}}).io;
    assert.equal(none.title, says.ioNone);
    assert.deepEqual(none.body.length, 1);
    assert.ok(none.body[0].includes(says.nothingCounted));
    // The body is replaced, not added to: the definition is for a tile that has a figure.
    assert.ok(!none.body.join(" ").includes(counted.body[0]));
    assert.ok(!counted.body.join(" ").includes(says.nothingCounted));
    // The period's own gaps still come on top.
    const partial = steps({tiles: {avoidable_io_ops: null}, period: {incomplete: ["restart"]}}).io.body;
    assert.equal(partial.length, 2);
    assert.ok(partial[0].includes(says.nothingCounted) && partial[1].includes(says.wholePeriod));
});

guideTest("a known carbon figure is an order of magnitude, at least when sources send no coefficient", (says) => {
    assert.ok(steps({}).co2.body.join(" ").includes(says.cite));
    assert.ok(!steps({}).co2.body.join(" ").includes(says.atLeast));
    const partial = steps({tiles: {co2_grams: 1200, co2_sources_without_coefficient: 2}}).co2.body.join(" ");
    assert.ok(partial.includes(says.cite));
    assert.ok(partial.includes(says.atLeast));
    // Under a gram the tile reads "< 1 g" with no "at least": the guide does not point at a word it does not show.
    assert.ok(!steps({tiles: {co2_grams: 0.4, co2_sources_without_coefficient: 2}}).co2.body.join(" ")
        .includes(says.atLeast));
});

guideTest("no carbon figure never tells the reader not to cite one", (says) => {
    const missing = steps({tiles: {co2_grams: null, co2_missing_reason: "no_coefficient"}}).co2.body.join(" ");
    assert.ok(!missing.includes(says.cite));
    assert.ok(!missing.includes(says.atLeast));
    assert.ok(missing.includes(says.noCoefficient));
    const idle = steps({tiles: {co2_grams: null, co2_missing_reason: "no_avoidable_io"}}).co2.body.join(" ");
    assert.ok(!idle.includes(says.cite));
    assert.ok(idle.includes(says.noIo));
    assert.ok(!idle.includes(says.noCoefficient));
    // A missing reason the Hub did not give reads as the general case.
    assert.ok(steps({tiles: {co2_grams: null, co2_missing_reason: null}}).co2.body.join(" ").includes(says.noCoefficient));
});

guideTest("problems and actions are counted as the tiles count them, singular or plural", () => {
    const one = steps({tiles: {active_findings: 1, actions: {done: 1}}}).open.title;
    const many = steps({tiles: {active_findings: 12, actions: {done: 3}}}).open.title;
    assert.ok(one.startsWith("1 ") && one.includes(", 1 "), one);
    assert.ok(many.startsWith("12 ") && many.includes(", 3 "), many);
    assert.notEqual(one.replace(/\d+/g, "n"), many.replace(/\d+/g, "n"));
});

guideTest("no priority drops the row and rewrites the table", (says) => {
    const empty = PSL.recapPilotageSteps(pilotageSnapshot({priorities: []}));
    assert.ok(!empty.some((step) => step.id === "row"));
    const table = empty.find((step) => step.id === "table");
    assert.ok(table.title.startsWith(says.nothingToFix));
    assert.deepEqual(table.anchors, ["recap.table"]);
    assert.notEqual(table.title, steps({}).table.title);
    assert.notEqual(table.body.join(" "), steps({}).table.body.join(" "));
    assert.ok(PSL.recapPilotageSteps(pilotageSnapshot()).some((step) => step.id === "row"));
});

guideTest("a first row without volume reads its dash", (says) => {
    const figure = (over) => steps({priorities: [pilotageRow(over)]}).row.marks[3].text;
    assert.ok(figure({avoidable_io_ops: null}).startsWith(says.dash));
    assert.ok(figure({avoidable_io_ops: 0}).startsWith(says.dash), "a zero is no volume to the screen either");
    assert.ok(!figure({avoidable_io_ops: 40}).startsWith(says.dash));
});

guideTest("a taken action does not say nobody took it", (says) => {
    const state = (action) => steps({priorities: [pilotageRow({action})]}).row.marks[5].text;
    assert.ok(state(null).includes(says.nobody));
    for (const status of ["todo", "in_progress", "done"]) {
        assert.ok(!state({status}).includes(says.nobody), status);
    }
    assert.notEqual(state({status: "todo"}), state(null));
});

guideTest("confirmed gone rows are told apart, none is not", (says) => {
    assert.ok(steps({movements: {gone_with_confirmed_action: 2}}).moved.body.join(" ").includes(says.confirmed));
    assert.ok(!steps({movements: {gone_with_confirmed_action: 0}}).moved.body.join(" ").includes(says.confirmed));
    assert.ok(!steps({}).moved.body.join(" ").includes(says.confirmed));
});

guideTest("nothing moved and no closed day replace the comparison", (says) => {
    const compared = steps({}).moved.body.join(" ");
    const nothing = steps({nothing_moved: true}).moved.body.join(" ");
    const none = steps({nothing_moved: true, previous: {truncated_to_days: 0}}).moved.body.join(" ");
    assert.ok(nothing.includes(says.nothingMoved));
    assert.ok(!nothing.includes(compared));
    assert.ok(none.includes(says.noClosedDay));
    // The screen says the closed days first: nothing moved is true there only because nothing was compared.
    assert.ok(!none.includes(says.nothingMoved));
    assert.ok(!compared.includes(says.nothingMoved) && !compared.includes(says.noClosedDay));
    // A period with closed days that cut to none is the only no-closed-day case: 3 closed days compare as usual.
    assert.equal(steps({previous: {truncated_to_days: 3}}).moved.body.join(" "), compared);
});

test("the guide's shell texts are set in both languages", () => {
    const keys = ["open", "quit", "prev", "next", "close", "count", "wait.title", "wait.body", "failed.title", "failed.body"];
    for (const lang of ["fr", "en"]) inLang(lang, () => {
        for (const key of keys) assert.ok(PSL.hasText("guide." + key), lang + " guide." + key);
        assert.equal(PSL.t("guide.count", {at: 2, total: 9}), lang === "fr" ? "2 sur 9" : "2 of 9");
    });
    inLang("fr", () => {
        assert.equal(PSL.t("guide.open"), "Comprendre cet écran");
        assert.equal(PSL.t("guide.wait.title"), "Le récap se calcule");
        assert.equal(PSL.t("guide.failed.title"), "Le récap n'a pas pu être lu");
    });
});

test("pilotage text carries no term of the technical view", () => {
    for (const lang of ["fr", "en"]) inLang(lang, () => {
        const text = allVariantsText();
        assert.ok(text.length > 1500, "the variants were read");
        // Any case: a capitalised "Finding" opening a sentence is the same jargon.
        const lower = text.toLowerCase();
        for (const term of ["finding", "i/o", "span", "n+1"]) assert.ok(!lower.includes(term), lang + " " + term);
        assert.doesNotMatch(text, /—/, "no em dash");
    });
});

test("French pilotage text addresses the reader as vous", () => inLang("fr", () => {
    const text = allVariantsText();
    // \b treats an accented letter as a break ("êtes" would read as "tes"): letters are matched as letters.
    assert.doesNotMatch(text, /(?<!\p{L})(tu|ton|ta|tes|toi)(?!\p{L})/iu);
    assert.match(text, /(?<!\p{L})vous(?!\p{L})/iu);
}));

// ---- the guide of the technical recap

const techRow = (over) => pilotageRow(Object.assign({
    signature: "sig-1", endpoint: "GET /orders/{id}", template: "SELECT * FROM order_line WHERE order_id = ?",
    suggestion: "Batch the order lines.", confidence: "ci"
}, over));

function techSnapshot(over) {
    return pilotageSnapshot(overlay({view: "tech", priorities: [techRow(), techRow({rank: 2, avoidable_io_ops: 300})]},
        over));
}

/** The technical steps of a snapshot, by id, read with `ctx`. */
function techSteps(over, ctx) {
    return Object.fromEntries(PSL.recapTechSteps(techSnapshot(over), ctx || {}).map((step) => [step.id, step]));
}

const text = (step) => [step.title, ...step.body, ...(step.marks || []).map((mark) => mark.text)].join(" ");

const TECH_SAYS = {
    fr: {
        banner: "Le bandeau nomme", closedDays: "jours clos", ranking: "dans le classement", note: "La note sous la période",
        filtered: "que les filtres retiennent", noVolume: "le tiret ne veut pas dire zéro", wholePeriod: "toute la période",
        truncated: "5 000 premiers", ioOne: "1 I/O évitable", ioNone: "I/O évitables", atLeastOne: "1 source n'envoie",
        atLeastMany: "3 sources n'envoient", noIo: "Aucune I/O évitable à valoriser", noCoefficient: "Aucune source n'a envoyé",
        magnitude: "ordre de grandeur", findingOne: "1 finding actif", findingMany: "4 findings actifs",
        actionOne: "1 action terminée", actionMany: "2 actions terminées", actionsNone: "Actions",
        tenFirst: "Les dix premières", showAll: "Tout afficher (12)", emptyTable: "Rien à classer sur cette période",
        label: "nom métier", dash: "Un tiret", sql: "Une requête SQL est mise en page", long: "Au-delà de 600 caractères",
        events: "l'historique de l'action", take: "« Prendre »", ack: "« Acquitter »", taken: "Le responsable",
        verdict: "verdict du moteur : ", contradicted: "la contredit", name: "« Nommer »", denied: "rien n'est écrit",
        noClosed: "Aucun jour de cette période", nothing: "Rien n'a bougé", groups: "Seuls les groupes",
        confirmed: "« Avec un correctif confirmé »", longGroup: "cinq premiers", sources: "sources sans volume"
    },
    en: {
        banner: "The banner names", closedDays: "finished days", ranking: "in the ranking", note: "The note under the period",
        filtered: "only what the filters keep", noVolume: "the dash does not mean zero", wholePeriod: "whole period",
        truncated: "first 5,000", ioOne: "1 avoidable I/O", ioNone: "Avoidable I/O", atLeastOne: "1 source sends",
        atLeastMany: "3 sources send", noIo: "No avoidable I/O to put a figure on", noCoefficient: "No source sent",
        magnitude: "order of magnitude", findingOne: "1 active finding", findingMany: "4 active findings",
        actionOne: "1 action done", actionMany: "2 actions done", actionsNone: "Actions",
        tenFirst: "The first ten", showAll: "Show all 12", emptyTable: "Nothing to rank in this period",
        label: "business name", dash: "A dash", sql: "A SQL query is laid out", long: "Past 600 characters",
        events: "history of the action", take: "\"Take\"", ack: "\"Acknowledge\"", taken: "The owner",
        verdict: "engine's verdict: ", contradicted: "contradicts it", name: "\"Name this\"", denied: "nothing is written",
        noClosed: "No day of this period", nothing: "Nothing has moved", groups: "Only the groups",
        confirmed: "\"With a confirmed fix\"", longGroup: "first five", sources: "sources with no detection volume"
    }
};

/** A technical guide test, in both languages. */
const techTest = (name, body) => guideTest(name, body, TECH_SAYS);

const moved = (over) => Object.assign({
    finding_type: "n_plus_one_sql", service: "shop-svc", plain: "x", trend: "new", change: null, label: null,
    service_label: null
}, over);

test("the technical steps come in the screen's order, each on its anchors", () => {
    const list = PSL.recapTechSteps(techSnapshot(), {});
    assert.deepEqual(list.map((step) => [step.id, step.anchors]), [
        ["view", ["recap.view"]], ["filters", ["recap.filters"]], ["pick", ["recap.pick.services", "recap.pick.groupings"]],
        ["fresh", ["recap.fresh"]], ["period", ["recap.period"]], ["io", ["recap.io"]], ["co2", ["recap.co2"]],
        ["findings", ["recap.findings"]], ["actions", ["recap.actions"]], ["table", ["recap.table"]],
        ["row", ["recap.row"]], ["fix", ["recap.row.detail"]], ["act", ["recap.row.facts", "recap.row.state"]],
        ["below", ["recap.below"]], ["moved", ["recap.moved"]], ["reports", ["recap.reports"]],
        ["card", ["recap.card"]], ["end", []]]);
    const row = list.find((step) => step.id === "row");
    assert.deepEqual(row.body, []);
    assert.deepEqual(row.marks.map((mark) => mark.anchor),
        ["recap.row.rank", "recap.row.what", "recap.row.suggest", "recap.row.figure", "recap.row.trend"]);
    assert.ok(list.every((step) => step.title && Array.isArray(step.body)));
    assert.ok(list.filter((step) => step.id !== "row").every((step) => step.body.length && !step.marks));
    // The card is the pilotage card, word for word.
    const card = list.find((step) => step.id === "card");
    assert.deepEqual([card.title, card.body], [PSL.t("guide.pilotage.card.title"), [PSL.t("guide.pilotage.card.body")]]);
});

test("no priority drops the row, its fix and its action", () => {
    const ids = PSL.recapTechSteps(techSnapshot({priorities: []}), {}).map((step) => step.id);
    for (const id of ["row", "fix", "act"]) assert.ok(!ids.includes(id), id);
});

test("the pick step says the most names a list takes", () => {
    assert.ok(techSteps().pick.body.join(" ").includes(String(PSL.RECAP_FILTER_MAX_VALUES)));
});

techTest("the technical period names its banner, its running days and its notes", (says) => {
    const closed = text(techSteps().period);
    assert.ok(closed.includes(PSL.periodText(techSnapshot().period)));
    for (const phrase of [says.banner, says.closedDays, says.note, says.filtered]) assert.ok(!closed.includes(phrase), phrase);
    const open = text(techSteps({period: {in_progress: true}}).period);
    assert.ok(open.includes(says.closedDays) && open.includes(says.ranking) && !open.includes(says.banner));
    for (const gap of [{period: {in_progress: true, incomplete: ["restart"]}}, {previous: {incomplete: ["sampling"]}}]) {
        const body = text(techSteps(gap).period);
        assert.ok(body.includes(says.banner) && !body.includes(says.closedDays), JSON.stringify(gap));
    }
    assert.ok(text(techSteps({period: {notes: ["sampling"]}}).period).includes(says.note));
    assert.ok(text(techSteps({previous: {notes: ["sampling"]}}).period).includes(says.note));
    assert.ok(text(techSteps({filters: {services: ["a", "b"]}}).period).includes(says.filtered));
});

techTest("the avoidable I/O step carries the tile's figure, its dash and its gaps", (says) => {
    assert.ok(techSteps({tiles: {avoidable_io_ops: 168900}}).io.title.startsWith(PSL.recapCount(168900) + " "));
    assert.equal(techSteps({tiles: {avoidable_io_ops: 1}}).io.title, says.ioOne);
    const none = techSteps({tiles: {avoidable_io_ops: null}}).io;
    assert.equal(none.title, says.ioNone);
    assert.equal(none.body.length, 1);
    assert.ok(none.body[0].includes(says.noVolume));
    assert.ok(!text(techSteps().io).includes(says.noVolume));
    assert.ok(text(techSteps({period: {incomplete: ["restart"]}}).io).includes(says.wholePeriod));
    assert.ok(!text(techSteps({previous: {incomplete: ["restart"]}}).io).includes(says.wholePeriod));
    assert.ok(text(techSteps({truncated: true}).io).includes(says.truncated));
    assert.ok(!text(techSteps().io).includes(says.truncated));
});

techTest("the CO2 step counts the sources without a coefficient, or gives the reason there is none", (says) => {
    const whole = text(techSteps().co2);
    assert.ok(whole.includes(says.magnitude) && !whole.includes(says.atLeastOne) && !whole.includes(says.atLeastMany));
    assert.ok(text(techSteps({tiles: {co2_sources_without_coefficient: 1}}).co2).includes(says.atLeastOne));
    assert.ok(text(techSteps({tiles: {co2_sources_without_coefficient: 3}}).co2).includes(says.atLeastMany));
    // Under a gram the tile shows no "at least".
    assert.ok(!text(techSteps({tiles: {co2_grams: 0.4, co2_sources_without_coefficient: 3}}).co2).includes(says.atLeastMany));
    const idle = techSteps({tiles: {co2_grams: null, co2_missing_reason: "no_avoidable_io"}}).co2;
    assert.deepEqual(idle.body.length, 1);
    assert.ok(idle.body[0].includes(says.noIo));
    const missing = techSteps({tiles: {co2_grams: null, co2_missing_reason: "no_coefficient"}}).co2;
    assert.ok(missing.body[0].includes(says.noCoefficient) && !text(missing).includes(says.magnitude));
});

techTest("findings and actions are counted as their tiles count them", (says) => {
    assert.equal(techSteps({tiles: {active_findings: 1}}).findings.title, says.findingOne);
    assert.equal(techSteps({tiles: {active_findings: 4}}).findings.title, says.findingMany);
    assert.equal(techSteps({tiles: {actions: {done: 1}}}).actions.title, says.actionOne);
    assert.equal(techSteps({tiles: {actions: {done: 2}}}).actions.title, says.actionMany);
    assert.equal(techSteps({tiles: {actions: {done: 0}}}).actions.title, says.actionsNone);
});

techTest("a long ranking says where the rest is until it is shown, an empty one says there is none", (says) => {
    const twelve = Array.from({length: 12}, (_, index) => techRow({rank: index + 1}));
    const cut = text(techSteps({priorities: twelve}).table);
    assert.ok(cut.includes(says.tenFirst) && cut.includes(says.showAll), cut);
    assert.ok(!text(techSteps({priorities: twelve}, {showAll: true}).table).includes(says.tenFirst));
    assert.ok(!text(techSteps().table).includes(says.tenFirst));
    const empty = techSteps({priorities: []}).table;
    assert.equal(empty.title, says.emptyTable);
    assert.deepEqual(empty.anchors, ["recap.table"]);
    assert.equal(empty.body.length, 1);
});

techTest("the row names the business label when there is one and reads its dash", (says) => {
    const what = (over) => techSteps({priorities: [techRow(over)]}).row.marks[1].text;
    assert.ok(what({label: "Order lines"}).includes(says.label));
    assert.ok(!what({}).includes(says.label));
    const figure = (over) => techSteps({priorities: [techRow(over)]}).row.marks[3].text;
    assert.ok(figure({avoidable_io_ops: null}).startsWith(says.dash));
    assert.ok(figure({avoidable_io_ops: 0}).startsWith(says.dash));
    assert.ok(!figure({}).startsWith(says.dash));
});

techTest("the fix step describes SQL layout, its limit and the action history only when they are there", (says) => {
    const fix = (over) => text(techSteps({priorities: [techRow(over)]}).fix);
    assert.ok(fix({}).includes(says.sql) && !fix({}).includes(says.long));
    assert.ok(!fix({finding_type: "n_plus_one_http", template: "GET /orders/{id}"}).includes(says.sql));
    assert.ok(!fix({template: undefined}).includes(says.sql));
    const long = fix({template: "SELECT a FROM b WHERE c = ? " + "AND d = ? ".repeat(70)});
    assert.ok(long.includes(says.sql) && long.includes(says.long));
    assert.ok(!fix({}).includes(says.events));
    assert.ok(fix({action: {status: "todo", events: [{from: null, to: "todo", by: "alice", at_ms: 1}]}}).includes(says.events));
    assert.ok(!fix({action: {status: "todo", events: []}}).includes(says.events));
});

techTest("the act step tells taking from changing, and reads the engine's verdict", (says) => {
    const act = (action) => text(techSteps({priorities: [techRow({action})]}).act);
    const free = act(null);
    assert.ok(free.includes(says.take) && free.includes(says.ack) && !free.includes(says.taken));
    const taken = act({status: "in_progress", confirmation: null});
    assert.ok(taken.includes(says.taken) && !taken.includes(says.take) && !taken.includes(says.verdict));
    const confirmed = act({status: "done", confirmation: "confirmed"});
    assert.ok(confirmed.includes(says.verdict + PSL.confirmationLabel("confirmed")), confirmed);
    assert.ok(!confirmed.includes(says.contradicted));
    for (const state of ["contradicted", "came_back"]) {
        const told = act({status: "done", confirmation: state});
        assert.ok(told.includes(says.verdict + PSL.confirmationLabel(state)) && told.includes(says.contradicted), state);
    }
    assert.ok(!act({status: "done", confirmation: null}).includes(says.verdict));
    for (const body of [free, taken, confirmed]) assert.ok(body.includes(says.name) && body.includes(says.denied));
});

techTest("what moved follows the screen's note, its confirmed fixes, its long groups and its sources", (says) => {
    const base = text(techSteps().moved);
    assert.ok(base.includes(says.groups));
    for (const phrase of [says.noClosed, says.nothing, says.confirmed, says.longGroup, says.sources]) {
        assert.ok(!base.includes(phrase), phrase);
    }
    const none = techSteps({nothing_moved: true, previous: {truncated_to_days: 0}}).moved;
    assert.ok(none.body[0].includes(says.noClosed) && !text(none).includes(says.nothing) && !text(none).includes(says.groups));
    const nothing = techSteps({nothing_moved: true}).moved;
    assert.ok(nothing.body[0].includes(says.nothing) && !text(nothing).includes(says.groups));
    assert.ok(text(techSteps({movements: {gone_with_confirmed_action: 1}}).moved).includes(says.confirmed));
    const six = Array.from({length: 6}, () => moved());
    assert.ok(text(techSteps({movements: {new: six}}).moved).includes(says.longGroup));
    assert.ok(!text(techSteps({movements: {new: six}}, {movesOpen: {new: true}}).moved).includes(says.longGroup));
    // A list the Hub cut counts its whole length.
    const counts = {new: 40, gone: 0, up: 0, down: 0, came_back: 0, done_still_detected: 0};
    assert.ok(text(techSteps({movements: {new: [moved()], counts}}).moved).includes(says.longGroup));
    // Opened but still cut, while its whole list is read, the screen keeps the button.
    assert.ok(text(techSteps({movements: {new: [moved()], counts}}, {movesOpen: {new: true}}).moved).includes(says.longGroup));
    assert.ok(!text(techSteps({movements: {new: Array.from({length: 5}, () => moved())}}).moved).includes(says.longGroup));
    assert.ok(text(techSteps({sources_without_volume: ["daemon-b"]}).moved).includes(says.sources));
});

test("technical text has no em dash, and French addresses the reader as vous", () => {
    const variants = [{}, {period: {in_progress: true, notes: ["sampling"]}}, {period: {incomplete: ["restart"]}},
        {tiles: {avoidable_io_ops: null, co2_grams: null, co2_missing_reason: "no_coefficient"}, truncated: true},
        {tiles: {co2_sources_without_coefficient: 2}}, {priorities: []},
        {priorities: [techRow({label: "x", action: {status: "done", confirmation: "came_back",
            events: [{from: null, to: "done", by: "a", at_ms: 1}]}})]},
        {nothing_moved: true}, {previous: {truncated_to_days: 0}}, {sources_without_volume: ["b"]}];
    for (const lang of ["fr", "en"]) inLang(lang, () => {
        const all = variants.flatMap((over) => PSL.recapTechSteps(techSnapshot(over), {})).map(text).join("\n");
        assert.ok(all.length > 3000, "the variants were read");
        assert.doesNotMatch(all, /—/);
        if (lang === "fr") {
            assert.doesNotMatch(all, /(?<!\p{L})(tu|ton|ta|tes|toi)(?!\p{L})/iu);
            assert.match(all, /(?<!\p{L})vous(?!\p{L})/iu);
        }
    });
});

// ---- the guides of the analysis screens: New, Recent, one run and its report

const GUIDE_LIMITS = {
    max_traces_cap: 2000, analysis_timeout_seconds: 300, report_retention_hours: 72, max_traces_embedded: 50,
    max_read_limit: 1000, fetch_concurrency_warn: 16, fetch_concurrency_max: 64, fetch_concurrency_supported: true,
    engine_memory_limit_mib: 0
};
const HOUR_MS = 3600000;

/** What the New screen's source reads off `state`, a Tempo source on a service run by default. */
const newCtx = (over) => Object.assign({
    sources: 3, runnable: 3, windowHandoff: false, traceHandoff: false, daemon: false, skewPill: false,
    mode: "service", rangeMode: "relative", spanMs: HOUR_MS, retentionHours: 168, maxTraces: 100, weighed: false,
    fetchBand: "ok", queue: 0, limits: GUIDE_LIMITS, blocker: null, unreachable: false, skewBehind: false, command: true
}, over);
const newBy = (over) => Object.fromEntries(PSL.newSteps(newCtx(over)).map((step) => [step.id, step]));

const guideRun = (over) => Object.assign({
    id: "0000000000000001", status: "succeeded", source_id: "tempo-eu", source_name: "Tempo EU", kind: "tempo",
    request: {service: "order-svc", lookback: "1h", max_traces: 100}, error_code: null, producer_version: "0.43.2",
    created_at_ms: 1000, started_at_ms: 2000, finished_at_ms: 9000, expires_at_ms: 1000 + 72 * HOUR_MS,
    result: {findings: 3, critical: 0, warning: 3, info: 0, traces_analyzed: 100, quality_gate_passed: true, warnings: []}
}, over);
const runBy = (over, now = 5000) =>
    Object.fromEntries(PSL.runSteps({run: guideRun(over), now, limits: GUIDE_LIMITS}).map((step) => [step.id, step]));
const reportBy = (over) => Object.fromEntries(PSL.reportSteps({run: guideRun(over)}).map((step) => [step.id, step]));
const recentBy = (first) =>
    Object.fromEntries(PSL.recentSteps({hours: 72, first}).map((step) => [step.id, step]));

const ANALYSIS_SAYS = {
    fr: {
        noSource: "aucune source configurée", noGrant: "Aucune autorisation", window: "d'où vient la fenêtre",
        trace: "« Utiliser l'autre trace »", daemon: "choisissez un backend", missing: "analyses.run",
        skewPill: "« de retard »", traceMode: "Un identifiant désigne", absolute: "Elle est absolue",
        relative: "Elle est relative", timeout: "300 secondes", retention: "Elle dépasse ce que la source",
        undeclared: "Personne n'a déclaré", heavy: "« Lourd »", ceiling: "« Au plafond »", over: "plafond strict de 2000",
        invalid: "au moins une trace", weighed: "mesuré, pas prédit", fetchLimits: "au plus 16 lectures",
        fetchMax: "pas plus de 64", fetchOver: "cocher la case de l'alerte", fetchBlocked: "rien ne permet de lancer",
        retentionHours: "Passé 72 h", queueOne: "derrière 1 tâche.", queueMany: "derrière 3 tâches.",
        ready: "redit ce que l'analyse va faire", blocked: "dit pourquoi : ", unreachable: "Cette source ne répond pas",
        skew: "Le bloc au-dessus nomme l'écart", intro: "supprimés 72 heures", emptyList: "après 72 heures",
        thresholds: "« Seuils »", error: "« Erreur »", logLive: "pas un flux", elapsedRunning: "La barre mesure",
        elapsedQueued: "démarre quand un worker", elapsedTotal: "Le temps total", tuned: "couleur d'avertissement",
        expires: "Le lien expire dans", caveats: "en anglais", frameEmpty: "Un rapport vide est attendu",
        pending: "pas encore prêt", finished: "« La relancer avec les mêmes paramètres »", expiredHours: "vivent 72 heures"
    },
    en: {
        noSource: "no configured source", noGrant: "No grant", window: "where the window comes from",
        trace: "\"Use the other trace\"", daemon: "pick a trace backend", missing: "analyses.run",
        skewPill: "\"behind\"", traceMode: "An ID names", absolute: "It is absolute", relative: "It is relative",
        timeout: "300 seconds", retention: "It goes past what the source", undeclared: "Nobody declared",
        heavy: "\"Heavy\"", ceiling: "\"At the ceiling\"", over: "hard cap of 2000", invalid: "at least one trace",
        weighed: "measured, not predicted", fetchLimits: "at most 16 reads", fetchMax: "no more than 64",
        fetchOver: "tick the alert's box", fetchBlocked: "nothing lets you run", retentionHours: "After 72 h",
        queueOne: "behind 1 job.", queueMany: "behind 3 jobs.", ready: "restates what the analysis will do",
        blocked: "says why: ", unreachable: "This source is not answering", skew: "The block above names the gap",
        intro: "deleted 72 hours", emptyList: "after 72 hours", thresholds: "\"Thresholds\"", error: "\"Error\"",
        logLive: "not a stream", elapsedRunning: "The bar measures", elapsedQueued: "starts when a worker",
        elapsedTotal: "The total time", tuned: "warning colour", expires: "The link expires in", caveats: "in English",
        frameEmpty: "An empty report is expected", pending: "not ready yet",
        finished: "\"Run it again with the same parameters\"", expiredHours: "live 72 hours"
    }
};
const analysisTest = (name, body) => guideTest(name, body, ANALYSIS_SAYS);

test("each analysis screen has its own guide, and the others none yet", () => {
    for (const screen of ["new", "recent", "run", "report"]) assert.equal(PSL.guideIdFor(screen, null), screen);
    assert.equal(PSL.guideIdFor("recap", "tech"), "recap.tech");
    for (const screen of ["ack", "missing"]) assert.equal(PSL.guideIdFor(screen, null), null);
});

test("a window's consequences keep the screen's thresholds", () => {
    const DAY = 24 * HOUR_MS;
    assert.deepEqual(PSL.rangeNotes(DAY, 168), []);
    assert.deepEqual(PSL.rangeNotes(DAY + 1, 168), ["wide"]);
    assert.deepEqual(PSL.rangeNotes(7 * DAY + 1, 168), ["wide", "timeout", "retention"]);
    assert.deepEqual(PSL.rangeNotes(2 * HOUR_MS, 1), ["retention"]);
    assert.deepEqual(PSL.rangeNotes(DAY + 1, null), ["wide", "undeclared"]);
    assert.deepEqual(PSL.rangeNotes(DAY, null), []);
});

test("the New steps come in the screen's order, each on its anchors", () => {
    const list = PSL.newSteps(newCtx({windowHandoff: true}));
    assert.deepEqual(list.map((step) => [step.id, step.anchors]), [
        ["empty", ["new.empty"]], ["handoff", ["new.handoff"]], ["source", ["new.source"]], ["daemon", ["new.daemon"]],
        ["mode", ["new.mode"]], ["range", ["new.range"]], ["traces", ["new.traces"]], ["fetch", ["new.fetch"]],
        ["cost", ["new.cost"]], ["advanced", ["new.advanced"]], ["submit", ["new.submit"]],
        ["terminal", ["new.terminal"]], ["end", []]]);
    assert.ok(list.every((step) => step.title && step.body.length && !step.marks));
});

test("no handoff drops its step, no command drops the terminal", () => {
    const ids = PSL.newSteps(newCtx({command: false})).map((step) => step.id);
    assert.ok(!ids.includes("handoff") && !ids.includes("terminal"));
});

analysisTest("an empty New screen says whether no source exists or none is yours", (says) => {
    assert.ok(text(newBy({sources: 0, runnable: 0}).empty).includes(says.noSource));
    assert.ok(text(newBy({sources: 2, runnable: 0}).empty).includes(says.noGrant));
});

analysisTest("a handoff says where it comes from, and a daemon that cannot take it", (says) => {
    const window = text(newBy({windowHandoff: true}).handoff);
    assert.ok(window.includes(says.window) && !window.includes(says.trace) && !window.includes(says.daemon));
    const trace = text(newBy({traceHandoff: true, daemon: true}).handoff);
    assert.ok(trace.includes(says.trace) && trace.includes(says.daemon) && !trace.includes(says.window));
});

analysisTest("the source step counts the sources left out and the version pills only when there are some", (says) => {
    const plain = text(newBy().source);
    assert.ok(!plain.includes(says.missing) && !plain.includes(says.skewPill));
    assert.ok(text(newBy({runnable: 2}).source).includes(says.missing));
    assert.ok(text(newBy({skewPill: true}).source).includes(says.skewPill));
});

analysisTest("a trace ID run says the window and the cap do not apply", (says) => {
    assert.ok(!text(newBy().mode).includes(says.traceMode));
    assert.ok(text(newBy({mode: "trace"}).mode).includes(says.traceMode));
});

analysisTest("the range says its mode and the consequences the screen lists under it", (says) => {
    const short = text(newBy().range);
    assert.ok(short.includes(says.relative) && !short.includes(says.absolute));
    for (const phrase of [says.timeout, says.retention, says.undeclared]) assert.ok(!short.includes(phrase), phrase);
    assert.ok(text(newBy({rangeMode: "absolute"}).range).includes(says.absolute));
    const long = text(newBy({spanMs: 8 * 24 * HOUR_MS}).range);
    assert.ok(long.includes(says.timeout) && long.includes(says.retention) && long.includes(PSL.dur(168 * HOUR_MS)));
    assert.ok(text(newBy({spanMs: 2 * 24 * HOUR_MS, retentionHours: null}).range).includes(says.undeclared));
});

analysisTest("the trace count reads its band, and the history only when there is one", (says) => {
    const safe = text(newBy().traces);
    for (const phrase of [says.heavy, says.ceiling, says.over, says.invalid, says.weighed]) assert.ok(!safe.includes(phrase), phrase);
    assert.ok(text(newBy({maxTraces: 800}).traces).includes(says.heavy));
    assert.ok(text(newBy({maxTraces: 1500}).traces).includes(says.ceiling));
    assert.ok(text(newBy({maxTraces: 2500}).traces).includes(says.over));
    assert.ok(text(newBy({maxTraces: 0}).traces).includes(says.invalid));
    assert.ok(text(newBy({weighed: true}).traces).includes(says.weighed));
});

analysisTest("the reads step gives the Hub's limits and what its alert asks", (says) => {
    const ok = text(newBy().fetch);
    assert.ok(ok.includes(says.fetchLimits) && ok.includes(says.fetchMax));
    assert.ok(!ok.includes(says.fetchOver) && !ok.includes(says.fetchBlocked));
    assert.ok(text(newBy({fetchBand: "over"}).fetch).includes(says.fetchOver));
    assert.ok(text(newBy({fetchBand: "blocked"}).fetch).includes(says.fetchBlocked));
});

analysisTest("the cost step gives the retention and the queue ahead, singular and plural", (says) => {
    const none = text(newBy().cost);
    assert.ok(none.includes(says.retentionHours) && !none.includes(says.queueOne) && !none.includes(says.queueMany));
    assert.ok(text(newBy({queue: 1}).cost).includes(says.queueOne));
    assert.ok(text(newBy({queue: 3}).cost).includes(says.queueMany));
});

analysisTest("the submit step says what blocks it, an unreachable source and an engine behind", (says) => {
    const ready = text(newBy().submit);
    assert.ok(ready.includes(says.ready) && !ready.includes(says.blocked));
    assert.ok(!ready.includes(says.unreachable) && !ready.includes(says.skew));
    const blocked = text(newBy({blocker: "Pick a service.", unreachable: true, skewBehind: true}).submit);
    assert.ok(blocked.includes(says.blocked + "Pick a service.") && !blocked.includes(says.ready));
    assert.ok(blocked.includes(says.unreachable) && blocked.includes(says.skew));
});

test("the Recent steps come in the screen's order, and no run drops the card", () => {
    assert.deepEqual(PSL.recentSteps({hours: 72, first: guideRun()}).map((step) => [step.id, step.anchors]), [
        ["intro", ["recent.sub"]], ["empty", ["recent.empty"]], ["binaries", ["recent.binaries"]],
        ["tuned", ["recent.tuned"]], ["card", ["recent.card"]], ["end", []]]);
    assert.ok(!PSL.recentSteps({hours: 72, first: null}).some((step) => step.id === "card"));
});

analysisTest("Recent gives the retention, and the card its thresholds and its error when it has them", (says) => {
    const steps = recentBy(guideRun());
    assert.ok(text(steps.intro).includes(says.intro) && text(steps.empty).includes(says.emptyList));
    assert.ok(!text(steps.card).includes(says.thresholds) && !text(steps.card).includes(says.error));
    assert.ok(text(recentBy(guideRun({request: {service: "x", detection: {n_plus_one_threshold: 9}}})).card)
        .includes(says.thresholds));
    assert.ok(text(recentBy(guideRun({status: "failed", error_code: "timeout"})).card).includes(says.error));
});

test("the run steps come in the screen's order, the outcome only once the run is over", () => {
    assert.deepEqual(PSL.runSteps({run: guideRun(), now: 5000, limits: GUIDE_LIMITS}).map((step) => [step.id, step.anchors]), [
        ["head", ["run.head", "run.headline"]], ["log", ["run.log"]], ["elapsed", ["run.elapsed"]],
        ["request", ["run.request"]], ["outcome", ["run.outcome"]], ["end", []]]);
    for (const status of ["pending", "running"]) assert.ok(!("outcome" in runBy({status})), status);
});

analysisTest("the head and the outcome name the run's state", () => {
    const states = {
        queued: {status: "pending"}, running: {status: "running"}, succeeded: {}, empty: {result: {empty: true}},
        failed: {status: "failed", error_code: "timeout"}, interrupted: {status: "interrupted"}, expired: {status: "expired"}
    };
    for (const [key, over] of Object.entries(states)) {
        const steps = runBy(over);
        assert.equal(steps.head.title, PSL.t("guide.run.head." + key + ".title"), key);
        assert.ok(steps.head.body[0] && !steps.head.body[0].includes("{"), key);
        if (steps.outcome) assert.equal(steps.outcome.title, PSL.t("guide.run.outcome." + key + ".title"), key);
    }
});

analysisTest("the run's log, elapsed time, request and outcome read what the run holds", (says) => {
    assert.ok(text(runBy({status: "running"}).log).includes(says.logLive));
    assert.ok(!text(runBy().log).includes(says.logLive));
    assert.ok(text(runBy({status: "running"}).elapsed).includes(says.elapsedRunning));
    assert.ok(text(runBy({status: "pending"}).elapsed).includes(says.elapsedQueued));
    assert.ok(text(runBy().elapsed).includes(says.elapsedTotal));
    assert.ok(text(runBy({status: "running"}).elapsed).includes("300"));
    assert.ok(!text(runBy().request).includes(says.tuned));
    assert.ok(text(runBy({request: {service: "x", detection: {slow_ms: 9}}}).request).includes(says.tuned));
    assert.ok(text(runBy({status: "expired"}).head).includes(says.expiredHours));
    const outcome = text(runBy().outcome);
    assert.ok(outcome.includes(says.expires + " " + PSL.durPrecise(72 * HOUR_MS - 4000)) && !outcome.includes(says.caveats));
    assert.ok(text(runBy({result: {warnings: [{kind: "sampling", message: "x"}]}}).outcome).includes(says.caveats));
    // Past its expiry no link is promised.
    assert.ok(!text(runBy({}, 1000 + 73 * HOUR_MS).outcome).includes(says.expires));
});

analysisTest("the report shows its frame, or says why there is none", (says) => {
    const shown = reportBy();
    assert.deepEqual(Object.keys(shown), ["bar", "frame", "gone", "end"]);
    assert.deepEqual(shown.frame.anchors, ["report.frame"]);
    assert.ok(!text(shown.frame).includes(says.frameEmpty));
    assert.ok(text(reportBy({result: {empty: true}}).frame).includes(says.frameEmpty));
    // A report deleted under a run that still reads succeeded is replaced in place by the expired notice.
    assert.equal(shown.gone.title, PSL.t("report.gone.expired.title"));
    for (const [over, view, phrase] of [[{status: "running"}, "running", says.pending], [{status: "pending"}, "queued", says.pending],
        [{status: "failed"}, "failed", says.finished], [{status: "expired"}, "expired", says.finished]]) {
        const steps = reportBy(over);
        assert.ok(!("frame" in steps), view);
        assert.equal(steps.gone.title, PSL.t("report.gone." + view + ".title"), view);
        assert.ok(text(steps.gone).includes(phrase), view);
    }
});

test("the analysis guides have no em dash, and French addresses the reader as vous", () => {
    const news = [newCtx({windowHandoff: true, traceHandoff: true, daemon: true, runnable: 2, skewPill: true,
        mode: "trace", spanMs: 8 * 24 * HOUR_MS, weighed: true, fetchBand: "over", queue: 2, blocker: "x.",
        unreachable: true, skewBehind: true}), newCtx({sources: 0, runnable: 0, retentionHours: null, spanMs: 2 * 24 * HOUR_MS,
        maxTraces: 1500, fetchBand: "blocked", rangeMode: "absolute"}), newCtx({maxTraces: 800}), newCtx({maxTraces: 2500}),
        newCtx({maxTraces: 0, runnable: 0})];
    const runs = [{}, {status: "pending"}, {status: "running"}, {result: {empty: true}}, {status: "failed"},
        {status: "interrupted"}, {status: "expired"}, {result: {warnings: [{kind: "k", message: "m"}]}}];
    for (const lang of ["fr", "en"]) inLang(lang, () => {
        const all = [
            ...news.flatMap((ctx) => PSL.newSteps(ctx)),
            ...PSL.recentSteps({hours: 72, first: guideRun({status: "failed", error_code: "timeout",
                request: {detection: {a: 1}}})}),
            ...runs.flatMap((over) => PSL.runSteps({run: guideRun(over), now: 5000, limits: GUIDE_LIMITS})),
            ...runs.flatMap((over) => PSL.reportSteps({run: guideRun(over)}))
        ].map(text).join("\n");
        assert.ok(all.length > 6000, "the variants were read");
        assert.doesNotMatch(all, /—/);
        assert.doesNotMatch(all, /guide\.(new|recent|run|report)\./, "every key has a text");
        if (lang === "fr") {
            assert.doesNotMatch(all, /(?<!\p{L})(tu|ton|ta|tes|toi)(?!\p{L})/iu);
            assert.match(all, /(?<!\p{L})vous(?!\p{L})/iu);
        }
    });
});

// ---- the guides of Sources, Incidents and Correlations

const sourcesBy = (over) => Object.fromEntries(PSL.sourcesSteps(Object.assign({
    unreachable: 0, behind: false, unknownProducer: false, lockedDaemon: false, gauges: null
}, over)).map((step) => [step.id, step]));
const incidentsBy = (over) => Object.fromEntries(PSL.incidentsSteps(Object.assign({
    stale: false, unauthorized: null, filtered: false, panel: null
}, over)).map((step) => [step.id, step]));
const correlationsBy = (over) => Object.fromEntries(PSL.correlationsSteps(Object.assign({
    pairOnly: false, limit: null, opened: false, traceable: false
}, over)).map((step) => [step.id, step]));

const FLEET_SAYS = {
    fr: {
        oneDown: "1 source est injoignable.", manyDown: "3 sources sont injoignables.", noThreshold: "aucun seuil",
        unknown: "« Inconnu »", locked: "sources.read", hints: "Aucun conseil", readFailed: "La lecture de ce daemon a échoué",
        stale: "copie de la dernière lecture", refused: "Daemon B a refusé la clé du Hub", filtered: "pour ce filtre",
        unfold: "Dépliez une ligne, puis rouvrez le guide", before: "« avant l'incident » se déclenchaient déjà",
        noFinding: "aucun finding pour cet incident", gone: "la rétention l'a supprimé", analyse: "« Analyser cette fenêtre »",
        pairOnly: "Vous lisez une seule paire", limit: "aux 1000 épisodes", explain: "« Expliquer la dernière paire »",
        notCause: "l'un provoque l'autre"
    },
    en: {
        oneDown: "1 source is unreachable.", manyDown: "3 sources are unreachable.", noThreshold: "no threshold",
        unknown: "\"Unknown\"", locked: "sources.read", hints: "No advice", readFailed: "The read of this daemon failed",
        stale: "copy of the last read", refused: "Daemon B refused the Hub's key", filtered: "for this filter",
        unfold: "Unfold a row, then open the guide again", before: "\"before the incident\" were already firing",
        noFinding: "no finding for this incident", gone: "retention deleted it", analyse: "\"Analyse this window\"",
        pairOnly: "You are reading a single pair", limit: "at the 1000 most recent", explain: "\"Explain the last pair\"",
        notCause: "one causes the other"
    }
};
const fleetTest = (name, body) => guideTest(name, body, FLEET_SAYS);

test("Sources, Incidents and Correlations each have their guide", () => {
    for (const screen of ["sources", "incidents", "correlations"]) assert.equal(PSL.guideIdFor(screen, null), screen);
    for (const screen of ["ack", "missing"]) assert.equal(PSL.guideIdFor(screen, null), null);
});

test("the Sources steps come in the screen's order, the fleet bar and the gauges only when the screen has them", () => {
    const anchors = (over) => PSL.sourcesSteps(Object.assign({
        unreachable: 0, behind: false, unknownProducer: false, lockedDaemon: false, gauges: null
    }, over)).map((step) => [step.id, step.anchors]);
    assert.deepEqual(anchors({behind: true, gauges: "read"}), [
        ["table", ["sources.table"]], ["health", ["sources.health"]],
        ["fresh", ["sources.last_success", "sources.down_for"]], ["producer", ["sources.producer"]],
        ["skew", ["shell.skew"]], ["env", ["sources.env"]], ["error", ["sources.last_error"]],
        ["daemon", ["sources.daemon"]], ["gauges", ["sources.gauges"]], ["end", []]]);
    const plain = anchors({}).map((step) => step[0]);
    assert.ok(!plain.includes("skew") && !plain.includes("gauges"));
});

fleetTest("Sources counts the unreachable sources as the tab does, and judges no age", (says) => {
    assert.ok(!text(sourcesBy().health).includes(says.oneDown));
    assert.ok(text(sourcesBy({unreachable: 1}).health).includes(says.oneDown));
    assert.ok(text(sourcesBy({unreachable: 3}).health).includes(says.manyDown));
    assert.ok(text(sourcesBy().fresh).includes(says.noThreshold));
});

fleetTest("Sources names an unread producer, a locked daemon and a failed read", (says) => {
    assert.ok(!text(sourcesBy().producer).includes(says.unknown));
    assert.ok(text(sourcesBy({unknownProducer: true}).producer).includes(says.unknown));
    assert.ok(!text(sourcesBy().daemon).includes(says.locked));
    assert.ok(text(sourcesBy({lockedDaemon: true}).daemon).includes(says.locked));
    const read = text(sourcesBy({gauges: "read"}).gauges);
    assert.ok(read.includes(says.hints) && !read.includes(says.readFailed));
    const failed = text(sourcesBy({gauges: "failed"}).gauges);
    assert.ok(failed.includes(says.readFailed) && !failed.includes(says.hints));
});

test("the Incidents steps come in the screen's order, the stale copy, a refusal and a panel only when there", () => {
    const steps = PSL.incidentsSteps({stale: true, unauthorized: "Daemon B", filtered: false, panel: "findings"});
    assert.deepEqual(steps.map((step) => [step.id, step.anchors]), [
        ["intro", ["incidents.sub"]], ["stale", ["incidents.stale"]], ["unauthorized", ["incidents.unauthorized"]],
        ["filters", ["incidents.filters"]], ["empty", ["incidents.empty"]], ["table", ["incidents.table"]],
        ["capture", ["incidents.capture"]], ["row", ["incidents.row"]], ["panel", ["incidents.panel"]],
        ["older", ["incidents.older"]], ["copy", ["incidents.copy"]], ["end", []]]);
    const plain = Object.keys(incidentsBy());
    for (const id of ["stale", "unauthorized", "panel"]) assert.ok(!plain.includes(id), id);
});

fleetTest("Incidents names the daemon that refused, a filtered empty list, and an unread fold", (says) => {
    assert.ok(text(incidentsBy({stale: true}).stale).includes(says.stale));
    assert.ok(text(incidentsBy({unauthorized: "Daemon B"}).unauthorized).includes(says.refused));
    assert.ok(!text(incidentsBy().empty).includes(says.filtered));
    assert.ok(text(incidentsBy({filtered: true}).empty).includes(says.filtered));
    assert.ok(text(incidentsBy().row).includes(says.unfold));
    assert.ok(!text(incidentsBy({panel: "findings"}).row).includes(says.unfold));
});

fleetTest("an unfolded incident says what was already firing, nothing frozen, or gone", (says) => {
    const findings = text(incidentsBy({panel: "findings"}).panel);
    assert.ok(findings.includes(says.before) && findings.includes(says.analyse) && !findings.includes(says.noFinding));
    const none = text(incidentsBy({panel: "none"}).panel);
    assert.ok(none.includes(says.noFinding) && none.includes(says.analyse) && !none.includes(says.before));
    const gone = text(incidentsBy({panel: "gone"}).panel);
    assert.ok(gone.includes(says.gone) && !gone.includes(says.analyse));
});

test("the Correlations steps come in the screen's order, the episodes only for an unfolded pair", () => {
    const steps = PSL.correlationsSteps({pairOnly: false, limit: null, opened: true, traceable: false});
    assert.deepEqual(steps.map((step) => [step.id, step.anchors]), [
        ["intro", ["correlations.sub"]], ["cause", []], ["filters", ["correlations.filters"]],
        ["empty", ["correlations.empty"]], ["table", ["correlations.table"]], ["pair", ["correlations.pair"]],
        ["episodes", ["correlations.panel"]], ["note", ["correlations.note"]], ["end", []]]);
    assert.ok(!("episodes" in correlationsBy()));
});

fleetTest("Correlations says a pair is no cause, a single pair, the limit, and the trace link", (says) => {
    assert.ok(text(correlationsBy().cause).includes(says.notCause));
    assert.ok(!text(correlationsBy().filters).includes(says.pairOnly));
    assert.ok(text(correlationsBy({pairOnly: true}).filters).includes(says.pairOnly));
    assert.ok(!text(correlationsBy().table).includes(says.limit));
    assert.ok(text(correlationsBy({limit: 1000}).table).includes(says.limit));
    assert.ok(text(correlationsBy().pair).includes(says.unfold));
    assert.ok(!text(correlationsBy({opened: true}).pair).includes(says.unfold));
    assert.ok(!text(correlationsBy({opened: true}).episodes).includes(says.explain));
    assert.ok(text(correlationsBy({opened: true, traceable: true}).episodes).includes(says.explain));
});

test("the fleet guides have no em dash, and French addresses the reader as vous", () => {
    for (const lang of ["fr", "en"]) inLang(lang, () => {
        const all = [
            ...PSL.sourcesSteps({unreachable: 2, behind: true, unknownProducer: true, lockedDaemon: true, gauges: "read"}),
            ...PSL.sourcesSteps({unreachable: 1, behind: false, unknownProducer: false, lockedDaemon: false, gauges: "failed"}),
            ...["findings", "none", "gone", null].flatMap((panel) =>
                PSL.incidentsSteps({stale: true, unauthorized: "Daemon B", filtered: panel === null, panel})),
            ...PSL.correlationsSteps({pairOnly: true, limit: 1000, opened: true, traceable: true}),
            ...PSL.correlationsSteps({pairOnly: false, limit: null, opened: false, traceable: false})
        ].map(text).join("\n");
        assert.ok(all.length > 5000, "the variants were read");
        assert.doesNotMatch(all, /—/);
        assert.doesNotMatch(all, /guide\.(sources|incidents|correlations|unfold)\b/, "every key has a text");
        for (const state of ["wait", "failed"]) {
            for (const id of ["sources", "incidents", "correlations"]) {
                assert.ok(PSL.hasText("guide." + id + "." + state + ".title"), id + " " + state);
                assert.ok(PSL.hasText("guide." + id + "." + state + ".body"), id + " " + state);
            }
        }
        assert.ok(PSL.hasText("guide.incidents.failed.stale.title") && PSL.hasText("guide.incidents.failed.stale.body"));
        if (lang === "fr") {
            assert.doesNotMatch(all, /(?<!\p{L})(tu|ton|ta|tes|toi)(?!\p{L})/iu);
            assert.match(all, /(?<!\p{L})vous(?!\p{L})/iu);
        }
    });
});

// ---- the guides of Disclosures, Access and Coverage

const DISCLOSURE = {
    id: "d1", report_uuid: "u1", organisation: "Acme", period: {type: "quarter", from: "2026-01-01", to: "2026-03-31"},
    intent: "official", confidentiality: "public", content_hash: "abc", integrity_level: "signed",
    figures: {
        energy_kwh: 10, carbon_kgco2eq: 2, avoidable_ratio: 0.1, coverage: 0.9, applications_declared: null,
        applications_measured: null, applications: []
    },
    files: {report: true, attestation: true, bundle: true, pdf: true}, pdf: "rendered", render_reason: null,
    published_at_ms: 0, signature_added_at_ms: null, withdrawn: null
};
const publicItem = (over) => Object.assign({}, DISCLOSURE, over);
const disclosureDetail = (item, over) => Object.assign({
    disclosure: publicItem(item), checks: {content_hash: "checked", binding: "checked", signature: "declared", engine_version: "0.43.2"},
    share_links: [], deliveries: [], expected_identity: null, expected_issuer: null, can_manage: false
}, over);
const WITHDRAWN = {at_ms: 0, reason: "typo", by: "ada"};
const disclosuresBy = (first) => Object.fromEntries(PSL.disclosuresSteps({first: first === undefined ? publicItem() : first})
    .map((step) => [step.id, step]));
const disclosureBy = (item, over, report = null) => Object.fromEntries(PSL.disclosureSteps({
    detail: disclosureDetail(item, over), report
}).map((step) => [step.id, step]));
const accessBy = (over) => Object.fromEntries(PSL.accessSteps(Object.assign({
    machine: false, noGroup: false, noReach: false, everything: false, tokens: "listed"
}, over)).map((step) => [step.id, step]));
const coverageBy = (over) => Object.fromEntries(PSL.coverageSteps(Object.assign({
    covered: false, truncated: false, backend: false, noConflict: false
}, over)).map((step) => [step.id, step]));
const HOSTS = {aggregate: {total_energy_kwh: 10, database_waste: {in_totals_windows: 2, windows_with_figure: 2, in_totals_energy_kwh: 1}}};

const PUBLIC_SAYS = {
    fr: {
        bracket: "de la moitié au double du chiffre", official: "au moins 75 %", withdrawn: "le bandeau dit par qui",
        unsigned: "n'est pas signé", refused: "a trouvé un problème", notChecked: "pour la raison du bandeau",
        internal: "« Jamais partagée »", rendered: "a recalculé son empreinte", kept: "sans la vérifier ici",
        pdf: "« Télécharger le PDF »", newLink: "« Nouveau lien de partage »", render: "« Rendre à nouveau »",
        withdraw: "« Retirer » demande une raison", noWithdraw: "n'offre plus ni lien", neverLink: "n'a jamais de lien",
        carbonBar: "La barre du carbone", measured: "2 des 3 applications déclarées", both: "« Hôtes de broker »",
        database: "La ligne « Hôtes de base de données »", excluded: "par l'opérateur", hashChecked: "n'ont pas changé depuis",
        hashNot: "n'a pas vérifié l'empreinte", signature: "sur votre ordinateur", audited: "Le niveau audité",
        neverShared: "n'est jamais partagé", locked: "votre accès ne comprend pas", groupChange: "prochaine connexion",
        noGroup: "aucun groupe", noReach: "Aucune autorisation ne concerne", everything: "« Tout » veut dire",
        columns: "le rôle et ses permissions", noToken: "Aucun jeton de terminal.", tokensFailed: "n'a pas listé les jetons",
        revoke: "« Révoquer »", covered: "Rien à attribuer", seen: "les sources où il a été vu", truncated: "200 premières",
        backend: "Un backend de traces", none: "Aucun."
    },
    en: {
        bracket: "from half to twice the figure", official: "at least 75 %", withdrawn: "the banner says by whom",
        unsigned: "is not signed", refused: "found a problem", notChecked: "for the reason in the banner",
        internal: "\"Never shared\"", rendered: "recomputed its content hash", kept: "without checking it here",
        pdf: "\"Download the PDF\"", newLink: "\"New share link\"", render: "\"Render again\"",
        withdraw: "\"Withdraw\" asks for a reason", noWithdraw: "offers neither a share link", neverLink: "never has a share link",
        carbonBar: "The carbon bar", measured: "2 of the 3 declared applications", both: "\"Broker hosts\"",
        database: "The \"Database hosts\" row", excluded: "by the operator", hashChecked: "have not changed since",
        hashNot: "did not check the content hash", signature: "on your computer", audited: "The audited level",
        neverShared: "is never shared", locked: "your access does not include", groupChange: "next sign-in",
        noGroup: "in no group", noReach: "No grant concerns", everything: "\"Everything\" means",
        columns: "the role and its permissions", noToken: "No terminal token.", tokensFailed: "did not list the tokens",
        revoke: "\"Revoke\"", covered: "Nothing to assign", seen: "the sources where it was seen", truncated: "first 200",
        backend: "A trace backend", none: "None."
    }
};
const publicTest = (name, body) => guideTest(name, body, PUBLIC_SAYS);

test("Disclosures, a disclosure, Access and Coverage each have their guide", () => {
    for (const screen of ["disclosures", "disclosure", "access", "coverage"]) assert.equal(PSL.guideIdFor(screen, null), screen);
    for (const screen of ["ack", "missing"]) assert.equal(PSL.guideIdFor(screen, null), null);
});

test("the Disclosures steps come in the screen's order, the card only with a report to read", () => {
    assert.deepEqual(PSL.disclosuresSteps({first: publicItem()}).map((step) => [step.id, step.anchors]), [
        ["intro", ["disc.sub"]], ["empty", ["disc.empty"]], ["rail", ["disc.rail"]], ["legend", ["disc.legend"]],
        ["card", ["disc.card"]], ["publish", ["disc.publish"]], ["end", []]]);
    assert.ok(!("card" in disclosuresBy(null)));
});

test("a disclosure's notes follow the order of its banners, then its chip", () => {
    assert.deepEqual(PSL.disclosureNotes(publicItem()), []);
    assert.deepEqual(PSL.disclosureNotes(publicItem({integrity_level: "hash-only"})), ["unsigned"]);
    assert.deepEqual(PSL.disclosureNotes(publicItem({integrity_level: "none", intent: "internal"})), []);
    assert.deepEqual(PSL.disclosureNotes(publicItem({withdrawn: WITHDRAWN, integrity_level: "none", confidentiality: "internal"})),
        ["withdrawn", "internal"]);
    assert.deepEqual(PSL.disclosureNotes(publicItem({pdf: "not_rendered", render_reason: "binding_mismatch"})), ["refused"]);
    assert.deepEqual(PSL.disclosureNotes(publicItem({pdf: "not_rendered", render_reason: "content_hash_mismatch"})), ["refused"]);
    assert.deepEqual(PSL.disclosureNotes(publicItem({pdf: "not_rendered", render_reason: "engine_missing"})), ["not_checked"]);
});

publicTest("the card of Disclosures quotes the carbon with its bracket and reads the first report's banners", (says) => {
    const plain = text(disclosuresBy().card);
    assert.ok(plain.includes(says.bracket) && plain.includes(says.official));
    for (const note of ["withdrawn", "unsigned", "refused", "notChecked", "internal"]) assert.ok(!plain.includes(says[note]), note);
    assert.ok(text(disclosuresBy(publicItem({withdrawn: WITHDRAWN})).card).includes(says.withdrawn));
    assert.ok(text(disclosuresBy(publicItem({integrity_level: "hash-only"})).card).includes(says.unsigned));
    assert.ok(text(disclosuresBy(publicItem({pdf: "not_rendered", render_reason: "binding_mismatch"})).card).includes(says.refused));
    assert.ok(text(disclosuresBy(publicItem({pdf: "not_rendered", render_reason: null})).card).includes(says.notChecked));
    assert.ok(text(disclosuresBy(publicItem({confidentiality: "internal"})).card).includes(says.internal));
});

test("the disclosure steps come in the screen's order", () => {
    assert.deepEqual(PSL.disclosureSteps({detail: disclosureDetail(), report: null}).map((step) => [step.id, step.anchors]), [
        ["head", ["disc.head", "disc.head.sub"]], ["actions", ["disc.actions"]], ["glance", ["disc.glance"]],
        ["apps", ["disc.apps"]], ["integrity", ["disc.integrity"]], ["files", ["disc.files"]], ["links", ["disc.links"]],
        ["deliveries", ["disc.deliveries"]], ["method", ["disc.method"]], ["end", []]]);
    // No PDF and no right to manage: the bar holds no button, and the step has nothing to say.
    assert.ok(!("actions" in disclosureBy({files: {report: true, attestation: false, bundle: false, pdf: false}})));
});

publicTest("the head of a disclosure says whether the Hub checked it, then its banners", (says) => {
    const rendered = text(disclosureBy().head);
    assert.ok(rendered.includes(says.rendered) && !rendered.includes(says.kept));
    const kept = text(disclosureBy({pdf: "not_rendered", withdrawn: WITHDRAWN}).head);
    assert.ok(kept.includes(says.kept) && kept.includes(says.withdrawn) && kept.includes(says.notChecked));
});

publicTest("the actions of a disclosure name each button the screen shows, and none without the right", (says) => {
    const reader = text(disclosureBy().actions);
    assert.ok(reader.includes(says.pdf));
    for (const button of ["newLink", "render", "withdraw"]) assert.ok(!reader.includes(says[button]), button);
    const manager = text(disclosureBy({}, {can_manage: true}).actions);
    assert.ok(manager.includes(says.newLink) && manager.includes(says.withdraw) && !manager.includes(says.render));
    assert.ok(text(disclosureBy({pdf: "not_rendered"}, {can_manage: true}).actions).includes(says.render));
    const withdrawn = text(disclosureBy({withdrawn: WITHDRAWN}, {can_manage: true}).actions);
    assert.ok(withdrawn.includes(says.noWithdraw) && !withdrawn.includes(says.newLink) && !withdrawn.includes(says.withdraw));
    const internal = text(disclosureBy({confidentiality: "internal"}, {can_manage: true}).actions);
    assert.ok(internal.includes(says.neverLink) && !internal.includes(says.newLink));
});

publicTest("a disclosure's figures, applications and integrity say what the screen shows", (says) => {
    assert.ok(text(disclosureBy().glance).includes(says.carbonBar));
    assert.ok(!text(disclosureBy({figures: Object.assign({}, DISCLOSURE.figures, {carbon_kgco2eq: 0})}).glance).includes(says.carbonBar));
    const plain = text(disclosureBy().apps);
    for (const variant of ["measured", "both", "database", "excluded"]) assert.ok(!plain.includes(says[variant]), variant);
    const figures = Object.assign({}, DISCLOSURE.figures, {applications_declared: 3, applications_measured: 2});
    assert.ok(text(disclosureBy({figures}).apps).includes(says.measured));
    const database = text(disclosureBy({}, {}, HOSTS).apps);
    assert.ok(database.includes(says.database) && !database.includes(says.both));
    const both = Object.assign({}, HOSTS.aggregate, {messaging_waste: HOSTS.aggregate.database_waste});
    assert.ok(text(disclosureBy({}, {}, {aggregate: both}).apps).includes(says.both));
    const excluded = {scope_manifest: {applications_excluded: [{service_name: "batch"}]}};
    assert.ok(text(disclosureBy({}, {}, excluded).apps).includes(says.excluded));
    const checked = text(disclosureBy().integrity);
    assert.ok(checked.includes(says.hashChecked) && checked.includes(says.signature) && checked.includes(says.audited));
    const unchecked = text(disclosureBy({integrity_level: "audited"}, {checks: Object.assign({}, disclosureDetail().checks, {content_hash: "not_checked"})}).integrity);
    assert.ok(unchecked.includes(says.hashNot) && !unchecked.includes(says.hashChecked) && !unchecked.includes(says.audited));
});

publicTest("a disclosure's share links say an internal report is never shared and a reader cannot manage them", (says) => {
    const manager = text(disclosureBy({}, {can_manage: true}).links);
    assert.ok(!manager.includes(says.neverShared) && !manager.includes(says.locked));
    assert.ok(text(disclosureBy().links).includes(says.locked));
    assert.ok(text(disclosureBy({confidentiality: "internal"}, {can_manage: true}).links).includes(says.neverShared));
});

test("the Access steps come in the screen's order", () => {
    assert.deepEqual(PSL.accessSteps({machine: false, noGroup: false, noReach: false, everything: false, tokens: "listed"})
        .map((step) => [step.id, step.anchors]), [
        ["intro", ["access.sub"]], ["who", ["access.who"]], ["groups", ["access.groups"]], ["reach", ["access.reach"]],
        ["tokens", ["access.tokens"]], ["admin", ["access.admin"]], ["projects", ["access.projects"]], ["end", []]]);
});

publicTest("Access says what your sign-in, groups, grants and tokens are", (says) => {
    assert.ok(text(accessBy().who).includes(says.groupChange));
    assert.ok(!text(accessBy({machine: true}).who).includes(says.groupChange));
    assert.ok(!text(accessBy().groups).includes(says.noGroup));
    assert.ok(text(accessBy({noGroup: true}).groups).includes(says.noGroup));
    const reach = text(accessBy().reach);
    assert.ok(reach.includes(says.columns) && !reach.includes(says.everything) && !reach.includes(says.noReach));
    assert.ok(text(accessBy({everything: true}).reach).includes(says.everything));
    const none = text(accessBy({noReach: true}).reach);
    assert.ok(none.includes(says.noReach) && !none.includes(says.columns));
    assert.ok(text(accessBy().tokens).includes(says.revoke));
    const noToken = text(accessBy({tokens: "none"}).tokens);
    assert.ok(noToken.includes(says.noToken) && !noToken.includes(says.revoke));
    assert.ok(text(accessBy({tokens: "failed"}).tokens).includes(says.tokensFailed));
});

test("the Coverage steps come in the screen's order", () => {
    assert.deepEqual(PSL.coverageSteps({covered: false, truncated: false, backend: false, noConflict: false})
        .map((step) => [step.id, step.anchors]), [
        ["intro", ["coverage.sub"]], ["attention", ["coverage.attention"]], ["snippet", ["coverage.snippet"]],
        ["conflicts", ["coverage.conflicts"]], ["tokens", ["coverage.tokens"]], ["config", ["coverage.config"]], ["end", []]]);
});

publicTest("Coverage says nothing is left to assign, a cut list, a trace backend and no conflict", (says) => {
    const listed = text(coverageBy().attention);
    assert.ok(listed.includes(says.seen) && !listed.includes(says.covered));
    for (const variant of ["truncated", "backend"]) assert.ok(!listed.includes(says[variant]), variant);
    const covered = text(coverageBy({covered: true}).attention);
    assert.ok(covered.includes(says.covered) && !covered.includes(says.seen));
    assert.ok(text(coverageBy({truncated: true}).attention).includes(says.truncated));
    assert.ok(text(coverageBy({backend: true}).attention).includes(says.backend));
    assert.ok(!text(coverageBy().conflicts).includes(says.none));
    assert.ok(text(coverageBy({noConflict: true}).conflicts).includes(says.none));
});

test("the public and access guides have no em dash, and French addresses the reader as vous", () => {
    for (const lang of ["fr", "en"]) inLang(lang, () => {
        const all = [
            ...PSL.disclosuresSteps({first: publicItem({withdrawn: WITHDRAWN, pdf: "not_rendered", confidentiality: "internal"})}),
            ...PSL.disclosuresSteps({first: publicItem({integrity_level: "hash-only", pdf: "not_rendered", render_reason: "binding_mismatch"})}),
            ...PSL.disclosureSteps({
                detail: disclosureDetail({
                    figures: Object.assign({}, DISCLOSURE.figures, {applications_declared: 3, applications_measured: 2}),
                    confidentiality: "internal", pdf: "not_rendered"
                }, {can_manage: true}),
                report: {aggregate: {database_waste: HOSTS.aggregate.database_waste}, scope_manifest: {applications_excluded: [{}]}}
            }),
            ...PSL.disclosureSteps({detail: disclosureDetail({withdrawn: WITHDRAWN}, {can_manage: true}), report: null}),
            ...PSL.accessSteps({machine: false, noGroup: true, noReach: false, everything: true, tokens: "listed"}),
            ...PSL.accessSteps({machine: true, noGroup: false, noReach: true, everything: false, tokens: "failed"}),
            ...PSL.accessSteps({machine: true, noGroup: false, noReach: true, everything: false, tokens: "none"}),
            ...PSL.coverageSteps({covered: false, truncated: true, backend: true, noConflict: true}),
            ...PSL.coverageSteps({covered: true, truncated: false, backend: false, noConflict: false})
        ].map(text).join("\n");
        assert.ok(all.length > 6000, "the variants were read");
        assert.doesNotMatch(all, /—/);
        assert.doesNotMatch(all, /guide\.(disclosures?|access|coverage)\b/, "every key has a text");
        for (const state of ["wait", "failed"]) {
            for (const id of ["disclosures", "disclosure"]) {
                assert.ok(PSL.hasText("guide." + id + "." + state + ".title"), id + " " + state);
                assert.ok(PSL.hasText("guide." + id + "." + state + ".body"), id + " " + state);
            }
        }
        if (lang === "fr") {
            assert.doesNotMatch(all, /(?<!\p{L})(tu|ton|ta|tes|toi)(?!\p{L})/iu);
            assert.match(all, /(?<!\p{L})vous(?!\p{L})/iu);
        }
    });
});
