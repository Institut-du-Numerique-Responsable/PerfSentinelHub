import {expect, Page, test} from "@playwright/test";
import {nameFor, settled, state} from "./capture";

// The screens that exist only with sign-in and access control on, captured on the
// second Hub global-setup stands up. Each test signs in afresh, since each gets a
// browser context of its own: Ada is in an admin group, Sam in two team groups that
// grants name, Noor in a group no grant names.

const DEVICE_CLIENT = "perf-sentinel";

/** Through the Hub's own sign-in page, then the fake provider's account chooser. */
async function signIn(page: Page, email: string): Promise<void> {
    await page.goto("/");
    await page.getByRole("link", {name: "Sign in with single sign-on"}).click();
    await page.getByRole("button", {name: email}).click();
    await page.waitForURL((url) => url.origin === state.accessBaseUrl && !url.pathname.startsWith("/auth"));
}

/** What `perf-sentinel login` sends first: a form post from a terminal, never a browser. */
async function terminal(path: string, form: Record<string, string>): Promise<Record<string, string>> {
    const response = await fetch(state.accessBaseUrl + path, {
        method: "POST",
        headers: {"content-type": "application/x-www-form-urlencoded"},
        body: new URLSearchParams({client_id: DEVICE_CLIENT, ...form})
    });
    return await response.json() as Record<string, string>;
}

/** The approval page with a pending code typed in and verified, before any decision. */
async function enterPendingCode(page: Page): Promise<string> {
    const start = await terminal("/auth/device", {});
    expect(start.user_code, JSON.stringify(start)).toBeTruthy();
    await page.goto("/device");
    await page.locator("#device-code").fill(start.user_code);
    await page.getByRole("button", {name: "Continue"}).click();
    await expect(page.locator("#device-request")).not.toBeEmpty();
    return start.device_code;
}

// The token table needs a token, and only the device flow makes one. Once per person
// for the whole run, so the dark and the light capture list the same rows.
async function ensureTerminalToken(page: Page, email: string): Promise<void> {
    // From the page: the session cookie is Secure, which Playwright's own request client
    // withholds over plain http even on loopback, where the browser sends it.
    const tokens = await page.evaluate(async () => (await fetch("/api/tokens")).json()) as { identity: string }[];
    if (tokens.some((token) => token.identity === email)) return;
    const deviceCode = await enterPendingCode(page);
    await page.locator("#device-approve").click();
    await expect(page.locator("#device-result")).toContainText("Approved");
    const issued = await terminal("/auth/device/token",
        {grant_type: "urn:ietf:params:oauth:grant-type:device_code", device_code: deviceCode});
    expect(issued.access_token, JSON.stringify(issued)).toMatch(/^pshub_/);
}

async function accessPage(page: Page): Promise<void> {
    await page.goto("/#/access");
    await settled(page, "Your access");
    await expect(page.locator(".access-table").first()).toBeVisible();
}

test("the sign-in page", async ({page}, info) => {
    await page.goto("/");
    await expect(page).toHaveURL(/\/auth\/sign-in\?/);
    // Opened, so the shot shows both ways in: single sign-on, and the break-glass form.
    await page.getByText("Sign in with a local account").click();
    await expect(page.locator("#name")).toBeVisible();
    await page.screenshot({path: nameFor("hub-sign-in", info.project.name), fullPage: true, animations: "disabled"});
});

test("approving a terminal", async ({page}, info) => {
    await signIn(page, "sam@example.com");
    await enterPendingCode(page);
    // The pointer still rests on Continue after the click, which would photograph hovered.
    await page.mouse.move(0, 0);
    // Left pending: the shot is the decision, and a denial or an approval would end it.
    await page.screenshot({path: nameFor("hub-device", info.project.name), fullPage: true, animations: "disabled"});
});

test("the access page of a member", async ({page}, info) => {
    await signIn(page, "sam@example.com");
    await ensureTerminalToken(page, "sam@example.com");
    await accessPage(page);
    await expect(page.locator("#main")).toContainText("your terminal tokens");
    await page.screenshot({
        path: nameFor("launcher-access-member", info.project.name),
        fullPage: true,
        animations: "disabled"
    });
});

test("the access page of an admin", async ({page}, info) => {
    await signIn(page, "ada@example.com");
    await ensureTerminalToken(page, "ada@example.com");
    await accessPage(page);
    // The admin half: every token, the configuration, and what no project covers with
    // the conflicts the pushed groupings make.
    await expect(page.locator("#main")).toContainText("terminal tokens, everyone's");
    await expect(page.locator("#main")).toContainText("// configuration");
    await expect(page.locator("#main")).toContainText("team=payments");
    await expect(page.locator(".access-table", {hasText: "Groupings without the key"})).toBeVisible();
    await page.screenshot({path: nameFor("launcher-access", info.project.name), fullPage: true, animations: "disabled"});
});

test("a caller no grant reaches", async ({page}, info) => {
    await signIn(page, "noor@example.com");
    // On the access route, so the tab bar highlights Access above the access page.
    await page.goto("/#/access");
    await settled(page, "No grant on this Hub reaches you");
    await expect(page.locator("#main")).toContainText("Your access");
    await page.screenshot({
        path: nameFor("launcher-no-grant", info.project.name),
        fullPage: true,
        animations: "disabled"
    });
});
