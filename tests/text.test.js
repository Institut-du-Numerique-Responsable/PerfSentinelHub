// The launcher's text tables, its language and the formatters that follow it.
// Node's own runner, no dependency and no build step, like launcher.test.js.
//
// English output is pinned to what the helpers these formatters replace return
// today, so a French screen can never cost an English one a byte.
const test = require("node:test");
const assert = require("node:assert");

globalThis.window = globalThis;
require("../PerfSentinelHub/wwwroot/text-en.js");
require("../PerfSentinelHub/wwwroot/text-fr.js");
require("../PerfSentinelHub/wwwroot/launcher.js");
const PSL = globalThis.PSL;
const TEXT = globalThis.PSL_TEXT;

/** Every `{name}` a value carries, sorted, so two twins compare as sets. */
function placeholders(value) {
    return [...new Set(value.match(/\{\w+\}/g) || [])].sort();
}

/** What differs between two tables: keys one side lacks, placeholders that do not match. */
function tableMismatches(en, fr) {
    const out = [];
    for (const key of Object.keys(en)) if (!(key in fr)) out.push("fr lacks " + key);
    for (const key of Object.keys(fr)) if (!(key in en)) out.push("en lacks " + key);
    for (const key of Object.keys(en)) {
        if (!(key in fr)) continue;
        if (placeholders(en[key]).join() !== placeholders(fr[key]).join()) out.push("placeholders differ in " + key);
    }
    return out;
}

/** Runs `body` in `lang`, then puts English back, so no test leaks its language. */
function inLang(lang, body) {
    PSL.setLang(lang);
    try {
        body();
    } finally {
        PSL.setLang("en");
    }
}

test("every key exists in both languages with the same placeholders", () => {
    assert.deepEqual(tableMismatches(TEXT.en, TEXT.fr), []);
    // The check itself catches a dropped key and a renamed placeholder.
    assert.deepEqual(
        tableMismatches({"a.one": "{count} runs", "a.two": "x"}, {"a.one": "{total} analyses"}),
        ["fr lacks a.two", "placeholders differ in a.one"]);
});

test("the default language follows the browser", () => {
    assert.equal(PSL.pickLang(["fr-CA", "en"], null), "fr");
    assert.equal(PSL.pickLang(["en-US", "fr"], null), "en");
    assert.equal(PSL.pickLang([], null), "en");
    assert.equal(PSL.pickLang(undefined, null), "en");
    assert.equal(PSL.pickLang(["en-US"], "fr"), "fr");
    assert.equal(PSL.pickLang(["fr"], "xx"), "fr");
    assert.equal(PSL.pickLang(["de-DE", "FR-be"], null), "fr");
});

test("the language switches and tells its listeners", () => {
    const seen = [];
    PSL.onLang(lang => seen.push(lang));
    inLang("fr", () => assert.equal(PSL.lang(), "fr"));
    assert.equal(PSL.lang(), "en");
    assert.deepEqual(seen, ["fr", "en"]);
    // An unknown language leaves the shown one as it is.
    PSL.setLang("de");
    assert.equal(PSL.lang(), "en");
});

test("a throwing storage still lets the language switch", () => {
    const storage = Object.getOwnPropertyDescriptor(globalThis, "localStorage");
    Object.defineProperty(globalThis, "localStorage", {
        configurable: true,
        get() {
            throw new Error("storage disabled");
        }
    });
    try {
        inLang("fr", () => assert.equal(PSL.lang(), "fr"));
    } finally {
        if (storage) Object.defineProperty(globalThis, "localStorage", storage);
        else delete globalThis.localStorage;
    }
});

test("a missing French key falls back to English", () => {
    TEXT.en["test.only_en"] = "{count} runs in {source}";
    try {
        inLang("fr", () => assert.equal(PSL.t("test.only_en", {count: 3, source: "tempo"}), "3 runs in tempo"));
        assert.equal(PSL.hasText("test.only_en"), true);
        assert.equal(PSL.hasText("test.nowhere"), false);
        // Under Node a key missing in both languages is a defect, not a screen.
        assert.throws(() => PSL.t("test.nowhere"), /test\.nowhere/);
    } finally {
        delete TEXT.en["test.only_en"];
    }
});

