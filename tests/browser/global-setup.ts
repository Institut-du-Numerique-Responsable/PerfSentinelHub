import {ChildProcess, spawn, spawnSync} from "node:child_process";
import {pbkdf2Sync, randomBytes} from "node:crypto";
import {existsSync, mkdirSync, readFileSync, rmSync, writeFileSync} from "node:fs";
import {join, resolve} from "node:path";
import {tmpdir} from "node:os";

// The Hub has no seeder, no fixture loader and no demo mode. Its validation
// refuses to start without a source, and a daemon view is read live rather
// than from storage, so the only way to a populated screen is a Hub that is
// really running against daemons that really answer.
//
// So this stands up: four fake daemons replaying real captures, a Hub built
// from this checkout, and four analysis runs submitted through the API to give
// the runs screen more than one state to show. Then a second Hub from the same
// build with sign-in and access control on, since the access screens exist only
// there, and a fake provider to sign people in to it.

const ROOT = resolve(__dirname, "..", "..");
const WORK = join(tmpdir(), "perf-sentinel-hub-demo");
const HUB_PORT = 41500;
const BUSY_PORT = 41400;
const CALM_PORT = 41401;
// Nothing listens here, on purpose: a trace backend is never polled, so an
// unreachable row needs a daemon.
const DEAD_PORT = 41403;
// The two daemons the ack page needs beside the busy one: one holding an ack of
// its own on the finding, one whose CI baseline holds it. Four daemons then
// carry the same finding, which is what gives that page one row per case.
const ACKED_PORT = 41405;
const BASELINE_PORT = 41406;
const BASE = `http://127.0.0.1:${HUB_PORT}`;
// The second Hub, with sign-in and access control on, for the screens that exist only
// then. Its own port, its metrics port, the fake provider it signs people in with, and
// a daemon source nothing listens for, which only ever gets findings pushed to it.
const ACCESS_PORT = 41520;
const ACCESS_METRICS_PORT = 41521;
const PROVIDER_PORT = 41510;
const PUSHED_PORT = 41407;
const ACCESS_BASE = `http://127.0.0.1:${ACCESS_PORT}`;

// global.json pins the SDK with rollForward: disable, and it is usually not
// the dotnet on PATH. DOTNET_ROOT names the install that carries it when the
// system-wide one has fallen behind the pin.
const PINNED_SDK = process.env.DOTNET_ROOT || "/usr/local/share/dotnet";
// The pinned SDK is reached by naming its binary outright rather than by putting
// its directory in front of PATH. Prepending would decide which `dotnet` runs by
// search order, and any writable directory later on that PATH could shadow it.
const DOTNET = existsSync(join(PINNED_SDK, "dotnet")) ? join(PINNED_SDK, "dotnet") : "dotnet";
const dotnetEnv = () => ({
    ...process.env,
    ...(existsSync(PINNED_SDK) ? {DOTNET_ROOT: PINNED_SDK} : {})
});

function engineBinary(): string {
    const fromEnv = process.env.HUB_ENGINE_BINARY;
    if (fromEnv) return fromEnv;
    const sibling = resolve(ROOT, "..", "..", "RustroverProjects", "perf-sentinel",
        "target", "release", "perf-sentinel");
    if (existsSync(sibling)) return sibling;
    throw new Error(
        "No perf-sentinel binary. Three of the seven screens need one, because the " +
        "Hub answers 503 to POST /api/analyses without it. Set HUB_ENGINE_BINARY, " +
        "or build the engine with `cargo build --release`.");
}

// The finding the ack page is captured on: the one a CI baseline acknowledges,
// which is the single row of the baseline daemon's listing the daemon store did
// not write. Read from the fixture rather than pinned here, so recapturing the
// fixtures against a newer engine moves the screenshot with them.
function ackSignature(): string {
    const path = join(__dirname, "demo", "fixtures", "daemon-acks-baseline.json");
    const rows = JSON.parse(readFileSync(path, "utf8")) as { signature: string, source: string }[];
    const baseline = rows.find((row) => row.source === "toml");
    if (!baseline) {
        throw new Error(`${path} holds no CI baseline ack, so the ack screen has no finding to open. ` +
            "Run demo/capture-fixtures.sh against a 0.24.0 or newer engine.");
    }
    return baseline.signature;
}

