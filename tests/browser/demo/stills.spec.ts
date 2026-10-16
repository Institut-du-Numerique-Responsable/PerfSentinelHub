import {expect, Page, test} from "@playwright/test";
import {nameFor, settled, state} from "./capture";

// One still per screen, in both themes. The light file keeps the bare name
// because it fills the <img src> slot of a <picture>; the dark one takes the
// -dark suffix the engine's docs already use.

test("run an analysis", async ({page}, info) => {
    await page.goto("/#/new");
    await settled(page, "Run an analysis");
    // The trace-backend form is the full one: service, window, trace cap. A
    // daemon takes no parameters at all, so it would show an empty form.
    await page.locator("button.source-row", {hasText: "Tempo EU"}).click();
    // The service field exists only for a trace backend, so its arrival is the
    // signal that the form has swapped.
    await expect(page.locator('input[placeholder="order-service"]')).toBeVisible();
    await page.screenshot({path: nameFor("launcher-new", info.project.name), fullPage: true, animations: "disabled"});
});

test("recent runs", async ({page}, info) => {
    await page.goto("/#/recent");
    await settled(page, "The team's short memory");
    await page.screenshot({
        path: nameFor("launcher-recent", info.project.name),
        fullPage: true,
        animations: "disabled"
    });
});

test("fleet health", async ({page}, info) => {
    await page.goto("/#/sources");
    await settled(page, "Fleet health");
    // A folded row shows none of the gauges, which are the point of the screen.
    await page.locator("button.row-toggle", {hasText: "Checkout production"}).click();
    // The gauges are what the daemon read produces, and they are the point of the
    // screen, so waiting for the first one waits for the whole panel.
    await expect(page.locator(".daemon-panel .count").first()).toBeVisible({timeout: 15_000});
    await page.screenshot({
        path: nameFor("launcher-sources", info.project.name),
        fullPage: true,
        animations: "disabled"
    });
});

test("incidents", async ({page}, info) => {
    await page.goto("/#/incidents");
    await settled(page, "What was already burning");
    // A folded row shows none of the findings the daemon froze, which are the
    // point of the screen.
    await page.locator("button.row-toggle").first().click();
    await expect(page.locator(".daemon-panel .table").first()).toBeVisible({timeout: 15_000});
    await page.screenshot({
        path: nameFor("launcher-incidents", info.project.name),
        fullPage: true,
        animations: "disabled"
    });
});