test("numbers and dates follow the language", () => {
    assert.equal(PSL.num(1937000), "1,937,000");
    assert.equal(PSL.pct(0.031, 1), "3.1%");
    assert.equal(PSL.day("2026-10-29", {withYear: true}), "29 October 2026");
    assert.equal(PSL.day("2026-10-29", {withYear: true}), PSL.cardDayText("2026-10-29", true));
    assert.equal(PSL.day("2026-10-29"), PSL.cardDayText("2026-10-29", false));
    const ms = new Date(2026, 9, 29, 14, 5).getTime();
    assert.equal(PSL.dateTime(ms), PSL.dtHuman(ms));
    assert.equal(PSL.dateTime(ms), "2026-10-29 14:05");
    inLang("fr", () => {
        assert.equal(PSL.num(1937000), "1 937 000");
        assert.equal(PSL.pct(0.031, 1), "3,1 %");
        assert.equal(PSL.day("2026-10-29", {withYear: true, short: true}), "29 oct. 2026");
        assert.equal(PSL.day("2026-10-29", {withYear: true}), "29 octobre 2026");
        assert.equal(PSL.dateTime(ms), "29/10/2026 14:05");
    });
});

test("the literal guard finds prose", () => {
    assert.deepEqual(
        PSL.literalsIn("function a() { return el('p', {text: 'Pick a period.'}); }", ["a"]),
        ["Pick a period."]);
    const source = [
        "function b(open) {",
        "    // Don't count the comment's apostrophes as a string.",
        "    const re = /'/g;",
        "    return el('div', {class: open ? 'card card-open' : 'card plain', id: 'recap main',",
        "        style: 'width: 3px; color: red', href: 'https://perf-sentinel.dev/a b',",
        "        'aria-label': 'Close the dialog', title: `Ran ${count} times today`,",
        "        rel: 'noopener noreferrer', dark: matchMedia('(prefers-color-scheme: dark)')},",
        "        [document.querySelector('main section'), t('shell.tab.recap'), 'table-mono']);",
        "}",
        "function c() { return 'Not this function.'; }"
    ].join("\n");
    assert.deepEqual(PSL.literalsIn(source, ["b"]), ["Close the dialog", "Ran ${count} times today"]);
});

test("adopting the page's language does not store it, only a choice does", () => {
    const store = new Map();
    const storage = Object.getOwnPropertyDescriptor(globalThis, "localStorage");
    Object.defineProperty(globalThis, "localStorage", {
        configurable: true,
        value: {setItem: (k, v) => store.set(k, v), getItem: k => store.get(k) ?? null},
    });
    try {
        PSL.setLang("fr", false);
        assert.equal(PSL.lang(), "fr");
        assert.equal(store.size, 0);
        PSL.setLang("en");
        assert.equal(store.get("perf-sentinel:lang"), "en");
    } finally {
        if (storage) Object.defineProperty(globalThis, "localStorage", storage);
        else delete globalThis.localStorage;
        PSL.setLang("en");
    }
});

/** Every code a problem can carry: the constants of ProblemCodes.cs and the card refusals of CardRules.Reasons. */
function problemCodes() {
    const fs = require("node:fs");
    const path = require("node:path");
    const hub = path.join(__dirname, "..", "PerfSentinelHub");
    const constants = fs.readFileSync(path.join(hub, "Api", "ProblemCodes.cs"), "utf8");
    const rules = fs.readFileSync(path.join(hub, "Cards", "CardRules.cs"), "utf8");
    const reasons = rules.match(/Reasons =\s*\[([^\]]*)\]/)[1];
    return [
        ...[...constants.matchAll(/const string \w+ = "(\w+)";/g)].map(match => match[1]),
        ...[...reasons.matchAll(/"(\w+)"/g)].map(match => match[1])
    ];
}

test("every problem code has its sentence", () => {
    const codes = problemCodes();
    assert.ok(codes.length > 60, "read " + codes.length + " codes");
    const missing = codes.flatMap(code =>
        ["en", "fr"].filter(lang => !(("problem." + code) in TEXT[lang])).map(lang => lang + " lacks problem." + code));
    assert.deepEqual(missing, []);
});