// Retries until `attempt` yields a value or the deadline passes. Recursive rather
// than a loop because each attempt waits on the previous one's answer.
async function poll<T>(attempt: () => Promise<T | undefined>, deadline: number,
                       pauseMs: number, failure: string): Promise<T> {
    const result = await attempt();
    if (result !== undefined) return result;
    if (Date.now() >= deadline) throw new Error(failure);
    await new Promise((r) => setTimeout(r, pauseMs));
    return poll(attempt, deadline, pauseMs, failure);
}

async function waitFor(url: string, seconds: number): Promise<void> {
    await poll(async () => {
        try {
            return (await fetch(url)).ok || undefined;
        } catch {
            return undefined; // not up yet
        }
    }, Date.now() + seconds * 1000, 250, `${url} never came up`);
}

async function submit(sourceId: string, request: unknown): Promise<string> {
    const response = await fetch(`${BASE}/api/analyses`, {
        method: "POST",
        headers: {"content-type": "application/json"},
        body: JSON.stringify({source_id: sourceId, request})
    });
    const body = await response.json() as { id?: string };
    if (!body.id) throw new Error(`submit ${sourceId}: ${JSON.stringify(body)}`);
    return body.id;
}

async function settle(id: string, seconds: number): Promise<string> {
    return poll(async () => {
        const run = await (await fetch(`${BASE}/api/analyses/${id}`)).json() as { status: string };
        return run.status === "pending" || run.status === "running" ? undefined : run.status;
    }, Date.now() + seconds * 1000, 300, `run ${id} never reached a terminal state`);
}

// The same fixed epoch demo/capture-fixtures.sh pins. Observations are then
// rebased onto it and rounded to the second, which is as reproducible as a live
// capture gets: run ids are renumbered below, but how long the four runs took
// still varies between captures, so the file moves when that moves.
const EPOCH_MS = 1_756_512_000_000;
const SECOND_MS = 1_000;
// Above this a number is a moment, below it a duration. window_duration_ms and
// its neighbours are durations and must survive untouched.
const IS_TIMESTAMP = 1_000_000_000_000;
// The two moments a finding envelope spells without the suffix.
const MOMENTS = new Set(["first_seen", "last_seen"]);

function rebase(value: unknown, capturedAt: number): unknown {
    if (Array.isArray(value)) return value.map((v) => rebase(v, capturedAt));
    if (value && typeof value === "object") {
        const out: Record<string, unknown> = {};
        for (const [k, v] of Object.entries(value)) {
            out[k] = (k.endsWith("_ms") || MOMENTS.has(k)) && typeof v === "number" && v > IS_TIMESTAMP
                ? Math.round((EPOCH_MS - (capturedAt - v)) / SECOND_MS) * SECOND_MS
                : rebase(v, capturedAt);
        }
        return out;
    }
    return value;
}

