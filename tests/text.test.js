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