test("an unknown code falls back to the detail", () => {
    assert.equal(PSL.problemText({code: "nope", detail: "X"}), "X");
    assert.equal(PSL.problemText({detail: "X"}), "X");
    assert.equal(PSL.problemText(null, 502), "The Hub refused the request (502).");
    assert.equal(PSL.problemText(null, 502, "The Hub refused the card (502)."), "The Hub refused the card (502).");
    // English keeps the Hub's own detail, which can say more than the table's general sentence.
    assert.equal(PSL.problemText({code: "invalid_request", detail: "label must be 80 characters at most."}),
        "label must be 80 characters at most.");
    inLang("fr", () => {
        assert.equal(PSL.problemText({code: "nope", detail: "X"}), "X");
        assert.equal(PSL.problemText({detail: "X"}), "X");
        assert.equal(PSL.problemText(null, 502), "Le Hub a refusé la requête (502).");
        assert.equal(PSL.problemText({code: "acks_not_relayed", detail: "This source relays no acknowledgments."}),
            "Cette source ne relaie aucun acquittement.");
    });
});

test("the card's previous-period refusal is read from its code", () => {
    assert.equal(PSL.isPreviousRefusal("Pas de courbe pour octobre 2026.", "previous_open_day"), true);
    assert.equal(PSL.isPreviousRefusal("No curve for October 2026.", "open_period"), false);
});

test("an ack refusal with a code reads in the shown language", () => {
    const results = [{name: "shop", action: "ack", status: 404, detail: "This source relays no acknowledgments.",
        code: "acks_not_relayed"}];
    assert.deepEqual(PSL.ackSummary(results), [{ok: false, text: "shop: This source relays no acknowledgments."}]);
    inLang("fr", () => assert.match(PSL.ackSummary(results)[0].text, /Cette source ne relaie aucun acquittement\.$/));
});

// ---- the sources, new analysis and run screens

const SCREEN_FUNCTIONS = ["renderSourcesScreen", "sourcesTable", "tableGroupRow", "sourceRow", "healthCell",
    "producerCell", "skeletonTable", "settingCopy", "daemonNameCell", "toggleDaemon", "loadDaemon", "daemonPanel",
    "daemonError", "daemonTopRow", "refreshMs", "refreshSeconds", "refreshControl", "refreshRing", "startTicker",
    "restartSweep", "restartCycle", "stopTicker", "stopAllTickers", "tickRefresh", "replaceIfIdle", "refreshDaemon",
    "gaugeText", "settingsDisclosure", "settingsColumns", "configAbsenceNote", "settingsCards", "settingsPreamble",
    "settingsCard", "isChanged", "settingRow", "settingValue", "daemonValue", "share", "selectedSource", "runSources",
    "selectSource", "setMode", "setMaxTraces", "refreshTraces", "renderNewScreen", "terminalSlot", "refreshTerminal",
    "terminalPanels", "noGrantBanner", "hubUnreachableBanner", "handoffBanner", "traceHandoffBanner", "sourcePanel",
    "sourceRows", "sourceGroupLabel", "sourceRadio", "producerLabel", "parametersPanel", "daemonNotice",
    "sourcesRowLink", "backendControls", "modeSwitch", "serviceInput", "traceInput", "radioKeys", "field",
    "windowLabel", "rangeWire", "windowSpanMs", "rangeControl", "rangeConsequences", "consequence", "rangePicker",
    "closePicker", "applyRange", "dateField", "customRelativeRow", "maxTracesBlock", "sinkBlock", "weightHistory",
    "capNote", "bands", "bandScale", "group", "tracesCap", "embeddedCap", "sinkPanel", "bandStyle", "heavyAck",
    "skewNotice", "upgradeLine", "unreachableNotice", "checkbox", "costBand", "submitBlocker", "submitSentence",
    "queuePhrase", "submitRow", "updateSubmit", "buildRequest", "submit", "renderRunScreen", "unknownRun", "rerun",
    "rerunAction", "backLink", "runView", "eventLog", "logRow", "logClosing", "factsRail", "ceilingRule",
    "ceilingNote", "requestCard", "expiryText", "outcomePanel", "outcomeSpec", "trimNotice", "countStrip",
    "moveBadge", "actionRow", "actionButton", "resubmit", "loadRun", "scheduleRunPoll", "runUnreachableBanner",
    "detectionKnobs", "detectionCount", "setDetection", "refreshDetectionCount", "advancedPanel", "detectionRow"];

