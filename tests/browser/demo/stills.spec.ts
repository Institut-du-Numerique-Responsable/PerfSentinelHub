import {expect, test} from "@playwright/test";
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
    await page.getByRole("button", {name: "Analyse this window"}).first().click();
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
