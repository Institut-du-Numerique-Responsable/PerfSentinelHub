// Replays a real daemon's reads so the launcher has something to draw, and
// answers the two routes an ack is relayed to.
//
// The bodies in fixtures/ are captures from a real 0.24.0 daemon, not
// hand-written shapes: the findings come from `analyze --format json` on the
// engine's own demo fixture, and the config is that daemon's `api/config`
// verbatim. Only the gauge values in the status files are chosen, because an
// idle daemon reports zeros and a screenshot of zeros teaches nothing. The
// incidents body is a daemon's `api/incidents` after five Alertmanager
// deliveries, read with its read key, and its stamps are slid forward to the
// present as it is served: the screen prints every time as an age, so a body
// captured last quarter would otherwise put "3 months ago" on an OOM kill and
// read as a broken screen. One delta over every millisecond field, so the
// windows, the frozen findings and the before-or-after-the-restart reading all
// keep the distances the daemon measured.
//
// The findings body is slid the same way, and for the same reason. The Hub
// takes `first_seen` and `last_seen` from the envelope's `first_seen_ms` and
// `stored_at_ms`, so a fixed stamp would print every finding as months old and
// no longer active.
//
// The ack listings are captures too, one from a daemon that took an ack at
// runtime and one from a daemon that loaded a CI baseline holding the same
// signature, which no single daemon does: it refuses an ack of its own on a
// signature its baseline already carries. Their stamps are served as captured,
// because the page prints them as the daemon wrote them.
//
// Usage: node fake-daemon.js <port> <busy|calm|acked|baseline>
const http = require("node:http");
const {existsSync, readFileSync} = require("node:fs");
const {join} = require("node:path");

const [port, variant = "busy"] = process.argv.slice(2);
if (!port) {
    console.error("usage: node fake-daemon.js <port> <busy|calm|acked|baseline>");
    process.exit(2);
}

const fixturePath = (name) => join(__dirname, "fixtures", `${name}.json`);
const fixture = (name) => readFileSync(fixturePath(name), "utf8");
/** A body for a daemon that has one, null for a daemon the fixture leaves out. */
const optional = (name) => (existsSync(fixturePath(name)) ? fixture(name) : null);

/**
 * Every epoch-millisecond field of a captured listing, shifted by one delta so
 * the newest row reads as two minutes old whenever the harness runs.
 */
const MS_FIELDS = new Set([
    "at_ms", "ended_at_ms", "window_from_ms", "window_to_ms", "oldest_finding_ms",
    "stored_at_ms", "first_seen_ms",
]);

/** @param {string} body @param {string} anchor the field the newest row is newest by */
function slideToNow(body, anchor) {
    const rows = JSON.parse(body);
    if (rows.length === 0) return body;
    const newest = Math.max(...rows.map((row) => row[anchor]));
    const delta = Date.now() - 120_000 - newest;
    const slide = (node) => {
        if (Array.isArray(node)) return node.map(slide);
        if (node === null || typeof node !== "object") return node;
        return Object.fromEntries(Object.entries(node).map(
            ([key, value]) => [key, MS_FIELDS.has(key) && typeof value === "number" ? value + delta : slide(value)]));
    };
    return JSON.stringify(rows.map(slide));
}

// One report fixture, one version per daemon. A run takes its producer from the
// report it was given, the fleet screen takes it from the daemon's status, so a
// daemon left answering the captured version on one route and its own on the
// other prints itself at two versions across two screens.
const status = JSON.parse(fixture(`daemon-status-${variant}`));
const report = {...JSON.parse(fixture("daemon-report")), binary_version: status.version};

