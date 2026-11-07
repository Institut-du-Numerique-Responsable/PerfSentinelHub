import {expect, Page, test} from "@playwright/test";
import {state} from "./capture";

// The guides of Access and Coverage, walked on the access-on Hub global-setup stands up: Ada is in an
// admin group, Sam in two team groups that grants name, Noor in a group no grant names. Assertions, not
// captures, like guide.spec.ts.

const card = (page: Page) => page.locator(".guide-card");
const title = (page: Page) => page.locator("#guide-title");
const next = (page: Page) => card(page).locator(".guide-nav .pill-primary");
const count = (page: Page) => card(page).locator(".guide-count");

async function signIn(page: Page, email: string): Promise<void> {
    await page.goto("/");
    await page.getByRole("link", {name: "Sign in with single sign-on"}).click();
    await page.getByRole("button", {name: email}).click();
    await page.waitForURL((url) => url.origin === state.accessBaseUrl && !url.pathname.startsWith("/auth"));
}

async function openOn(page: Page, route: string, ready: string): Promise<void> {
    await page.goto(route);
    await expect(page.locator(ready).first()).toBeVisible({timeout: 30_000});
    await page.locator("#guide-open").click();
    await expect(card(page)).toBeVisible();
}

/** Every title of the walk, from the first card to the last, then Close. */
async function walkTitles(page: Page): Promise<string[]> {
    const total = Number((await count(page).textContent() ?? "").match(/(\d+)\D*$/)?.[1]);
    const titles: string[] = [];
    for (let index = 0; index < total; index += 1) {
        if (index > 0) await next(page).click();
        await expect(count(page)).toHaveText(new RegExp(`^${index + 1} \\D+ ${total}$`));
        titles.push(await title(page).textContent() ?? "");
    }
    await expect(next(page)).toHaveText("Close");
    await next(page).click();
    await expect(page.locator("#guide-root")).toBeHidden();
    return titles;
}

test.beforeEach(async ({page}) => {
    await page.addInitScript(() => localStorage.setItem("perf-sentinel:lang", "en"));
});

test("a member's Access walk, also where a shared Coverage link lands", async ({page}) => {
    await signIn(page, "sam@example.com");
    for (const route of ["/#/access", "/#/coverage"]) {
        await openOn(page, route, '[data-guide="access.tokens"]');
        expect(await walkTitles(page), route).toEqual(["Your access", "Who you are", "Your groups", "What you reach",
            "Your terminal tokens", "The projects you reach", "You know what you see here"]);
    }
});

test("an admin's Access walk points to Coverage, and the Coverage walk reads every card", async ({page}) => {
    await signIn(page, "ada@example.com");
    await openOn(page, "/#/access", '[data-guide="access.admin"]');
    expect(await walkTitles(page)).toEqual(["Your access", "Who you are", "Your groups", "What you reach",
        "Your terminal tokens", "You administer this Hub", "You know what you see here"]);
    await openOn(page, "/#/coverage", '[data-guide="coverage.tokens"]');
    expect(await walkTitles(page)).toEqual(["What access control covers", "Needs attention", "Patterns to paste",
        "Deployments that disagree", "Everyone's tokens", "The configuration", "You know what is missing"]);
});

test("a caller no grant reaches gets the guide of their access, without the reach", async ({page}) => {
    await signIn(page, "noor@example.com");
    await page.goto("/#/sources");
    await expect(page.locator("#main")).toContainText("No grant on this Hub reaches you", {timeout: 15_000});
    await expect(page.locator("#guide-open")).toBeVisible();
    await openOn(page, "/#/sources", '[data-guide="access.tokens"]');
    expect(await walkTitles(page)).toEqual(["Your access", "Who you are", "Your groups", "Your terminal tokens",
        "You know what you see here"]);
});