test("the window handed to a new analysis", async ({page}, info) => {
    await page.goto("/#/incidents");
    await settled(page, "What was already burning");
    // Named on purpose: this row carries both a kind the banner can say and a
    // namespace, which is the pair the sentence is built for.
    await page.locator("button.row-toggle", {hasText: "checkout-svc"}).click();
    await expect(page.locator(".daemon-panel .table").first()).toBeVisible({timeout: 15_000});
    await page.getByRole("link", {name: "Analyse this window"}).first().click();
    await expect(page).toHaveURL(/#\/new\?from=\d+&to=\d+&service=[^&]+&incident=[0-9a-f]{32}$/);
    await expect(page.locator(".banner", {hasText: "from the incidents screen"})).toBeVisible();
    // A daemon takes no window, so the shot is the form that can run it. The
    // service the link carried has to survive the source switch, which is the
    // half of the prefill a still cannot show on its own.
    await page.locator("button.source-row", {hasText: "Tempo EU"}).click();
    await expect(page.locator('input[placeholder="order-service"]')).toHaveValue(/\S/);
    await page.screenshot({
        path: nameFor("launcher-handoff", info.project.name),
        fullPage: true,
        animations: "disabled"
    });
});

test("acknowledging a finding", async ({page}, info) => {
    // The address a Grafana link carries, a query and not a hash, which the
    // launcher turns into the route on load. Named by environment and by no
    // source, so the two production daemons start checked and both buttons apply:
    // one of them can take an ack, the other already holds one.
    await page.goto(`/?ack=${encodeURIComponent(state.ackSignature)}&environment=production`);
    await settled(page, "Acknowledge a finding");
    // Four daemons carry this finding and no two rows say the same thing. The
    // count alone cannot fail the way this screen really breaks: an ack read that
    // has not landed leaves every relaying source in the unknown branch, which
    // keeps four rows and prints one sentence three times. So each row is read
    // for the case it is here to show.
    const rows = page.locator(".ack-table tbody tr");
    await expect(rows).toHaveCount(4);
    await expect(rows.filter({hasText: "Checkout production"})).toContainText("Acknowledge.");
    await expect(rows.filter({hasText: "Orders production"})).toContainText("Revoke this ack.");
    await expect(rows.filter({hasText: "CI baseline"})).toContainText("Acknowledged by the CI baseline");
    await expect(rows.filter({hasText: "Billing staging"}))
        .toContainText("The Hub holds no ack credential");
    await page.locator("#ack-reason").fill("Known, the batched query ships with the order service.");
    // A month out, computed rather than written: the field refuses a day already
    // over, and an empty form would photograph its blocker instead of its plan.
    await page.locator("#ack-expiry").fill(new Date(Date.now() + 30 * 86_400_000).toISOString().slice(0, 10));
    // Left focused, a date field photographs with one of its parts selected, which
    // reads as a field being edited rather than as a form that is ready.
    await page.locator("#ack-expiry").blur();
    await expect(page.locator("#ack-sentence")).toContainText("Acknowledge writes to");
    await page.screenshot({path: nameFor("launcher-ack", info.project.name), fullPage: true, animations: "disabled"});
});

test("one run", async ({page}, info) => {
    await page.goto(`/#/run/${state.succeeded}`);
    await settled(page, /succeeded/i);
    await page.screenshot({path: nameFor("launcher-run", info.project.name), fullPage: true, animations: "disabled"});
});

test("the rendered report", async ({page}, info) => {
    await page.goto(`/#/report/${state.succeeded}`);
    // The dashboard is an iframe on the same origin. Waiting on its content
    // rather than on a delay, because a blank frame screenshots just as well.
    await expect(page.frameLocator("iframe").locator("body"))
        .toContainText("Findings", {timeout: 30_000});
    await page.screenshot({path: nameFor("launcher-report", info.project.name), animations: "disabled"});
});

// The pair whose episodes run through the busy daemon's incidents, unfolded.
async function openPair(page: Page): Promise<void> {
    await page.goto("/#/correlations");
    await settled(page, "Findings that keep following each other");
    await page.locator("button.row-toggle", {hasText: "orders-svc"}).click();
    await expect(page.locator(".daemon-detail:not([hidden]) .timeline-strip")).toBeVisible();
}

test("correlations", async ({page}, info) => {
    await openPair(page);
    const panel = page.locator(".daemon-detail:not([hidden]) .daemon-panel");
    await expect(panel.locator("rect.timeline-incident").first()).toBeAttached();
    await expect(panel.locator("tbody tr")).toHaveCount(3);
    await expect(panel.locator("a.chip[data-kind]").first()).toBeVisible();
    await expect(panel.getByRole("link", {name: "Explain the last pair"}).first()).toBeVisible();
    await page.screenshot({
        path: nameFor("launcher-correlations", info.project.name),
        fullPage: true,
        animations: "disabled"
    });
});

test("correlations hand an episode's window to a new analysis", async ({page}) => {
    await openPair(page);
    await page.getByRole("link", {name: "Analyse this window"}).first().click();
    await expect(page).toHaveURL(/#\/new\?from=\d+&to=\d+&service=order-svc&pair=[0-9a-f]{32}$/);
    await expect(page.locator(".banner", {hasText: "from the correlations screen"})).toBeVisible();
});

test("correlations hand the last pair's traces to a new analysis", async ({page}) => {
    await openPair(page);
    await page.getByRole("link", {name: "Explain the last pair"}).first().click();
    await expect(page).toHaveURL(/#\/new\?trace=[0-9a-f]{32}&other=[0-9a-f]{32}$/);
    await page.locator("button.source-row", {hasText: "Tempo EU"}).click();
    const trace = page.locator('input[placeholder="4bf92f3577b34da6a3ce929d0e0e4736"]');
    const first = await trace.inputValue();
    await page.getByRole("button", {name: "Use the other trace"}).click();
    await expect(trace).not.toHaveValue(first);
});

test("correlations keep one banner from a window to a trace", async ({page}) => {
    await openPair(page);
    await page.getByRole("link", {name: "Analyse this window"}).first().click();
    await expect(page.locator(".banner", {hasText: "from the correlations screen"})).toBeVisible();
    await page.goBack();
    await expect(page.locator(".daemon-detail:not([hidden]) .timeline-strip")).toBeVisible();
    await page.getByRole("link", {name: "Explain the last pair"}).first().click();
    await expect(page.locator(".banner", {hasText: "A trace of the last co-occurrence"})).toBeVisible();
    await expect(page.locator(".banner", {hasText: "Window of"})).toHaveCount(0);
});

test("correlations open on the pair a link names", async ({page}) => {
    await openPair(page);
    const pairKey = await page.evaluate(() => fetch("/api/correlations?service=orders-svc&limit=1")
        .then((response) => response.json()).then((rows) => rows[0].pair_key as string));
    await page.goto(`/#/correlations?pair_key=${pairKey}`);
    await settled(page, "Findings that keep following each other");
    await expect(page.locator("button.row-toggle")).toHaveCount(1);
    await expect(page.locator("button.row-toggle")).toHaveAttribute("aria-expanded", "true");
    // Back on every pair, over the 180 days the link widened to: three pairs on two daemons.
    await page.getByRole("link", {name: "Show every pair"}).click();
    await expect(page.locator("button.row-toggle")).toHaveCount(3);
});

test("correlations on a phone", async ({page}, info) => {
    await page.setViewportSize({width: 390, height: 844});
    await openPair(page);
    // The tables scroll inside their frame. The page itself must not.
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
    await page.screenshot({
        path: nameFor("launcher-correlations-phone", info.project.name),
        fullPage: true,
        animations: "disabled"
    });
});

test("correlations during an incident", async ({page}, info) => {
    await page.goto("/#/incidents");
    await settled(page, "What was already burning");
    await page.locator("button.row-toggle", {hasText: "checkout-svc"}).click();
    const block = page.locator(".daemon-detail:not([hidden]) .incident-correlations");
    await expect(block.locator("tbody tr")).toHaveCount(1, {timeout: 15_000});
    await expect(block.getByRole("link", {name: "All its episodes"})).toBeVisible();
    await page.screenshot({
        path: nameFor("launcher-incident-correlations", info.project.name),
        fullPage: true,
        animations: "disabled"
    });
});

test("correlations an incident cannot show", async ({page}) => {
    await page.goto("/#/incidents");
    await settled(page, "What was already burning");
    await page.locator("button.row-toggle", {hasText: "search-svc"}).click();
    await expect(page.locator(".incident-correlations", {hasText: "No correlation was active in this window."}))
        .toBeVisible({timeout: 15_000});
    await page.locator("button.row-toggle", {hasText: "cache-svc"}).click();
    await expect(page.locator(".incident-correlations",
        {hasText: "Billing staging pushes no correlation you can read, so none can show here."})).toBeVisible({timeout: 15_000});
});

test("correlations open every episode of an incident's pair", async ({page}) => {
    await page.goto("/#/incidents");
    await settled(page, "What was already burning");
    await page.locator("button.row-toggle", {hasText: "checkout-svc"}).click();
    await page.locator(".daemon-detail:not([hidden]) .incident-correlations")
        .getByRole("link", {name: "All its episodes"}).click();
    await expect(page).toHaveURL(/#\/correlations\?pair_key=[0-9a-f]{32}$/);
    await settled(page, "Findings that keep following each other");
    await expect(page.locator("button.row-toggle", {hasText: "orders-svc"})).toHaveAttribute("aria-expanded", "true");
});