const routes = {
    "/api/status": fixture(`daemon-status-${variant}`),
    "/api/config": fixture("daemon-config"),
    "/api/export/report": JSON.stringify(report),
    "/api/findings": slideToNow(fixture("daemon-findings"), "stored_at_ms"),
    // A daemon with no incidents fixture has its alerting unwired, and answers
    // the empty listing a daemon with nothing recorded answers, not a 404.
    "/api/incidents": slideToNow(optional(`daemon-incidents-${variant}`) ?? "[]", "at_ms"),
};
// A daemon with no acks fixture has no such route at all, which is what a
// daemon below 0.24.0 is, and what leaves the Hub's ack state on it unknown.
const acks = optional(`daemon-acks-${variant}`);
if (acks !== null) routes["/api/acks"] = acks;

// The detection ledger is a capture too, its days slid by whole days so the captured
// day is today. A status that carries a ledger names this one, because the Hub reads
// a ledger only under the id the status gives. A status without one, the daemon a
// minor behind, has no such route, as a daemon below 0.37.0 has none.
//
// The capture's daemon started that day, so alone it would leave every closed day of
// the recap's week and of the week before uncovered, and the recap would say it cannot
// compare. So the daemon is served as started IDLE_DAYS earlier and idle until today:
// each earlier day sealed with nothing counted, which is what a daemon up with no
// traffic records, and why the findings still read as first seen today. Twenty-one days
// cover, on a Sunday, the report frozen on the last closed week and the week before it;
// the start day keeps the gap.
const DAY_MS = 86_400_000;
const IDLE_DAYS = 21;
const ledger = optional("daemon-detections");
if (ledger !== null && status.detection_ledger) {
    const page = JSON.parse(ledger);
    const shift = Math.floor(Date.now() / DAY_MS) - page.current_day;
    const day = (value) => (value === null ? null : value + shift);
    const today = day(page.current_day);
    const header = {
        ledger_id: page.ledger_id, started_ms: page.started_ms + (shift - IDLE_DAYS) * DAY_MS,
        current_day: today, oldest_day: today - IDLE_DAYS, sealed_through: today - 1,
    };
    const captured = page.days.map((row) => ({...row, day: day(row.day), gap: false}));
    const idle = Array.from({length: IDLE_DAYS}, (_, i) => ({
        ...captured[0], day: today - IDLE_DAYS + i, sealed: true, gap: i === 0,
        avoidable_gco2: 0, avoidable_gco2_low: 0, avoidable_io_ops: 0, total_io_ops: 0,
    }));
    routes["/api/detections"] = JSON.stringify({
        ...page, ...header, next_after: null,
        counts: page.counts.map((row) => ({...row, day: day(row.day)})),
        days: [...idle, ...captured],
    });
    // A daemon that holds the whole span retains at least that many days.
    const retained_days = Math.max(status.detection_ledger.retained_days ?? 0, IDLE_DAYS + 1);
    routes["/api/status"] = JSON.stringify({...status, detection_ledger: {...status.detection_ledger, ...header, retained_days}});
}

/** Where the Hub relays an ack, POST, or a revoke, DELETE. */
const ACK_WRITE = /^\/api\/findings\/[^/]+\/ack$/;
const ACK_WRITE_STATUS = {POST: 201, DELETE: 204};

http
    .createServer((req, res) => {
        // Drained rather than read: nothing here is stored, and a relayed body left
        // unread can reset the connection the Hub is waiting on.
        req.resume();
        const path = req.url.split("?")[0].replace(/\/$/, "");
        // Answered, not recorded. The listing above is a capture, and a daemon that
        // took its own writes would drift away from it mid-demo.
        if (ACK_WRITE.test(path)) {
            res.writeHead(ACK_WRITE_STATUS[req.method] ?? 405);
            res.end();
            return;
        }
        const body = routes[path];
        if (body === undefined) {
            res.writeHead(404, {"content-type": "application/json"});
            res.end('{"error":"not found"}');
            return;
        }
        res.writeHead(200, {"content-type": "application/json"});
        res.end(body);
    })
    .listen(Number(port), "127.0.0.1", () =>
        console.log(`fake daemon (${variant}) on http://127.0.0.1:${port}`),
    );