const SCREEN_HELPERS = ["sourceHealth", "dur", "durDown", "durParts", "humanDur", "versionGap", "skew", "argsLine",
    "weightBand", "bytes", "hostKwh", "sourceCountLabel", "monitorKeyHint"];

function wwwrootFile(file) {
    return require("node:fs").readFileSync(
        require("node:path").join(__dirname, "..", "PerfSentinelHub", "wwwroot", file), "utf8");
}

test("the sources, new analysis and run screens hold no prose literal", () => {
    const app = wwwrootFile("app.js");
    const launcher = wwwrootFile("launcher.js");
    for (const name of SCREEN_FUNCTIONS) assert.ok(app.includes("function " + name + "("), "app.js has no " + name);
    for (const name of SCREEN_HELPERS) assert.ok(launcher.includes("function " + name + "("), "launcher.js has no " + name);
    assert.deepEqual(PSL.literalsIn(app, SCREEN_FUNCTIONS), []);
    assert.deepEqual(PSL.literalsIn(launcher, SCREEN_HELPERS), []);
});

test("the copy tables of these screens hold text keys, not text", () => {
    const app = wwwrootFile("app.js");
    for (const table of ["DAEMON_COPY", "DETECTION_COPY"]) {
        const start = app.indexOf("const " + table + " = {");
        assert.ok(start >= 0, "app.js has no " + table);
        const body = app.slice(start, app.indexOf("};", start));
        const values = [...body.matchAll(/^\s+[a-z0-9_]+: '([^']*)'/gm)].map(match => match[1]);
        assert.ok(values.length >= 10, table + " holds " + values.length + " entries");
        assert.deepEqual(values.filter(key => !(key in TEXT.en) || !(key in TEXT.fr)), [], table);
    }
});