// Records what the Hub answers on the routes the launcher reads, so the site
// can serve a populated launcher with no Hub behind it. Captured here
// rather than in a script of its own because this is the only place a populated
// Hub exists, and capturing beside the screenshots keeps the two in step.
async function captureEmbedFixtures(runs: string[]): Promise<void> {
    const capturedAt = Date.now();
    const routes: Record<string, unknown> = {};
    // A run id is drawn from RandomNumberGenerator, so left alone it would rewrite
    // this file on every capture. Renumbered in order, the fixture only moves when
    // the data does. The replacements stay 16 hex characters because the launcher
    // routes #/run/{id} on exactly that shape.
    const stable = new Map(runs.map((id, i) => [id, (i + 1).toString(16).padStart(16, "0")]));
    const renumber = (text: string) =>
        [...stable].reduce((acc, [from, to]) => acc.split(from).join(to), text);
    const read = async (path: string): Promise<[string, unknown]> => {
        let response = await fetch(BASE + path);
        // A gated route (DaemonViewGate admits two reads) turns the rest of a
        // parallel batch away with 503 and a Retry-After, so wait and ask again.
        for (let attempt = 0; response.status === 503 && response.headers.has("retry-after") && attempt < 10; attempt++) {
            await new Promise((resolve) => setTimeout(resolve, 1000 * Number(response.headers.get("retry-after"))));
            response = await fetch(BASE + path);
        }
        if (!response.ok) throw new Error(`${path} answered ${response.status}`);
        const body = rebase(await response.json(), capturedAt);
        return [renumber(path), JSON.parse(renumber(JSON.stringify(body)))];
    };
    // Fetched together, recorded in the order given, so the file's key order holds.
    const readAll = async (paths: string[]) =>
        Object.assign(routes, Object.fromEntries(await Promise.all(paths.map(read))));

    // The incidents screen reads the first page, then one record per unfolded
    // row. The id is the daemon's own hash, so it is stable across captures.
    const incidents = "/api/incidents?limit=100&offset=0";
    await readAll([
        "/api/status",
        "/api/sources",
        "/api/analyses?limit=500",
        ...runs.map((id) => `/api/analyses/${id}`),
        // Only a daemon has a view. A trace backend answers 400, which is correct and
        // is not something the embed needs to replay.
        ...["checkout-prod", "billing-stg", "search-prod", "orders-prod", "ci-main"]
            .map((id) => `/api/sources/${id}/daemon`),
        // What the ack page reads, spelled the way the launcher spells it, so the Ack
        // link of an unfolded incident lands on a page with something on it rather
        // than on an embed that has no answer for the route.
        `/api/findings?signature=${encodeURIComponent(ackSignature())}&include_acked=true&limit=1`,
        incidents
    ]);
    await readAll((routes[incidents] as { id: string }[]).map((incident) => `/api/incidents/${incident.id}`));

    writeFileSync(join(__dirname, "demo", "fixtures", "hub-embed.json"),
        JSON.stringify({epoch_ms: EPOCH_MS, routes}, null, 2) + "\n");
}

// Hub configuration written as nested values, the shape of the Helm values, and flattened
// to the environment variables the Hub binds: arrays become indexes, keys join with "__".
function hubEnv(prefix: string, value: unknown): Record<string, string> {
    if (value === null || typeof value !== "object") return {[prefix]: String(value)};
    return Object.assign({}, ...Object.entries(value).map(([key, inner]) => hubEnv(`${prefix}__${key}`, inner)));
}

// The PHC string `PerfSentinelHub hash-password` prints, computed here so the setup
// needs no second build. The password is thrown away: the sign-in page is captured
// for its form, and nothing signs in through it.
function localPasswordHash(): string {
    const salt = randomBytes(16);
    const key = pbkdf2Sync(randomBytes(24).toString("base64"), salt, 600_000, 32, "sha256");
    const unpadded = (bytes: Buffer) => bytes.toString("base64").replace(/=+$/, "");
    return `$pbkdf2-sha256$i=600000$${unpadded(salt)}$${unpadded(key)}`;
}

// The access screens read what the Hub holds about attributes, and no capture carries
// any: the engine's demo trace file has no grouping attribute. So the pushed source gets
// captured findings with a grouping written here, in three pushes, because one copy
// seen under two groupings is what a conflict is: a signature first seen under team=shop
// then under team=payments, another seen under team=shop then under no grouping at all.
async function pushGroupedFindings(importKey: string): Promise<void> {
    const findings = JSON.parse(readFileSync(join(__dirname, "demo", "fixtures", "daemon-findings.json"), "utf8")) as
        { finding: Record<string, unknown> }[];
    const {version} = JSON.parse(readFileSync(join(__dirname, "demo", "fixtures", "daemon-status-busy.json"), "utf8")) as
        { version: string };
    const grouped = (envelope: { finding: Record<string, unknown> }, team?: string) =>
        ({...envelope, finding: {...envelope.finding, grouping: team ? [{key: "team", value: team}] : []}});
    const push = async (batch: unknown[]) => {
        const response = await fetch(`${ACCESS_BASE}/api/import/findings?source_id=shop-prod`, {
            method: "POST",
            headers: {"content-type": "application/json", "x-api-key": importKey},
            body: JSON.stringify({producer_version: version, findings: batch})
        });
        if (!response.ok) throw new Error(`the push to the access Hub answered ${response.status}`);
    };
    await push([...findings.slice(0, 6).map((f) => grouped(f, "shop")),
        ...findings.slice(6).map((f) => grouped(f, "payments"))]);
    await push([grouped(findings[0], "payments")]);
    await push([grouped(findings[1])]);
}

