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

test("French says grant where English does, and no link dies", () => {
    const keys = Object.keys(TEXT.en);
    assert.deepEqual(keys.filter(key => /\bgrants?\b/i.test(TEXT.en[key]) && /\bdroits?\b/i.test(TEXT.fr[key])
        && !/\brights?\b/i.test(TEXT.en[key])), []);
    // A crashed process may die, a link is disabled or expires.
    assert.deepEqual(keys.filter(key => /\blien/i.test(TEXT.fr[key]) && /\b(mort|morts|meurt)\b/i.test(TEXT.fr[key])), []);
});

test("a choice also goes to the cookie the sign-in and device pages read", () => {
    const cookies = [];
    globalThis.document = {
        documentElement: {},
        set cookie(value) {
            cookies.push(value);
        },
    };
    try {
        PSL.setLang("fr", false);
        assert.deepEqual(cookies, []);
        PSL.setLang("fr");
        assert.deepEqual(cookies, ["perf-sentinel-lang=fr;max-age=31536000;samesite=lax"]);
        assert.equal(globalThis.document.documentElement.lang, "fr");
    } finally {
        delete globalThis.document;
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

// ---- the recap

const RECAP_FUNCTIONS = ["recapRoute", "goRecap", "loadRecap", "readRecapReports", "renderRecapScreen",
    "recapErrorBanner", "recapFilterLine", "recapTextFilter", "recapViewSwitch", "recapHeading", "recapHelp",
    "recapNotes", "recapPeriodBlock", "recapTile", "recapProgressChip", "recapPreviousText", "recapCount",
    "recapCo2Tile", "recapTiles", "recapPriorities", "recapPriorityNotes", "recapRow", "recapTechWhat",
    "recapPlainWhat", "recapServiceHead", "recapTrend", "recapFigure", "recapDetail", "recapFix", "recapCodeLocation",
    "recapFacts", "recapEvents", "recapStateChip", "recapWhen", "recapOwner", "recapActionReadOnly",
    "recapActionControls", "recapFoot", "recapSend", "recapWrite", "recapNameTarget", "recapNamingTarget",
    "recapNameButton", "recapNameEditor", "recapNameSave", "recapMoveWho", "recapMoveItem", "recapMoved",
    "recapMovedNote", "recapReportsPanel", "recapGenerateBar", "recapGenerate", "recapReportRow"];

const RECAP_HELPERS = ["serviceParts", "namesService", "readRecapRoute", "recapHash", "recapPath", "recapReportLabel",
    "recapFreezeText", "recapErrorText", "recapTruncatedText", "recapSourcesNote", "recapLateNote", "recapReportStatus",
    "recapDeliveryText", "recapOnDemandBody", "co2Parts", "co2Text", "co2Tail", "hasAvoidableVolume", "dividesBefore",
    "trendText", "confirmationLabel", "periodText", "incompleteText", "notesText", "httpUrl", "localTime", "recapPlain"];

test("the recap screen holds no prose literal", () => {
    const app = wwwrootFile("app.js");
    const launcher = wwwrootFile("launcher.js");
    for (const name of RECAP_FUNCTIONS) assert.ok(app.includes("function " + name + "("), "app.js has no " + name);
    for (const name of RECAP_HELPERS) assert.ok(launcher.includes("function " + name + "("), "launcher.js has no " + name);
    assert.deepEqual(PSL.literalsIn(app, RECAP_FUNCTIONS), []);
    assert.deepEqual(PSL.literalsIn(launcher, RECAP_HELPERS), []);
    for (const table of ["RECAP_TODO_LABEL", "RECAP_REPORT_VIEWS", "RECAP_PERIOD_CHOICES"]) {
        const start = app.indexOf("const " + table + " = ");
        assert.ok(start >= 0, "app.js has no " + table);
        assert.deepEqual(PSL.literalsIn("function x() {" + app.slice(start, app.indexOf(";", start)) + "}", ["x"]), [],
            table);
    }
    for (const table of ["RECAP_LABELS", "RECAP_HELP", "RECAP_REPORTS_COPY"]) {
        const start = launcher.indexOf("const " + table + " = ");
        assert.ok(start >= 0, "launcher.js has no " + table);
        assert.deepEqual(PSL.literalsIn("function x() {" + launcher.slice(start, launcher.indexOf(";", start)) + "}",
            ["x"]), [], table);
    }
});

// The French twin of launcher.test.js TECHNICAL, as RecapSentencesTests.FrenchTechnicalWords.
const TECHNICAL_FR = /requête|base de données|endpoint|broker|pool|connexion|sql|http|i\/o|e\/s|finding|signature|moteur|daemon|démon|registre|coefficient|trace|ingestion|échantillon|source|service/i;
const RECAP_GAP_REASONS = ["uncovered_days", "open_day", "restart", "sampling", "shed", "ingest_rejected", "overflow",
    "multi_instance"];

test("Management_copy_carries_no_technical_word, in French", () => inLang("fr", () => {
    const route = PSL.readRecapRoute("#/recap?period=month&at=2026-10-02&service=checkout&view=pilotage");
    const report = {
        subscription: null, created_by: "alice", nothing_moved: false, complete: false,
        period: {kind: "week", from: "2026-10-19", to: "2026-10-25"}, deliveries: []
    };
    const texts = [
        ...RECAP_GAP_REASONS.concat("brand_new").map((reason) => PSL.gapLabel(reason, "pilotage")),
        PSL.incompleteText(RECAP_GAP_REASONS, "pilotage"),
        PSL.notesText(RECAP_GAP_REASONS, 0.5, "pilotage"),
        PSL.co2Text(2000, 3, null, "pilotage"),
        PSL.co2Text(null, 0, "no_coefficient", "pilotage"),
        PSL.co2Text(null, 0, "no_avoidable_io", "pilotage"),
        ...["confirmed", "contradicted", "came_back", "awaiting"].map((state) => PSL.confirmationLabel(state, "pilotage")),
        PSL.recapTruncatedText("pilotage"),
        PSL.recapSourcesNote(["svc-quiet"], "pilotage"),
        PSL.recapLateNote(["svc-quiet"]),
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
        PSL.recapDeliveryText({channel: "webhook", target: "ops-chat", state: "delivered", attempts: 1, last_status: 204}),
        PSL.recapDeliveryText({channel: "email", target: "pm@acme.test", state: "failed", attempts: 8, last_status: null}),
        ...Object.keys(TEXT.fr).filter(key => key.startsWith("recap.plain.")).map(key => PSL.t(key)),
        // app.js: the Management screen's own words.
        ...["recap.prio.more.one", "recap.prio.more.other", "recap.view.pilotage", "recap.view.tech",
            "recap.prio.divider.pilotage", "recap.figure.none", "recap.tile.acked.pilotage.one",
            "recap.tile.acked.pilotage.other", "recap.label.pilotage.io_lower", "recap.prio.hint", "recap.title",
            "recap.sub", "recap.moves.nothing", "recap.moves.none_closed", "recap.where", "recap.where.move",
            "recap.where.move.at", "recap.tile.co2", "recap.tile.actions.checked", "recap.tile.actions.open",
            "recap.state.not_taken", "recap.state.done_with", "recap.when.done", "recap.when.updated"]
            // A placeholder names a value, not a word the reader meets.
            .map(key => PSL.t(key).replace(/\{\w+\}/g, "x"))
    ];
    assert.equal(texts.filter(text => PSL.t("recap.plain.n_plus_one_sql") === text).length, 1);
    for (const text of texts) {
        assert.ok(text, "every text is set");
        assert.doesNotMatch(text, TECHNICAL_FR);
    }
}));

test("Management_help_carries_no_technical_word, in French", () => inLang("fr", () => {
    const keys = ["period", "io", "co2", "findings", "actions", "prio", "moved"];
    assert.deepEqual(Object.keys(PSL.RECAP_HELP.pilotage), keys);
    for (const text of Object.values(PSL.RECAP_HELP.pilotage)) assert.doesNotMatch(text, TECHNICAL_FR);
    assert.ok(PSL.RECAP_HELP.pilotage.prio.endsWith(
        " Une équipe peut nommer ses applications et ses étapes dans le Hub, pour que ce rapport parle sa langue."));
}));

// Every French string below is pinned in RecapTextTests too, so a frozen report and the #/recap screen read alike.
test("the recap's figures and words read in the shown language", () => inLang("fr", () => {
    assert.equal(PSL.co2Text(2000, 1, null), "au moins ~2 kg CO2e, estimation, 1 source sans coefficient");
    assert.equal(PSL.co2Text(2500, 2, null), "au moins ~2,5 kg CO2e, estimation, 2 sources sans coefficient");
    assert.equal(PSL.co2Text(1234567, 0, null), "~1 230 kg CO2e, estimation");
    assert.equal(PSL.co2Text(999.5, 0, null), "~1 000 g CO2e, estimation");
    assert.equal(PSL.co2Text(null, 0, "no_coefficient"), "— aucun coefficient du moteur");
    assert.equal(PSL.co2Text(null, 0, "no_avoidable_io"), "— aucune I/O évitable");
    assert.equal(PSL.co2Text(null, 0, "no_coefficient", "pilotage"), "— non disponible");
    assert.equal(PSL.co2Text(0.4, 2, null, "pilotage"),
        "< 1 g CO2e, estimation, une partie de l'activité n'a pas d'estimation CO2");
    assert.deepEqual(PSL.co2Parts(2000, 1, null), {lead: "au moins", figure: "~2 kg", unit: "CO2e"});
    assert.equal(PSL.trendText("up", 0.35), "+35 %");
    assert.equal(PSL.trendText("down", -0.2), "-20 %");
    assert.equal(PSL.trendText("down", -0.004), "0 %");
    assert.equal(PSL.trendText("up", null), "en hausse");
    assert.equal(PSL.trendText("flat", 0), "stable");
    assert.equal(PSL.trendText("new", null), "nouveau");
    assert.equal(PSL.trendText("gone", null), "disparu");
    assert.equal(PSL.trendText("unknown", null), "");
    assert.equal(PSL.confirmationLabel("confirmed"), "Confirmée par le moteur");
    assert.equal(PSL.confirmationLabel("awaiting", "pilotage"), "En attente d'une journée complète de mesures");
    assert.equal(PSL.periodText({kind: "week", from: "2026-10-26", to: "2026-11-01", in_progress: true}),
        "du 26 oct. 2026 au 1er nov. 2026, en cours");
    assert.equal(PSL.periodText({kind: "day", from: "2026-10-29", to: "2026-10-29", in_progress: false}),
        "29 oct. 2026");
    assert.equal(PSL.incompleteText(["open_day", "x"], "pilotage"),
        "Période incomplète : une journée pas encore terminée, un autre manque de mesure");
    assert.equal(PSL.incompleteText(["open_day", "x"]), "Période incomplète : un jour encore ouvert, x");
    assert.equal(PSL.notesText(["sampling", "shed"], 0.5, "pilotage"),
        "Note : seule une partie du trafic a été examinée (taux 0,5), une partie de l'activité a été écartée sous la charge");
    assert.equal(PSL.recapTruncatedText("pilotage"),
        "Le Hub a lu les 5 000 premiers problèmes et s'est arrêté là. Resserrez les filtres pour voir les autres.");
    assert.equal(PSL.recapSourcesNote(["a", "b"], "tech"), "Sources sans volume de détection : a, b.");
    assert.deepEqual(PSL.RECAP_LABELS.pilotage, {io: "Opérations inutiles", findings: "Problèmes ouverts",
        moved: "Ce qui a changé"});
    assert.match(PSL.recapErrorText(403, "tech"), /findings\.read/);
}));

test("a recap report and its freeze line read in the shown language", () => inLang("fr", () => {
    const week = {subscription: "weekly-shop", period: {kind: "week", from: "2026-10-19", to: "2026-10-25"}, complete: false};
    assert.equal(PSL.recapReportLabel(week), "Semaine du 19 oct. 2026 au 25 oct. 2026 · weekly-shop · incomplet");
    assert.equal(PSL.recapReportLabel({subscription: null, created_by: "alice", complete: true,
        period: {kind: "day", from: "2026-10-28", to: "2026-10-28"}}), "Journée du 28 oct. 2026 · demandé par alice");
    assert.equal(PSL.recapReportStatus({subscription: null, nothing_moved: false, deliveries: []}),
        "À la demande, non envoyé");
    assert.equal(PSL.recapDeliveryText({channel: "email", target: "pm@example.test", state: "failed", attempts: 1,
        last_status: 550}), "email pm@example.test : échec (550), 1 essai");
    assert.equal(PSL.recapDeliveryText({channel: "webhook", target: "ops", state: "pending", attempts: 3}),
        "webhook ops : en attente, 3 essais");
    assert.equal(PSL.recapFreezeText(PSL.readRecapRoute("#/recap?period=month&at=2026-10-02&service=x")),
        "Fige le mois qui contient le 2 oct. 2026, avec les filtres ci-dessus.");
    assert.equal(PSL.recapFreezeText(PSL.readRecapRoute("#/recap")), "Fige la dernière semaine close.");
}));

test("a recap time reads in the shown language, in the browser's zone", () => {
    const kept = process.env.TZ;
    try {
        process.env.TZ = "Europe/Paris";
        inLang("fr", () => {
            assert.equal(PSL.localTime(Date.UTC(2026, 9, 29, 13, 32, 59)), "29/10/2026 14:32 CET");
            assert.equal(PSL.localTime(Date.UTC(2026, 9, 28, 23, 5)), "29/10/2026 00:05 CET");
        });
    } finally {
        if (kept === undefined) delete process.env.TZ; else process.env.TZ = kept;
    }
});

test("a Management row's plain sentence follows the shown language, an unknown type keeps its own", () => {
    const known = {finding_type: "n_plus_one_sql", plain: "stored"};
    const unknown = {finding_type: "blocking_wait", plain: "The stored sentence."};
    assert.equal(PSL.recapPlain(known), "For each item on a page, the application fetches the same kind of information again, instead of fetching it all at once.");
    assert.equal(PSL.recapPlain(unknown), "The stored sentence.");
    inLang("fr", () => {
        assert.equal(PSL.recapPlain(known), "Pour chaque élément d'une page, l'application va de nouveau chercher le même type d'information, au lieu de tout récupérer en une fois.");
        assert.equal(PSL.recapPlain(unknown), "The stored sentence.");
    });
});

// ---- the share card section

const CARD_FUNCTIONS = ["cardState", "cardToday", "cardPeriodFrom", "cardOptionsReady", "cardFix", "cardLabel",
    "cardForm", "cardFileName", "cardCurveRefusal", "cardBlocked", "cardFields", "cardFieldValue", "cardFieldError",
    "cardInvalid", "cardPlace", "cardRender", "cardMarkStale", "cardPick", "cardPickFormat", "loadCardOptions",
    "loadCardKeys", "recapCardPanel", "cardImageGlyph", "cardSegmented", "cardLegend", "cardAngleGroup",
    "cardStepButton", "cardPeriodGroup", "cardWarn", "cardEnvironmentLine", "cardPeriodFacts", "cardFixGroup",
    "cardNoFix", "cardFixRow", "cardTyped", "cardTextField", "cardWordsGroup", "cardLabelField", "cardFormatGroup",
    "cardActions", "cardPreviewAside", "cardPreviewShown", "cardPreviewFrame", "cardPreviewCard", "cardPreviewEmpty",
    "cardPreviewProof", "cardAttachBlock", "cardSignedFigures", "cardBusyNote", "cardPost", "cardFailure",
    "cardRememberLabel", "cardMake", "cardPreview", "cardDownload", "cardCheckAttachment", "cardKeysPanel",
    "cardKeysFill", "cardKeyRotate", "cardKeysFile", "cardCopyLine", "cardKeyRow", "cardRevokeOpen",
    "cardRevokeConfirm", "cardKeyWrite"];

const CARD_HELPERS = ["previewFormat", "cardBusy", "cardDate", "cardIso", "cardPeriodStart", "stepPeriod",
    "lastClosedPeriod", "canStepForward", "cardDayText", "cardPeriodName", "cardRangeText", "cardText", "cardBody",
    "cardOptionsPath", "canRememberLabel", "rememberState", "cardFieldProblem", "isPreviousRefusal",
    "cardContactText", "attachmentText", "contentLine", "signedFigureLines", "revokeText", "levelLine",
    "cardEnvironmentLine", "cardVolume", "cardFixLine", "cardRefusalText", "cardLang"];

test("the share card section holds no prose literal", () => {
    const app = wwwrootFile("app.js");
    const launcher = wwwrootFile("launcher.js");
    for (const name of CARD_FUNCTIONS) assert.ok(app.includes("function " + name + "("), "app.js has no " + name);
    for (const name of CARD_HELPERS) assert.ok(launcher.includes("function " + name + "("), "launcher.js has no " + name);
    // An Accept header, and the prefix of the Hub's English detail a refusal without a code is read by.
    const wire = ["application/json, image/png, image/svg+xml, image/jxl, application/pdf", "No curve for "];
    assert.deepEqual(PSL.literalsIn(app, CARD_FUNCTIONS).filter(text => !wire.includes(text)), []);
    assert.deepEqual(PSL.literalsIn(launcher, CARD_HELPERS).filter(text => !wire.includes(text)), []);
    assert.doesNotMatch(app, /CARD_KEY_STATES/);
    for (const table of ["CARD_FREE"]) {
        const start = app.indexOf("const " + table + " = ");
        assert.ok(start >= 0, "app.js has no " + table);
        assert.deepEqual(PSL.literalsIn("function x() {" + app.slice(start, app.indexOf(";", start)) + "}", ["x"]), [],
            table);
    }
    for (const table of ["CARD_PERIOD_LABELS", "CARD_ANGLE_NAMES", "CARD_ANGLE_LINES", "CARD_FORMAT_LABELS",
        "CARD_FORMAT_NOTES", "CARD_COPY", "ATTACH_REASONS", "ATTACH_VERDICTS", "SIGNED_FIGURE_LABELS"]) {
        const start = launcher.indexOf("const " + table + " = ");
        assert.ok(start >= 0, "launcher.js has no " + table);
        assert.deepEqual(PSL.literalsIn("function x() {" + launcher.slice(start, launcher.indexOf(";", start)) + "}",
            ["x"]), [], table);
    }
});

/** The texts launcher.test.js Card_copy_carries_no_technical_word reads, in the shown language. */
function cardCopyTexts() {
    return [
        ...Object.values(PSL.CARD_ANGLE_LINES),
        ...Object.values(PSL.CARD_PERIOD_LABELS),
        ...Object.values(PSL.CARD_FORMAT_LABELS),
        ...Object.values(PSL.CARD_FORMAT_NOTES),
        PSL.cardEnvironmentLine("prod", "pilotage"),
        PSL.cardEnvironmentLine(null, "pilotage"),
        PSL.cardContactText("acme.example", "pilotage"),
        ...Object.keys(PSL.FINDING_TYPE_LABEL).map((type) => PSL.cardFixLine({finding_type: type, before_fix_ops: null}).text),
        PSL.cardFixLine({finding_type: "n_plus_one_sql", before_fix_ops: 5, before_period_name: "May 2026"}).text,
        ...Object.values(PSL.SIGNED_FIGURE_LABELS),
        PSL.contentLine({content: "intact"}),
        PSL.attachmentText({integrity: "ok", content: "modified", level: 1, domain: null, reason: "content_modified"},
            false).text,
        PSL.CARD_COPY.busy
    ];
}

test("Card_copy_carries_no_technical_word, in French", () => inLang("fr", () => {
    const words = ["I/O", "E/S", "finding", "requête", "endpoint", "signature", "DNS"];
    for (const text of cardCopyTexts()) {
        assert.ok(text, "every text is set");
        assert.ok(!/[A-Z][a-z]+ [a-z]+ [a-z]+ the /.test(text), `"${text}" reads English`);
        for (const word of words) assert.ok(!text.includes(word), `"${text}" holds "${word}"`);
    }
    assert.equal(PSL.cardEnvironmentLine("prod", "pilotage"),
        "Environnement prod, repris du filtre du récap ci-dessus. Une carte couvre toutes ses applications.");
    assert.equal(PSL.CARD_PERIOD_LABELS.semester, "Semestre");
    assert.equal(PSL.CARD_ANGLE_NAMES.story, "Récit");
}));

test("a card period reads in words in the shown language", () => {
    inLang("fr", () => {
        assert.equal(PSL.cardPeriodName("month", "2026-10-01"), "octobre 2026");
        assert.equal(PSL.cardPeriodName("quarter", "2026-07-01"), "T3 2026");
        assert.equal(PSL.cardPeriodName("semester", "2026-01-01"), "premier semestre 2026");
        assert.equal(PSL.cardPeriodName("semester", "2026-07-01"), "second semestre 2026");
        assert.equal(PSL.cardPeriodName("year", "2026-01-01"), "2026");
        assert.equal(PSL.cardPeriodName("week", "2026-10-05"), "la semaine du 5 octobre 2026");
        assert.equal(PSL.cardPeriodName("week", "2026-06-01"), "la semaine du 1er juin 2026");
        assert.equal(PSL.cardPeriodName("month", "2026-10-01", "en"), "October 2026");
        assert.equal(PSL.cardRangeText("2026-10-01", "2026-10-31"), "du 1er au 31 octobre 2026, UTC");
        assert.equal(PSL.cardRangeText("2026-07-01", "2026-09-30"), "du 1er juillet au 30 septembre 2026, UTC");
        assert.equal(PSL.cardRangeText("2025-12-29", "2026-01-04"), "du 29 décembre 2025 au 4 janvier 2026, UTC");
    });
    assert.equal(PSL.cardPeriodName("month", "2026-10-01", "fr"), "octobre 2026");
    assert.equal(PSL.cardPeriodName("week", "2026-10-05"), "the week of 5 October 2026");
});

// CardRulesTests.Every_reason_has_a_sentence, so the screen and the API's English detail read alike.
const CARD_RULES_EN = {
    open_period: "A card covers a closed period. Pick an earlier one.",
    uncovered_days: "Some days of this period were not recorded, so its figures are not whole.",
    open_day: "Some days of this period are still being counted. Try again tomorrow.",
    overflow: "On some days of this period there were too many different problems to count each one, so its figures are not whole.",
    previous_uncovered_days: "No curve for October 2026. The curve compares with September 2026, and some of its days were not recorded. A story or a collective card still works for October 2026.",
    previous_open_day: "No curve for October 2026. The curve compares with September 2026, and some of its days are still being counted. A story or a collective card still works for October 2026.",
    previous_overflow: "No curve for October 2026. The curve compares with September 2026, and on some of its days there were too many different problems to count each one. A story or a collective card still works for October 2026.",
    no_confirmed_fix: "No fix of this period is confirmed by the measure yet.",
    unknown_fix: "This fix is not among the confirmed fixes of the period. Pick one from the list.",
    unprintable_fix: "This fix's service or endpoint name is too long or holds a hidden character, so it cannot be printed on a card.",
    no_total_io: "The curve needs the total activity of each day, which this period or the one before does not hold yet.",
    curve_needs_whole_sources: "The curve covers whole sources, and you read only part of one.",
    unprintable_scope: "A source or environment name is too long or holds a hidden character, so it cannot be printed on a card. Ask an administrator to rename it.",
    scope_too_long: "The environment and source names of this card are too long together to print on it. Pick an environment or a source.",
    unkept_fixes: "Some fixes of this period were marked done before this Hub kept their details, so it cannot count them all. A story or a curve card still works."
};

test("a card refusal reads from its code and its period", () => {
    for (const [code, text] of Object.entries(CARD_RULES_EN)) {
        assert.equal(PSL.cardRefusalText(code, "month", "2026-10-01"), text, code);
    }
    inLang("fr", () => {
        assert.equal(PSL.cardRefusalText("previous_uncovered_days", "month", "2026-10-01"),
            "Pas de courbe pour cette période (octobre 2026). La courbe la compare à la période précédente "
            + "(septembre 2026), dont certains jours n'ont pas été enregistrés. Une carte récit ou collective reste "
            + "possible pour cette période.");
        assert.equal(PSL.cardRefusalText("open_period", "month", "2026-10-01"), PSL.t("problem.open_period"));
        assert.equal(PSL.t("card.refused", {period: "T3 2026"}), "Pas de carte pour cette période (T3 2026).");
    });
});

test("an attachment verdict, the level line and the revoke confirmation read in the shown language", () => inLang("fr", () => {
    const base = {integrity: "ok", content: "intact", level: 1, domain: "acme.example", key: "unknown"};
    assert.deepEqual(PSL.attachmentText({...base, level: 2, key: "current", reason: "key_published"}, false),
        {text: "Rattachée : les cartes affichent « Signé par acme.example ».", title: null});
    assert.deepEqual(PSL.attachmentText({...base, key: "revoked", reason: "key_revoked"}, true),
        {text: "Cette carte a été signée avec une clé désormais révoquée.", title: "key_revoked"});
    assert.deepEqual(PSL.attachmentText({...base, reason: "binding_partial"}, true), {
        text: "Vérification impossible : l'un des deux emplacements qui publient la clé sous acme.example n'a "
            + "pas répondu.",
        title: "binding_partial"
    });
    assert.equal(PSL.contentLine({content: "intact"}), "Fichier intact : c'est bien le fichier qui a été signé.");
    assert.equal(PSL.levelLine(true, "acme.example"),
        "Sur la page de vérification, cette carte affiche : Signé par acme.example.");
    const said = PSL.revokeText("2026-10-01", false, "acme.example");
    assert.equal(said.alert, "Toutes les cartes signées par cette clé apparaîtront révoquées sur la page de "
        + "vérification, quelle que soit leur date. La page de vérification indiquera que la clé a pu être exposée "
        + "depuis le 1er octobre 2026. Cette action est irréversible.");
    assert.equal(said.step, "Republiez la ligne DNS de cette clé sous acme.example : la page de vérification ne "
        + "lit que la ligne DNS, et continue d'afficher ces cartes comme signées tant que la ligne ne dit pas révoquée.");
    assert.ok(PSL.revokeText("2026-10-01", true, "acme.example").step
        .endsWith(" La prochaine carte crée une nouvelle clé : publiez aussi sa ligne, sous acme.example."));
    assert.ok(PSL.revokeText("2026-10-01", true, null).step.endsWith(" La prochaine carte crée une nouvelle clé."));
}));

test("a card's figures and fixes read in the shown language", () => {
    // pct writes the permille as the old (value / 10).toFixed(1) + '%' did, for every value.
    for (let permille = 0; permille <= 1000; permille++) {
        assert.equal(PSL.signedFigureLines({period: {kind: "year", from: "2026-01-01"}, last_waste_permille: permille})[1].value,
            (permille / 10).toFixed(1) + "%");
    }
    inLang("fr", () => {
        assert.deepEqual(PSL.signedFigureLines({
            period: {kind: "month", from: "2026-09-01", to: "2026-09-30"},
            before_fix_period: {from: "2026-07-01", to: "2026-07-31"},
            cited_ms: 2349, before_fix_ops: 1937000, last_waste_permille: 31
        }), [
            {label: "Période", value: "septembre 2026"},
            {label: "Période avant la correction", value: "juillet 2026"},
            {label: "Chiffre principal de la carte", value: "2 349 millisecondes"},
            {label: "Opérations inutiles avant la correction", value: "1 937 000"},
            {label: "Part du travail qui était évitable, période de la carte", value: "3,1 %"}
        ]);
        assert.deepEqual(PSL.cardFixLine({finding_type: "n_plus_one_sql", before_fix_ops: 1_940_000}, "septembre 2026"),
            {figure: "1,9 million", text: " d'opérations inutiles avant la correction (septembre 2026)"});
        assert.equal(PSL.cardFixLine({finding_type: "n_plus_one_sql", before_fix_ops: 2_500_000}, "x").figure,
            "2,5 millions");
        assert.deepEqual(PSL.cardFixLine({finding_type: "redundant_http", before_fix_ops: 410_500}, "T3 2026"),
            {figure: "410 mille", text: " opérations inutiles avant la correction (T3 2026)"});
        assert.equal(PSL.cardFixLine({finding_type: "redundant_http", before_fix_ops: 9_999}, "x").figure, "9 999");
        assert.deepEqual(PSL.cardFixLine({finding_type: "slow_sql", before_fix_ops: null}),
            {figure: null, text: "la carte montre la durée de ses passages les plus lents"});
        assert.equal(PSL.cardFieldProblem("a​b"),
            "La police de la carte ne sait pas dessiner « ​ ». Retirez-le, ou écrivez-le autrement.");
        assert.deepEqual(PSL.rememberState("Afficher un panier", null, "cette action"),
            {text: "Retenir ce nom pour cette action", enabled: true});
    });
    // The period a caller names wins over the Hub's English one, which stays the default.
    assert.equal(PSL.cardFixLine({finding_type: "n_plus_one_sql", before_fix_ops: 5, before_period_name: "May 2026"}).text,
        " unnecessary operations in May 2026, before the fix");
});

test("the card language follows the Hub until picked", () => {
    assert.equal(PSL.cardLang(null), "en");
    assert.equal(PSL.cardLang("fr"), "fr");
    inLang("fr", () => {
        assert.equal(PSL.cardLang(null), "fr");
        assert.equal(PSL.cardLang("en"), "en");
        assert.equal(PSL.cardLang("de"), "fr");
    });
    const app = wwwrootFile("app.js");
    assert.match(app, /lang: PSL\.cardLang\(card\.lang\)/);
    assert.doesNotMatch(app, /getItem\(CARD_LANG_KEY\) === 'fr'/);
});

// ---- the terminal sign-in page

test("the terminal approval page reads in French", () => {
    const now = new Date(2026, 9, 14, 14, 3, 40).getTime();
    const verify = {
        user_code: "BCDF-GHJK", client_address: "203.0.113.7", same_network: false,
        requested_at_ms: new Date(2026, 9, 14, 14, 2, 31).getTime(), identity: "ada@example.com",
        token_expires_at_ms: new Date(2026, 9, 14, 18, 42).getTime()
    };
    inLang("fr", () => {
        assert.equal(PSL.deviceIntro(verify.token_expires_at_ms),
            "Saisissez le code affiché par votre terminal. Ne saisissez qu'un code issu d'un perf-sentinel login "
            + "que vous venez de lancer vous-même : l'autoriser donne à ce terminal votre accès jusqu'à 18:42.");
        assert.match(PSL.deviceIntro(null), /votre accès jusqu'à la fin de votre session\.$/);
        assert.equal(PSL.deviceRequestLine(verify, now),
            "Demandé depuis 203.0.113.7 à 14:02:31 (il y a 1 minute). Votre identité\u00a0: ada@example.com. "
            + "Le jeton expire à 18:42.");
        assert.equal(PSL.deviceRequestLine(Object.assign({}, verify, {requested_at_ms: now - 5000}), now).split(". ")[0],
            "Demandé depuis 203.0.113.7 à 14:03:35 (il y a moins d'une minute)");
        assert.equal(PSL.deviceRequestLine(Object.assign({}, verify, {requested_at_ms: now - 180000, identity: null,
            token_expires_at_ms: null}), now),
            "Demandé depuis 203.0.113.7 à 14:00:40 (il y a 3 minutes). Votre identité\u00a0: inconnue. "
            + "Le jeton expire à la fin de votre session.");
        assert.equal(PSL.deviceNetworkAlert(verify),
            "Ce code a été demandé depuis un autre réseau que celui de ce navigateur (203.0.113.7). N'autorisez ce code "
            + "que si vous avez lancé vous-même cette connexion sur cette machine.");
        assert.equal(PSL.t("device.approve_anyway"), "Autoriser quand même");
        assert.equal(PSL.t("device.answered", {status: 502}), "Le Hub a répondu 502.");
    });
    // English keeps the request line's unknown identity and session end as they were.
    assert.equal(PSL.deviceRequestLine(Object.assign({}, verify, {identity: null, token_expires_at_ms: null}), now),
        "Requested from 203.0.113.7 at 14:02:31 (1 minute ago). You are signed in as unknown. The token expires at "
        + "the end of your session.");
});

test("the terminal approval page holds no prose literal and takes the page's language first", () => {
    const device = wwwrootFile("device.js");
    const launcher = wwwrootFile("launcher.js");
    const helpers = ["deviceIntro", "deviceRequestLine", "deviceNetworkAlert"];
    for (const name of helpers) assert.ok(launcher.includes("function " + name + "("), "launcher.js has no " + name);
    assert.deepEqual(PSL.literalsIn(launcher, helpers), []);
    assert.deepEqual(PSL.literalsIn("function x() {" + device + "}", ["x"]).filter(text => text !== "use strict"), []);
    // The server wrote <html lang>: the scripted lines follow it, without storing it as a choice.
    const setLang = device.indexOf("PSL.setLang(document.documentElement.lang, false)");
    assert.ok(setLang >= 0 && setLang < device.indexOf("addEventListener('DOMContentLoaded'"));
    assert.match(device, /PSL\.problemText\(answer\.payload, answer\.status, PSL\.t\('device\.answered'/);
});

test("no screen holds a prose literal", () => {
    // Each file whole, so every function and every table at the top of the module is read.
    const allowlist = JSON.parse(require("node:fs").readFileSync(
        require("node:path").join(__dirname, "text-allowlist.json"), "utf8"));
    for (const entry of allowlist) assert.ok(entry.why && entry.why.length > 10, "no reason for " + entry.texts);
    for (const file of ["app.js", "launcher.js", "device.js"]) {
        const found = PSL.literalsIn("function x() {" + wwwrootFile(file) + "}", ["x"]);
        const allowed = allowlist.filter(entry => entry.file === file).flatMap(entry => entry.texts);
        assert.deepEqual(found.filter(text => !allowed.includes(text)), [], file);
        // An entry no literal matches any more is dropped, not kept as a blank cheque.
        assert.deepEqual(allowed.filter(text => !found.includes(text)), [], file + " allowlist");
    }
});