test("a run's failure vocabulary reads in the shown language", () => {
    assert.match(PSL.ERRORS.timeout, /^the run passed this Hub's time ceiling/);
    assert.equal(PSL.ERROR_TITLES.internal, "The Hub failed on its own side.");
    inLang("fr", () => {
        for (const table of [PSL.ERRORS, PSL.READ_ERRORS, PSL.ERROR_TITLES]) {
            for (const code of Object.keys(table)) assert.doesNotMatch(table[code], /\bthe\b/i, code);
        }
        assert.match(PSL.READ_ERRORS.database_full, /base de données du Hub/);
    });
});

test("durations write their units in the shown language", () => {
    assert.equal(PSL.dur(3_900_000), "1 h 5 m");
    assert.equal(PSL.humanDur("15m"), "15 minutes");
    assert.equal(PSL.humanDur("1h"), "1 hour");
    inLang("fr", () => {
        assert.equal(PSL.dur(3_900_000), "1 h 5 min");
        assert.equal(PSL.dur(90_000_000), "1 j 1 h");
        assert.equal(PSL.dur(45_000), "45 s");
        assert.equal(PSL.durPrecise(90_061_000), "1 j 1 h 1 min 1 s");
        assert.equal(PSL.durMinutes(888_120_000), "10 j 6 h 42 min");
        assert.equal(PSL.humanDur("15m"), "15 minutes");
        assert.equal(PSL.humanDur("1h"), "1 heure");
        assert.equal(PSL.humanDur("7d"), "7 jours");
    });
});

test("durParts reads back what dur writes in either language", () => {
    for (const lang of ["en", "fr"]) {
        inLang(lang, () => {
            for (const ms of [45_000, 3_600_000, 3_900_000, 90_000_000]) {
                const parts = PSL.durParts(ms);
                assert.equal(parts.map(part => part.n + " " + part.u).join(" "), PSL.dur(ms), lang + " " + ms);
            }
            assert.deepEqual(PSL.durParts(null), [{n: PSL.dur(null), u: ""}]);
        });
    }
    inLang("fr", () => assert.deepEqual(PSL.durParts(3_900_000), [{n: "1", u: "h"}, {n: "5", u: "min"}]));
});

test("a source's health, a weight band and a size read in the shown language", () => {
    const down = {reachable: false, unreachable_since_ms: 0, last_error_code: "timeout"};
    assert.deepEqual(PSL.sourceHealth(down, 300_000), {tone: "crit", text: "unreachable 5 m"});
    assert.equal(PSL.weightBand(100).label, "comfortable");
    assert.equal(PSL.bytes(1536), "2 KB");
    inLang("fr", () => {
        assert.deepEqual(PSL.sourceHealth(down, 300_000), {tone: "crit", text: "injoignable depuis 5 min"});
        assert.equal(PSL.sourceHealth({reachable: true, last_attempt_ms: 1}, 1).text, "joignable");
        const band = PSL.weightBand(100);
        assert.equal(band.label, "confortable");
        assert.equal(band.key, "safe");
        assert.match(PSL.weightBand(0, 2000).body, /entre 1 et 2000\./);
        assert.equal(PSL.bytes(512), "512 o");
        assert.equal(PSL.bytes(1536), "2 Ko");
        assert.equal(PSL.bytes(1024 * 1024 * 1.5), "1,5 Mo");
        assert.equal(PSL.hostKwh(0.2), "0,20");
        assert.equal(PSL.hostKwh(0.003), "3,0e-3");
        assert.equal(PSL.skew(null), null);
    });
});

test("a version gap reads in the shown language", () => {
    PSL.setVersions("0.10.0", "0.20.1");
    try {
        assert.equal(PSL.skew("0.19.0").label, "1 minor behind");
        inLang("fr", () => {
            assert.equal(PSL.skew("0.19.0").label, "1 mineure de retard");
            assert.equal(PSL.skew("0.18.0").label, "2 mineures de retard");
            assert.equal(PSL.skew("0.20.2").label, "1 correctif d'avance");
            assert.equal(PSL.skew("1.0.0").label, "1 majeure d'avance");
        });
    } finally {
        PSL.setVersions(null, null);
    }
});

// ---- the incidents, correlations, acknowledgment and access screens

const TASK5_FUNCTIONS = ["renderIncidentsScreen", "unauthorizedBanners", "filterSelect", "incidentFilterLine",
    "incidentCopyNote", "incidentsTable", "incidentRow", "startedText", "incidentNameCell", "incidentPanel",
    "findingsFilter", "incidentFindingsTable", "findingHead", "ackLink", "renderCorrelationsScreen",
    "correlationFilterLine", "correlationsTable", "pairRow", "pairCell", "pairSide", "episodeSpanCell", "pairPanel",
    "timelineStrip", "episodesTable", "episodeActions", "incidentChip", "incidentCorrelationsBlock",
    "incidentEpisodesTable", "allEpisodesLink", "renderAckScreen", "ackResultLines", "ackDetectionFacts",
    "ackDetectionLines", "ackFindingCard", "updateAckButtons", "ackForm", "daemonAckBlock", "ackSourcesTable",
    "ackCell", "relayAck", "submitAck", "renderAccessScreen", "renderCoverageScreen", "accessCard", "accessTable",
    "tokensCard", "revokeButton", "projectsTable", "configurationPart", "configurationCard", "attentionCard",
    "conflictsCard", "renderRecentScreen", "runCard", "cardFacts", "renderMissingScreen", "renderReportScreen",
    "reportBar", "reportGone", "reportLifetime"];

// ackCommand and coverageSnippet write a command line and YAML, which the guard would read as prose: their
// French is pinned below instead.
const TASK5_HELPERS = ["moreFiltersLabel", "phaseLabel", "incidentsCopy", "lagText", "episodeSpan", "pairLabel",
    "episodeEndNote", "ackRows", "formatDetections", "gapLabel", "ackExpiry", "ackExpiryLine",
    "sourceCount", "ackPlan", "ackSummary", "shellBanner", "accessExpiry", "accessSignedIn", "accessReachRows",
    "accessRequestText", "revokeOutcome", "tokenRows", "ownerLabel"];

test("the incidents, correlations, acknowledgment and access screens hold no prose literal", () => {
    const app = wwwrootFile("app.js");
    const launcher = wwwrootFile("launcher.js");
    for (const name of TASK5_FUNCTIONS) assert.ok(app.includes("function " + name + "("), "app.js has no " + name);
    for (const name of TASK5_HELPERS) assert.ok(launcher.includes("function " + name + "("), "launcher.js has no " + name);
    assert.deepEqual(PSL.literalsIn(app, TASK5_FUNCTIONS), []);
    assert.deepEqual(PSL.literalsIn(launcher, TASK5_HELPERS), []);
});

test("the copy tables of these screens read in the shown language", () => {
    const app = wwwrootFile("app.js");
    for (const table of ["INCIDENT_CAPTURE", "INCIDENT_ERROR_TEXT"]) {
        const start = app.indexOf("const " + table + " = ");
        assert.ok(start >= 0, "app.js has no " + table);
        assert.deepEqual(PSL.literalsIn("function x() {" + app.slice(start, app.indexOf(";", start)) + "}", ["x"]), [],
            table);
    }
    assert.equal(PSL.FINDING_TYPE_LABEL.n_plus_one_sql, "N+1 SQL");
    assert.equal(PSL.INCIDENT_KIND_LABEL.memory_saturation, "memory saturation");
    assert.equal(PSL.FINDING_TYPE_LABEL.thirteenth_type, undefined, "an unknown type shows as its id");
    inLang("fr", () => {
        assert.equal(PSL.FINDING_TYPE_LABEL.slow_sql, "SQL lent");
        assert.equal(PSL.INCIDENT_KIND_LABEL.memory_saturation, "saturation mémoire");
        assert.equal(PSL.INCIDENT_READ_STATE.error, "la lecture a échoué");
        assert.equal(PSL.gapLabel("restart"), "un redémarrage du daemon");
        assert.equal(PSL.gapLabel("from_a_newer_engine"), "from_a_newer_engine");
        assert.deepEqual(Object.keys(PSL.INCIDENT_KIND_LABEL), ["oom_kill", "memory_saturation", "restart", "deploy",
            "other"]);
    });
});

test("plural forms follow the shown language", () => {
    assert.equal(PSL.plural(0), "other");
    assert.equal(PSL.plural(1), "one");
    assert.equal(PSL.plural(2), "other");
    inLang("fr", () => {
        assert.equal(PSL.plural(0), "one");
        assert.equal(PSL.plural(1), "one");
        assert.equal(PSL.plural(2), "other");
    });
    assert.deepEqual([0, 1, 2].map(PSL.sourceCount), ["0 sources", "1 source", "2 sources"]);
    inLang("fr", () => assert.deepEqual([0, 1, 2].map(PSL.sourceCount), ["0 source", "1 source", "2 sources"]));
});

test("an ack's expiry reads in the shown language", () => {
    const now = Date.parse("2026-10-20T10:00:00Z");
    inLang("fr", () => {
        assert.throws(() => PSL.ackExpiry("2026-10-19", now), {name: "RangeError", message: "L'échéance est déjà passée."});
        assert.throws(() => PSL.ackExpiry("2026-02-31", now), {message: "L'échéance n'est pas une date."});
        assert.equal(PSL.ackExpiry("2026-11-30", now), "2026-11-30T23:59:59Z");
        assert.equal(PSL.ackExpiryLine("", now), "Sans date : l'acquittement est permanent.");
        assert.equal(PSL.ackExpiryLine("2026-11-30", now), "Valable jusqu'au 2026-11-30 23:59:59 UTC.");
        const plan = PSL.ackPlan([{id: "a", action: "ack", checked: true}], {}, "wait", "", now);
        assert.equal(plan.sentence, "Acquitter écrit sur 1 source, sans échéance.");
        assert.equal(PSL.ackPlan([], {}, "", "", now).sentence, "Aucune source cochée ne peut recevoir d'acquittement ni de révocation.");
    });
});

test("a correlation's span and lag read in the shown language", () => {
    const start = new Date(2026, 9, 29, 14, 5).getTime();
    const sameDay = new Date(2026, 9, 29, 15, 30).getTime();
    const nextDay = new Date(2026, 9, 30, 1, 0).getTime();
    assert.equal(PSL.episodeSpan(start, sameDay), "2026-10-29 14:05 → 15:30");
    assert.equal(PSL.time(sameDay), "15:30");
    inLang("fr", () => {
        assert.equal(PSL.episodeSpan(start, sameDay), "29/10/2026 14:05 → 15:30");
        assert.equal(PSL.episodeSpan(start, nextDay), "29/10/2026 14:05 → 30/10/2026 01:00");
        assert.equal(PSL.time(sameDay), "15:30");
        assert.equal(PSL.lagText(850), "850 ms");
        assert.equal(PSL.lagText(1200), "1,2 s");
        assert.equal(PSL.lagText(90_000), "1 min 30 s");
        assert.equal(PSL.lagText(null), "—");
        assert.equal(PSL.pairLabel({source: {finding_type: "slow_sql", service: "a"},
            target: {finding_type: "zzz", service: "b"}}), "SQL lent dans a, puis zzz dans b");
    });
});

test("the ack command's reason placeholder reads in the shown language", () => {
    const source = {base_url: "http://d:4318"};
    assert.match(PSL.ackCommand(source, "sig", "posix"), / --reason 'why it can wait'$/);
    inLang("fr", () => assert.match(PSL.ackCommand(source, "sig", "posix"), / --reason 'pourquoi cela peut attendre'$/));
});

test("the coverage snippet's comments read in the shown language, its patterns do not move", () => {
    const unmatched = {services: [{service: "reports-svc"}], attributes: [], sources: [{source_id: "billing-stg"}]};
    inLang("fr", () => assert.equal(PSL.coverageSnippet(unmatched), [
        "# Ajoutez les lignes d'une équipe aux motifs de son projet dans hub.access.projects,",
        "# puis redémarrez le Hub. Un finding sans service demande un motif de source ou d'attribut.",
        "- { service: \"reports-svc\" }",
        "- { source: \"billing-stg\" }"
    ].join("\n")));
});

// ---- the disclosures screens

const TASK6_FUNCTIONS = ["loadDisclosures", "loadDisclosure", "sendDisclosure", "periodDays", "shortHash",
    "checkGlyph", "enumWord", "levelLabel", "levelChip", "checkChip", "disclosureChips", "figure", "fixed", "coverageBar",
    "disclosureFigures", "fact", "ago", "disclosureFacts", "disclosureFileButton", "disclosureBanners", "disclosureCard",
    "railView", "railLegend", "publishPanel", "renderDisclosuresScreen", "detailPanel", "simpleTable",
    "carbonBracket", "applicationsPanel", "shareCell", "integrityPanel", "filesPanel", "linkState",
    "shareLinksPanel", "deliveriesPanel", "methodPanel", "disclosureActionButton", "withdrawForm", "detailActions",
    "renderDisclosureScreen"];

// verifyCommand and publishCommand write command lines, which the guard would read as prose: the one comment
// line of the second is pinned below instead.
const TASK6_HELPERS = ["describePattern", "readDisclosureId", "quarterOf", "railCell", "periodRail", "coverageNotch",
    "checkRows", "hostRows", "monthName", "periodLabel"];

test("the disclosures screens hold no prose literal", () => {
    const app = wwwrootFile("app.js");
    const launcher = wwwrootFile("launcher.js");
    for (const name of TASK6_FUNCTIONS) assert.ok(app.includes("function " + name + "("), "app.js has no " + name);
    for (const name of TASK6_HELPERS) assert.ok(launcher.includes("function " + name + "("), "launcher.js has no " + name);
    // The new share link's verify-hash line is a command, not prose.
    assert.deepEqual(PSL.literalsIn(app, TASK6_FUNCTIONS).filter(text => !/perf-sentinel verify-hash --url $/.test(text)), []);
    assert.deepEqual(PSL.literalsIn(launcher, TASK6_HELPERS), []);
    for (const table of ["LEVEL_LABELS", "RENDER_REASONS", "DISCLOSURE_FILES"]) {
        const start = app.indexOf("const " + table + " = ");
        assert.ok(start >= 0, "app.js has no " + table);
        assert.deepEqual(PSL.literalsIn("function x() {" + app.slice(start, app.indexOf(";", start)) + "}", ["x"]), [],
            table);
    }
    assert.ok(!/\bconst MONTHS\b|function longDate\(|function periodLabel\(/.test(app), "app.js keeps its own dates");
});

test("a disclosure's period reads in the shown language", () => {
    const quarter = {type: "calendar-quarter", from: "2026-07-01", to: "2026-09-30"};
    const month = {type: "calendar-month", from: "2026-10-01", to: "2026-10-31"};
    const custom = {type: "custom", from: "2026-07-01", to: "2026-08-15"};
    assert.equal(PSL.periodLabel(quarter), "2026 Q3");
    assert.equal(PSL.periodLabel({type: "calendar-year", from: "2026-01-01", to: "2026-12-31"}), "2026");
    assert.equal(PSL.periodLabel(month), "October 2026");
    assert.equal(PSL.periodLabel(custom), "1 July 2026 to 15 August 2026");
    assert.equal(PSL.monthName(10), "October");
    assert.equal(PSL.monthName(10, "fr"), "octobre");
    inLang("fr", () => {
        assert.equal(PSL.periodLabel(quarter), "T3 2026");
        assert.equal(PSL.periodLabel(month), "octobre 2026");
        assert.equal(PSL.periodLabel(custom), "du 1er juillet 2026 au 15 août 2026");
        assert.equal(PSL.monthName(2), "février");
        assert.equal(PSL.monthName(2, "en"), "February");
        assert.equal(PSL.day("2026-10-01", {withYear: true}), "1er octobre 2026");
        assert.equal(PSL.day("2026-10-11", {withYear: true}), "11 octobre 2026");
        const rail = PSL.periodRail([{id: "q1", period: {type: "calendar-quarter", from: "2026-01-01", to: "2026-03-31"},
            confidentiality: "public", integrity_level: "signed", withdrawn: null}], Date.UTC(2026, 4, 2));
        assert.deepEqual(rail.map(cell => cell.label), ["T1 2026", "T2 2026"]);
    });
});

test("who checked what reads in the shown language", () => {
    const failed = {
        disclosure: {integrity_level: "hash-only"},
        checks: {content_hash: "not_checked", binding: "no_attestation", signature: "absent", engine_version: null}
    };
    assert.deepEqual(PSL.checkRows(failed).map(row => row.by), ["not checked here", "no attestation published",
        "you, with verify-hash against the expected signer", "nobody, the audited level is reserved"]);
    inLang("fr", () => {
        const rows = PSL.checkRows(failed);
        assert.deepEqual(rows.map(row => row.outcome), ["not_checked", "absent", "absent", "none"]);
        assert.equal(rows[0].claim, "Les chiffres correspondent à l'empreinte du contenu");
        assert.equal(rows[0].by, "non vérifié ici");
        assert.equal(rows[1].by, "aucune attestation publiée");
        const checked = PSL.checkRows({disclosure: {integrity_level: "audited"},
            checks: {content_hash: "checked", binding: "checked", signature: "declared", engine_version: "0.39.0"}});
        assert.equal(checked[0].by, "ce Hub, perf-sentinel 0.39.0");
        assert.equal(checked[3].by, "déclaré par le rapport");
        const hosts = PSL.hostRows({aggregate: {database_waste: {windows_with_figure: 2, in_totals_windows: 2,
            in_totals_energy_kwh: 1}}});
        assert.equal(hosts[0].label, "Hôtes de base de données");
    });
});

test("the publish command's comment reads in the shown language, its commands do not move", () => {
    const en = PSL.publishCommand("https://hub.example").split("\n");
    assert.equal(en[5], "# patch integrity.signature and integrity_level into report.json, see REPORTING.md");
    inLang("fr", () => {
        const fr = PSL.publishCommand("https://hub.example").split("\n");
        assert.match(fr[5], /^# reportez integrity\.signature et integrity_level dans report\.json/);
        assert.deepEqual(fr.filter((line, i) => i !== 5), en.filter((line, i) => i !== 5));
    });
});