// The access-on Hub: sign-in against the fake provider, a local account (which puts
// the Hub's own sign-in page in front), projects, grants, a machine token, the trusted
// proxy network terminal sign-in needs and the metrics port access control needs.
async function startAccessHub(engine: string, children: ChildProcess[]): Promise<void> {
    children.push(spawn(process.execPath, [join(__dirname, "demo", "fake-provider.js"), String(PROVIDER_PORT)],
        {stdio: "ignore", detached: true}));
    const provider = `http://127.0.0.1:${PROVIDER_PORT}`;
    const machineToken = randomBytes(30).toString("base64url");
    const importKey = randomBytes(30).toString("base64url");
    const daemonSource = (id: string, name: string, environment: string, port: number) =>
        ({Id: id, Name: name, Environment: environment, Kind: "daemon", BaseUrl: `http://127.0.0.1:${port}`});
    const hub = {
        DatabasePath: join(WORK, "access", "hub.db"),
        UpdateCheck: {Enabled: false},
        Analysis: {EngineBinaryPath: engine, ReportDirectory: join(WORK, "access", "reports")},
        MetricsPort: ACCESS_METRICS_PORT,
        Sources: [
            daemonSource("checkout-prod", "Checkout production", "production", BUSY_PORT),
            daemonSource("billing-stg", "Billing staging", "staging", CALM_PORT),
            {...daemonSource("shop-prod", "Shop production", "production", PUSHED_PORT), ImportApiKey: importKey},
            {Id: "tempo-eu", Name: "Tempo EU", Environment: "production", Kind: "tempo",
                BaseUrl: "http://127.0.0.1:41402", RetentionHours: 168}
        ],
        Auth: {
            Enabled: true,
            AuthorizationEndpoint: `${provider}/authorize`,
            TokenEndpoint: `${provider}/token`,
            UserInformationEndpoint: `${provider}/userinfo`,
            ClientId: "perf-sentinel-hub",
            ClientSecret: "demo",
            // No proxy in front: the Hub's own peer is the client.
            TrustedProxyNetworks: ["127.0.0.1/32"]
        },
        Access: {
            Enabled: true,
            AdminGroups: ["/hub-admins", "internal:break-glass"],
            Groups: [
                {Name: "break-glass", Members: ["local:ops"]},
                {Name: "ci-orders", Members: []}
            ],
            LocalAccounts: [{Name: "ops", PasswordHash: localPasswordHash()}],
            Projects: [
                {Name: "checkout", Patterns: [{Source: "checkout-prod"}]},
                {Name: "orders", Patterns: [{Service: "order*"}]},
                {Name: "shop", Patterns: [{Attribute: "team=shop"}]}
            ],
            Roles: [{Name: "ci-reader", Permissions: ["findings.read"]}],
            Grants: [
                {Group: "/teams/shop", Project: "shop", Role: "member"},
                {Group: "/teams/shop", Project: "checkout", Role: "reader"},
                {Group: "/teams/orders", Project: "orders", Role: "member"},
                {Group: "internal:ci-orders", Project: "orders", Role: "ci-reader"}
            ],
            MachineTokens: [{
                Name: "ci-orders", Group: "internal:ci-orders", Token: machineToken,
                ExpiresAt: new Date(Date.now() + 90 * 86_400_000).toISOString()
            }]
        }
    };
    mkdirSync(join(WORK, "access", "reports"), {recursive: true});
    // Detached and signalled as a group at teardown, like the first Hub. Both ports on
    // loopback: the metrics port must be bound beside the main one or the Hub stops.
    children.push(spawn(DOTNET,
        ["run", "--project", join(ROOT, "PerfSentinelHub", "PerfSentinelHub.csproj"),
            "-c", "Release", "--no-build", "--no-launch-profile"],
        {
            stdio: "ignore",
            detached: true,
            env: {
                ...dotnetEnv(),
                ASPNETCORE_URLS: `${ACCESS_BASE};http://127.0.0.1:${ACCESS_METRICS_PORT}`,
                ASPNETCORE_ENVIRONMENT: "Production",
                ...hubEnv("Hub", hub)
            }
        }));
    await waitFor(`${ACCESS_BASE}/health/ready`, 90);
    await pushGroupedFindings(importKey);

    // The admin's access page counts what the two polled daemons hold, so it waits for
    // both first polls. The machine token reads order* services, which both carry.
    await Promise.all(["checkout-prod", "billing-stg"].map((source) => poll(async () => {
        const response = await fetch(`${ACCESS_BASE}/api/findings?source_id=${source}&limit=1`,
            {headers: {authorization: `Bearer ${machineToken}`}});
        return response.ok && (await response.json() as unknown[]).length > 0 || undefined;
    }, Date.now() + 30_000, 300, `the access Hub never polled ${source}`)));
}

async function startAll(children: ChildProcess[]): Promise<void> {
    const engine = engineBinary();
    rmSync(WORK, {recursive: true, force: true});
    mkdirSync(join(WORK, "reports"), {recursive: true});

    // Detached like the Hub: an interrupted run must not leave these holding their
    // ports, or the next run's Hub binds against stale daemons and the capture
    // reads the previous run's data.
    const daemon = (port: number, variant: string) =>
        children.push(spawn(process.execPath, [join(__dirname, "demo", "fake-daemon.js"),
                String(port), variant],
            {stdio: "ignore", detached: true}));
    daemon(BUSY_PORT, "busy");
    daemon(CALM_PORT, "calm");
    daemon(ACKED_PORT, "acked");
    daemon(BASELINE_PORT, "baseline");
    await Promise.all([BUSY_PORT, CALM_PORT, ACKED_PORT, BASELINE_PORT]
        .map((port) => waitFor(`http://127.0.0.1:${port}/api/status`, 15)));

    const build = spawnSync(DOTNET,
        ["build", join(ROOT, "PerfSentinelHub", "PerfSentinelHub.csproj"), "-c", "Release", "--nologo"],
        {env: dotnetEnv(), stdio: "inherit"});
    if (build.status !== 0) throw new Error("the Hub did not build");

    const source = (i: number, id: string, name: string, environment: string,
                    kind: string, port: number, extra: Record<string, string> = {}) => {
        const out: Record<string, string> = {
            [`Hub__Sources__${i}__Id`]: id,
            [`Hub__Sources__${i}__Name`]: name,
            [`Hub__Sources__${i}__Environment`]: environment,
            [`Hub__Sources__${i}__Kind`]: kind,
            [`Hub__Sources__${i}__BaseUrl`]: `http://127.0.0.1:${port}`
        };
        for (const [k, v] of Object.entries(extra)) out[`Hub__Sources__${i}__${k}`] = v;
        return out;
    };

    // The daemon's own [daemon.ack] api_key. The fake daemons check nothing, but
    // the Hub publishes ack_relay from the credential being there at all, and the
    // ack page draws a row from that.
    const ack = (name: string) => ({
        AckHeaderName: "X-API-Key",
        AckHeaderValue: `demo-ack-key-${name}`
    });

    // The built binary rather than `dotnet run`: the latter spawns the app as a
    // grandchild that outlives a SIGTERM to its parent, and a Hub left holding
    // the port makes the next run read the previous run's data.
    // `dotnet run` rather than the built binary: the build output holds a static
    // web asset manifest, not wwwroot itself, so the binary alone serves the API
    // and no page. Detached, because dotnet run puts the Hub in a grandchild that
    // survives a signal to its parent, and a Hub left holding the port makes the
    // next run read the previous run's data. Teardown signals the whole group.
    const hub = spawn(DOTNET,
        ["run", "--project", join(ROOT, "PerfSentinelHub", "PerfSentinelHub.csproj"),
            "-c", "Release", "--no-build", "--no-launch-profile"],
        {
            stdio: "ignore",
            detached: true,
            env: {
                ...dotnetEnv(),
                ASPNETCORE_URLS: BASE,
                ASPNETCORE_ENVIRONMENT: "Production",
                Hub__DatabasePath: join(WORK, "hub.db"),
                // No screenshot should depend on GitHub being reachable.
                Hub__UpdateCheck__Enabled: "false",
                Hub__Analysis__EngineBinaryPath: engine,
                Hub__Analysis__ReportDirectory: join(WORK, "reports"),
                // The ack credential is what makes a source relay, and it is deliberately
                // not on every daemon: Billing staging has none, so the ack page has a row
                // that says the Hub holds no write path to it.
                ...source(0, "checkout-prod", "Checkout production", "production", "daemon", BUSY_PORT,
                    ack("checkout")),
                ...source(1, "billing-stg", "Billing staging", "staging", "daemon", CALM_PORT),
                ...source(2, "tempo-eu", "Tempo EU", "production", "tempo", 41402,
                    {RetentionHours: "168"}),
                ...source(3, "search-prod", "Search production", "production", "daemon", DEAD_PORT),
                // Victoria Traces speaks the Jaeger query API, which is the kind the
                // engine reads it with. Two backends rather than one so the group the
                // list draws holds more than a single row.
                ...source(4, "victoria-eu", "Victoria Traces EU", "staging", "jaeger_query", 41404,
                    {RetentionHours: "72"}),
                // Production, like Checkout, so a link that names the environment and no
                // source ticks the two of them and both buttons of the ack page apply.
                ...source(5, "orders-prod", "Orders production", "production", "daemon", ACKED_PORT,
                    ack("orders")),
                ...source(6, "ci-main", "CI baseline", "ci", "daemon", BASELINE_PORT, ack("ci"))
            }
        });
    children.push(hub);
    await waitFor(`${BASE}/health/ready`, 90);
    await startAccessHub(engine, children);

    // Four runs, chosen for the states they end in rather than for variety:
    // two that succeed, one refused by a dead source, one refused before it is
    // ever queued because the window outruns what the backend keeps.
    const succeeded = await submit("checkout-prod", {});
    const alsoSucceeded = await submit("billing-stg", {});
    const unreachable = await submit("search-prod", {});
    const refused = await submit("tempo-eu",
        {service: "checkout", lookback: "999h", max_traces: 100});
    await Promise.all([succeeded, alsoSucceeded, unreachable, refused].map((id) => settle(id, 120)));

    try {
        await captureEmbedFixtures([succeeded, alsoSucceeded, unreachable, refused]);
    } catch (error) {
        // The embed fixture is consumed by the website repository, not by this
        // suite. A capture that fails leaves the previous one in place and must not
        // stop the screenshots this suite exists to produce.
        console.error("hub-embed.json was not refreshed:", error);
    }

    writeFileSync(join(WORK, "state.json"), JSON.stringify({
        baseUrl: BASE,
        succeeded,
        ackSignature: ackSignature(),
        accessBaseUrl: ACCESS_BASE,
        pids: children.map((c) => c.pid).filter((p): p is number => typeof p === "number"),
        hubGroup: hub.pid
    }, null, 2));
}

export default async function globalSetup(): Promise<void> {
    const children: ChildProcess[] = [];
    try {
        await startAll(children);
    } catch (error) {
        // Teardown finds the pids in state.json, which a failed setup never wrote, so
        // whatever was already spawned is stopped here, the way teardown would.
        for (const child of children) {
            if (child.pid === undefined) continue;
            try { process.kill(-child.pid, "SIGTERM"); } catch { /* not a group leader */ }
            try { process.kill(child.pid, "SIGTERM"); } catch { /* already gone */ }
        }
        throw error;
    }
}

export {WORK};
