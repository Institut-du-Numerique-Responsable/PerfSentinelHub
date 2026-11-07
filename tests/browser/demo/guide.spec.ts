import {expect, Page, Request, Route, test} from "@playwright/test";
import {state} from "./capture";

// The pilotage guide walked on the real recap of the demo Hub. Assertions, not captures: nothing
// here writes to docs/img, and `npm run demo` does not run it (see README).

const RECAP = "/#/recap?view=pilotage";
const LANG_KEY = "perf-sentinel:lang";

const card = (page: Page) => page.locator(".guide-card");
const title = (page: Page) => page.locator("#guide-title");
const next = (page: Page) => card(page).locator(".guide-nav .pill-primary");
const count = (page: Page) => card(page).locator(".guide-count");

/** The entry opens its menu, whose first choice is the guide of the screen on view. */
async function screenGuide(page: Page): Promise<void> {
    await page.locator("#guide-open").click();
    await page.locator("#guide-menu-screen").click();
}

async function useLang(page: Page, lang: "en" | "fr"): Promise<void> {
    await page.addInitScript(([key, value]) => localStorage.setItem(key, value), [LANG_KEY, lang]);
}

/** The recap in its management view, drawn, then the guide opened on it. */
async function openGuide(page: Page): Promise<void> {
    await page.goto(RECAP);
    await expect(page.locator(".recap-row").first()).toBeVisible({timeout: 60_000});
    await screenGuide(page);
    await expect(card(page)).toBeVisible();
}

/** Until the smooth scroll to the step has landed: the cutout and the marks follow it. */
async function settle(page: Page): Promise<void> {
    await expect.poll(() => page.evaluate(() => new Promise<boolean>((done) => {
        const at = window.scrollY;
        setTimeout(() => requestAnimationFrame(() => done(window.scrollY === at)), 150);
    }))).toBe(true);
}

/** Whether the cutout frames exactly these anchors, with its 7 px margin, after the scroll landed. */
async function framed(page: Page, anchors: string[]): Promise<boolean> {
    await settle(page);
    return page.evaluate((names) => {
        const rects = names.map((name) => document.querySelector(`[data-guide="${name}"]`)!.getBoundingClientRect());
        const hole = document.querySelector(".guide-hole")!.getBoundingClientRect();
        const near = (a: number, b: number) => Math.abs(a - b) <= 2;
        return near(hole.left, Math.min(...rects.map((r) => r.left)) - 7) &&
            near(hole.top, Math.min(...rects.map((r) => r.top)) - 7) &&
            near(hole.right, Math.max(...rects.map((r) => r.right)) + 7) &&
            near(hole.bottom, Math.max(...rects.map((r) => r.bottom)) + 7);
    }, anchors);
}

/** Next until the step that frames `anchors`, whatever its title says. */
async function goToAnchors(page: Page, anchors: string[]): Promise<void> {
    const total = await totalSteps(page);
    for (let step = 0; step < total; step += 1) {
        if (await framed(page, anchors)) return;
        await next(page).click();
    }
    throw new Error(`no step frames ${anchors.join(", ")}`);
}

async function totalSteps(page: Page): Promise<number> {
    const text = await count(page).textContent() ?? "";
    return Number(text.match(/(\d+)\D*$/)?.[1]);
}

/** Next until the card's title is `wanted`. */
async function goTo(page: Page, wanted: string): Promise<void> {
    const total = await totalSteps(page);
    for (let step = 0; step < total; step += 1) {
        if (await title(page).textContent() === wanted) return;
        await next(page).click();
    }
    throw new Error(`no step titled ${wanted}`);
}

/** Calls `check` on every step, from the first to the last. */
async function eachStep(page: Page, check: (index: number) => Promise<void>): Promise<void> {
    const total = await totalSteps(page);
    for (let index = 0; index < total; index += 1) {
        if (index > 0) await next(page).click();
        await expect(count(page)).toHaveText(new RegExp(`^${index + 1} \\D+ ${total}$`));
        await check(index);
    }
}

async function focusInCard(page: Page): Promise<boolean> {
    return page.evaluate(() => {
        const active = document.activeElement as HTMLElement | null;
        return Boolean(active && document.querySelector(".guide-card")?.contains(active) && !active.hidden);
    });
}

async function expectInWindow(page: Page, selector: string): Promise<void> {
    const size = page.viewportSize()!;
    for (const box of await page.locator(selector).evaluateAll((nodes) =>
        nodes.map((node) => node.getBoundingClientRect().toJSON()))) {
        expect(box.left, selector).toBeGreaterThanOrEqual(0);
        expect(box.top, selector).toBeGreaterThanOrEqual(0);
        expect(box.right, selector).toBeLessThanOrEqual(size.width);
        expect(box.bottom, selector).toBeLessThanOrEqual(size.height);
    }
}

test.beforeEach(async ({page}) => {
    await useLang(page, "en");
});

test("the walk reaches every step and returns the focus", async ({page}) => {
    await openGuide(page);
    await expect(page.locator("#main")).toHaveAttribute("inert", "");
    const total = await totalSteps(page);
    expect(total).toBeGreaterThan(5);
    const titles: string[] = [];
    await eachStep(page, async (index) => {
        titles.push(await title(page).textContent() ?? "");
        await expect(next(page)).toHaveText(index === total - 1 ? "Close" : "Next");
    });
    expect(titles[0]).toBe("You are in the Management view");
    expect(titles).toContain("How to read a row");
    expect(titles[total - 1]).toBe("You can now answer three questions");
    await next(page).click();
    await expect(page.locator("#guide-root")).toBeHidden();
    await expect(page.locator("#guide-open")).toBeFocused();
    await expect(page.locator("#main")).not.toHaveAttribute("inert", "");
});

test("the row step numbers its marks like its legend", async ({page}) => {
    await openGuide(page);
    await goTo(page, "How to read a row");
    const legend = await card(page).locator(".guide-legend li").count();
    expect(legend).toBeGreaterThanOrEqual(5);
    await expect(page.locator(".guide-marks .guide-mark")).toHaveCount(legend);
    await settle(page);
    await expectInWindow(page, ".guide-marks .guide-mark");
});

test("a redraw keeps the step and the focus in the card", async ({page}) => {
    await openGuide(page);
    await goTo(page, "How to read a row");
    expect(await framed(page, ["recap.row"])).toBe(true);
    const where = await count(page).textContent();
    await next(page).focus();
    // The old row is tagged, and #main itself (which a render keeps) shifts what it redraws down:
    // a guide that did not follow the render would frame the row where it was.
    await page.evaluate(() => {
        (document.querySelector('[data-guide="recap.row"]') as HTMLElement).dataset.stale = "1";
        (document.querySelector("#main") as HTMLElement).style.paddingTop = "40px";
    });
    // What a poll or a return to the screen does: the route is read again and #main redrawn.
    await page.evaluate(() => window.dispatchEvent(new HashChangeEvent("hashchange")));
    await expect(page.locator('[data-guide="recap.row"][data-stale]')).toHaveCount(0, {timeout: 60_000});
    await expect(page.locator('[data-guide="recap.row"]')).toBeVisible();
    await expect(title(page)).toHaveText("How to read a row");
    await expect(count(page)).toHaveText(where!);
    expect(await framed(page, ["recap.row"])).toBe(true);
    // Each mark sits on its part of the new row, in the legend's order.
    const parts = ["rank", "what", "where", "figure", "trend", "state"].map((part) => `recap.row.${part}`);
    const offsets = await page.evaluate((names) => {
        const marks = Array.from(document.querySelectorAll(".guide-marks .guide-mark"));
        const found = names.map((name) => document.querySelector(`[data-guide="${name}"]`)).filter(Boolean);
        return {
            marks: marks.length, parts: found.length,
            gaps: marks.map((mark, index) => mark.getBoundingClientRect().top - found[index]!.getBoundingClientRect().top)
        };
    }, parts);
    expect(offsets.marks).toBe(offsets.parts);
    for (const gap of offsets.gaps) expect(Math.abs(gap + 1)).toBeLessThanOrEqual(1);
    expect(await focusInCard(page)).toBe(true);
});

/** Holds the recap job running, as a computation of several minutes would, until `release`. */
async function holdRecapJob(page: Page): Promise<{ release: () => void, posts: () => number }> {
    let held = true;
    let posts = 0;
    await page.route((url) => url.pathname.startsWith("/api/recap/jobs"), async (route: Route) => {
        const request = route.request();
        const path = new URL(request.url()).pathname;
        if (request.method() === "POST") posts += 1;
        const polled = request.method() === "GET" && /^\/api\/recap\/jobs\/[^/]+$/.test(path);
        if (!held || !(request.method() === "POST" || polled)) return route.continue();
        const response = await route.fetch();
        const job = await response.json();
        await route.fulfill({response, json: {...job, status: "running"}});
    });
    return {release: () => { held = false; }, posts: () => posts};
}

test("a running recap shows the wait card and adds no request", async ({page}) => {
    const job = await holdRecapJob(page);
    await page.goto(RECAP);
    await expect(page.locator("#guide-open")).toBeVisible();
    // The screen is polling its job: the first poll has gone out.
    await page.waitForRequest((request) => /\/api\/recap\/jobs\/[^/]+$/.test(new URL(request.url()).pathname));
    const seen: Request[] = [];
    const before = new Set<string>();
    page.on("request", (request) => seen.push(request));
    await page.waitForTimeout(2_500);
    seen.splice(0).forEach((request) => before.add(request.method() + " " + new URL(request.url()).pathname));
    await screenGuide(page);
    await expect(title(page)).toHaveText("The recap is being computed");
    await expect(count(page)).toBeHidden();
    await expect(card(page).locator(".guide-quit")).toBeFocused();
    await page.waitForTimeout(2_500);
    // While the guide waits, the page asks only what it was already asking.
    const during = seen.splice(0).map((request) => request.method() + " " + new URL(request.url()).pathname);
    expect(during.filter((request) => !before.has(request))).toEqual([]);
    expect(job.posts()).toBe(1);
    job.release();
    await expect(count(page)).toHaveText(/^1 of \d+$/, {timeout: 60_000});
    await expect(title(page)).toHaveText("You are in the Management view");
    expect(await focusInCard(page)).toBe(true);
    expect(job.posts()).toBe(1);
});

test("a failed recap offers only to close", async ({page}) => {
    await page.route((url) => url.pathname.startsWith("/api/recap/jobs"),
        (route) => route.fulfill({status: 500, json: {error: "demo"}}));
    await page.goto(RECAP);
    // Opened before the error lands: the wait card gives way to the failed one.
    await screenGuide(page);
    await expect(title(page)).toHaveText("The recap could not be read", {timeout: 15_000});
    await expect(card(page).locator("button:visible")).toHaveCount(1);
    await expect(next(page)).toHaveText("Close");
    await expect(next(page)).toBeFocused();
    await next(page).click();
    await expect(page.locator("#guide-root")).toBeHidden();
});

test("an empty source waits instead of throwing", async ({page}) => {
    await page.goto(RECAP);
    await expect(page.locator(".recap-row").first()).toBeVisible({timeout: 60_000});
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.evaluate(() => (window as unknown as { PSL_GUIDE: { start(source: () => unknown): void } })
        .PSL_GUIDE.start(() => ({status: "ready", steps: []})));
    await expect(title(page)).toHaveText("The recap is being computed");
    await page.keyboard.press("Escape");
    await expect(page.locator("#guide-root")).toBeHidden();
    expect(errors).toEqual([]);
});

test("the card stays on screen beside a tall row", async ({page}) => {
    await openGuide(page);
    // A row taller than the window, as a long sentence makes one on a phone.
    await page.addStyleTag({content: '[data-guide="recap.row"] { min-height: 130vh; }'});
    await goTo(page, "How to read a row");
    await settle(page);
    await expectInWindow(page, ".guide-card");
    for (const by of [300, -150]) {
        const before = await page.evaluate(() => window.scrollY);
        await page.mouse.wheel(0, by);
        await settle(page);
        // The wheel did scroll, or the card had nothing to follow.
        expect(await page.evaluate(() => window.scrollY)).not.toBe(before);
        await expectInWindow(page, ".guide-card");
    }
});

test("the card stays on screen when the page is scrolled up past its row", async ({page}) => {
    await openGuide(page);
    // The row sits more than a window below the top, so the top of the page leaves it under the fold.
    await page.addStyleTag({content: '[data-guide="recap.row"] { margin-top: 150vh; }'});
    await goTo(page, "How to read a row");
    await settle(page);
    await page.evaluate(() => window.scrollTo({top: 0, behavior: "instant"}));
    await settle(page);
    expect(await page.locator('[data-guide="recap.row"]').evaluate((row) =>
        row.getBoundingClientRect().top > window.innerHeight)).toBe(true);
    await expectInWindow(page, ".guide-card");
});

test("Quit never sits over the title's text, in French", async ({page}) => {
    await useLang(page, "fr");
    await openGuide(page);
    await eachStep(page, async () => {
        const [quit, lines] = await page.evaluate(() => {
            const heading = document.querySelector("#guide-title")!;
            const range = document.createRange();
            range.selectNodeContents(heading);
            return [
                document.querySelector(".guide-quit")!.getBoundingClientRect().toJSON(),
                Array.from(range.getClientRects()).map((line) => line.toJSON())
            ];
        });
        for (const line of lines) expect(line.right).toBeLessThanOrEqual(quit.left);
    });
});

test("the entry shares its row with the theme and language", async ({page}) => {
    await page.goto(RECAP);
    await expect(page.locator(".recap-row").first()).toBeVisible({timeout: 60_000});
    // The demo fleet carries the version-skew warning, the widest chip the cluster holds.
    await expect(page.locator(".shell-version-skew")).toBeVisible();
    const top = async (selector: string) => (await page.locator(selector).boundingBox())!.y;
    expect(await top("#guide-open")).toBe(await top("#theme-toggle"));
    expect(await top("#guide-open")).toBe(await top("#lang-toggle"));
});

test("going back leaves the guide and wakes the page", async ({page}) => {
    // A page without a guide, so the entry stays hidden once the page is back.
    await page.goto("/#/nowhere");
    await expect(page.locator("#guide-open")).toBeHidden();
    await openGuide(page);
    await page.goBack();
    await expect(page).toHaveURL(/#\/nowhere/);
    await expect(page.locator("#guide-root")).toBeHidden();
    for (const part of [".shell-topbar", "#main", ".shell-footer"])
        await expect(page.locator(part)).not.toHaveAttribute("inert", "");
    await expect(page.locator("#guide-open")).toBeHidden();
});

test("the page behind is inert", async ({page}) => {
    await openGuide(page);
    await settle(page);
    const tech = page.locator('[data-guide="recap.view"] [role=radio]').filter({hasText: "Engineering"});
    const box = (await tech.boundingBox())!;
    const x = box.x + box.width / 2;
    const y = box.y + box.height / 2;
    // Nothing of the guide covers the radio: the click lands on it and only inert stops it. Hit-testing
    // skips inert nodes, so the point is read with #main woken for that one synchronous call.
    expect(await tech.evaluate((radio, [left, top]) => {
        const main = document.getElementById("main")!;
        const guide = document.elementFromPoint(left, top);
        main.removeAttribute("inert");
        const hit = document.elementFromPoint(left, top);
        main.setAttribute("inert", "");
        return !document.getElementById("guide-root")!.contains(guide) && hit !== null && radio.contains(hit);
    }, [x, y])).toBe(true);
    await page.mouse.click(x, y);
    await page.waitForTimeout(500);
    await expect(page).toHaveURL(/view=pilotage/);
    await expect(card(page)).toBeVisible();
    // Tab stays among the card's buttons.
    for (let press = 0; press < 4; press += 1) {
        await page.keyboard.press("Tab");
        expect(await focusInCard(page)).toBe(true);
    }
});

for (const lang of ["en", "fr"] as const) {
    test(`the figures match the screen (${lang})`, async ({page}) => {
        await useLang(page, lang);
        await openGuide(page);
        await expect(count(page)).toHaveText(lang === "fr" ? /^1 sur \d+$/ : /^1 of \d+$/);
        await goToAnchors(page, ["recap.io"]);
        const tile = (name: string) => page.locator(`[data-guide="${name}"] .disc-figure-value`);
        const io = (await tile("recap.io").textContent())!.trim();
        expect(io).not.toBe("");
        // The title is the tile's figure, as the tile writes it, then the words.
        await expect(title(page)).toHaveText(new RegExp("^" + io.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "\\s"));
        await goToAnchors(page, ["recap.findings", "recap.actions"]);
        const findings = (await tile("recap.findings").textContent())!.trim();
        const done = (await tile("recap.actions").evaluate((node) => node.firstChild?.textContent ?? ""))!.trim();
        await expect(title(page)).toHaveText(new RegExp(`^${findings}\\D.*, ${done}\\D`));
    });
}

test("nothing scrolls sideways", async ({page}) => {
    await openGuide(page);
    await eachStep(page, async () => {
        await settle(page);
        const wide = await page.evaluate(() => ({
            scroll: document.documentElement.scrollWidth, width: window.innerWidth
        }));
        expect(wide.scroll).toBeLessThanOrEqual(wide.width);
        await expectInWindow(page, ".guide-card");
    });
});

test("under reduced motion the scroll a frame after each move is already its final position", async ({page}) => {
    await page.emulateMedia({reducedMotion: "reduce"});
    await openGuide(page);
    await settle(page);
    const total = await totalSteps(page);
    const scrollY = () => page.evaluate(() => window.scrollY);
    let scrolled = 0;
    // Each move lands at once: the scroll a frame after the click is already the final one.
    for (let index = 1; index < total; index += 1) {
        const before = await scrollY();
        await next(page).click();
        const first = await page.evaluate(() => new Promise<number>((done) =>
            requestAnimationFrame(() => done(window.scrollY))));
        await page.waitForTimeout(400);
        expect(await scrollY()).toBe(first);
        if (first !== before) scrolled += 1;
    }
    // Some of those moves had to scroll, or the check above proved nothing.
    expect(scrolled).toBeGreaterThan(0);
});

const TECH = "/#/recap?view=tech";

/** The recap in its technical view, drawn; `before` runs on the drawn page, then the guide opens. */
async function openTechGuide(page: Page, before?: () => Promise<void>): Promise<void> {
    await page.goto(TECH);
    await expect(page.locator(".recap-row").first()).toBeVisible({timeout: 60_000});
    if (before) await before();
    await screenGuide(page);
    await expect(card(page)).toBeVisible();
}

test("the technical walk reaches the priority, its fix and its action", async ({page}) => {
    await openTechGuide(page);
    const total = await totalSteps(page);
    const titles: string[] = [];
    await eachStep(page, async (index) => {
        titles.push(await title(page).textContent() ?? "");
        await expect(next(page)).toHaveText(index === total - 1 ? "Close" : "Next");
    });
    expect(titles[0]).toBe("You are in the Engineering view");
    for (const wanted of ["What the page counts", "Lists to tick", "The period", "What to fix first",
        "How to read a priority", "How to fix", "Take an action", "What moved"]) expect(titles).toContain(wanted);
    expect(titles[total - 1]).toBe("You can now choose what to take");
    await next(page).click();
    await expect(page.locator("#guide-root")).toBeHidden();
    await expect(page.locator("#guide-open")).toBeFocused();
});

test("the technical row numbers its marks, and the fix and the action frame their parts", async ({page}) => {
    await openTechGuide(page);
    await goTo(page, "How to read a priority");
    const legend = await card(page).locator(".guide-legend li").count();
    expect(legend).toBeGreaterThanOrEqual(4);
    await expect(page.locator(".guide-marks .guide-mark")).toHaveCount(legend);
    await next(page).click();
    await expect(title(page)).toHaveText("How to fix");
    expect(await framed(page, ["recap.row.detail"])).toBe(true);
    await next(page).click();
    await expect(title(page)).toHaveText("Take an action");
    expect(await framed(page, ["recap.row.facts", "recap.row.state"])).toBe(true);
});

test("a folded fix is skipped, as the guide never unfolds it", async ({page}) => {
    await openTechGuide(page, async () => {
        await page.locator('[data-guide="recap.row"] .recap-detail > summary').click();
        await expect(page.locator('[data-guide="recap.row.detail"]')).toHaveCount(0);
    });
    const titles: string[] = [];
    await eachStep(page, async () => {
        titles.push(await title(page).textContent() ?? "");
    });
    expect(titles).toContain("Take an action");
    expect(titles).not.toContain("How to fix");
});

test("a guide reads its own wait text, else the shared one", async ({page}) => {
    await page.goto(TECH);
    await expect(page.locator(".recap-row").first()).toBeVisible({timeout: 60_000});
    type Guide = { PSL_GUIDE: { start(source: () => unknown, guide?: string): void }, PSL_TEXT: Record<string, Record<string, string>> };
    await page.evaluate(() => {
        const guide = window as unknown as Guide;
        guide.PSL_TEXT.en["guide.probe.wait.title"] = "The probe waits";
        guide.PSL_GUIDE.start(() => ({status: "wait", steps: []}), "screen.probe");
    });
    await expect(title(page)).toHaveText("The probe waits");
    // No body of its own: the shared one.
    await expect(card(page).locator("#guide-body")).toHaveText(/^The guide will start/);
    await page.keyboard.press("Escape");
    await page.evaluate(() => (window as unknown as Guide).PSL_GUIDE.start(() => ({status: "wait", steps: []}), "recap.tech"));
    await expect(title(page)).toHaveText("The recap is being computed");
    await page.keyboard.press("Escape");
});

test("a fix folded between two openings of the guide is skipped the second time", async ({page}) => {
    await openTechGuide(page);
    await goTo(page, "How to fix");
    await page.keyboard.press("Escape");
    await expect(page.locator("#guide-root")).toBeHidden();
    await page.locator('[data-guide="recap.row"] .recap-detail > summary').click();
    await expect(page.locator('[data-guide="recap.row.detail"]')).toHaveCount(0);
    await screenGuide(page);
    const titles: string[] = [];
    await eachStep(page, async () => {
        titles.push(await title(page).textContent() ?? "");
    });
    expect(titles).toContain("Take an action");
    expect(titles).not.toContain("How to fix");
});

// ---- the analysis screens: New, Recent, one run and its report

/** The guide opened once the screen has drawn `ready`. */
async function openHere(page: Page, ready: string): Promise<void> {
    await expect(page.locator(ready).first()).toBeVisible({timeout: 60_000});
    await screenGuide(page);
    await expect(card(page)).toBeVisible();
}

async function openOn(page: Page, route: string, ready: string): Promise<void> {
    await page.goto(route);
    await openHere(page, ready);
}

/** Every title of the walk, from the first card to the last, then Close. */
async function walkTitles(page: Page): Promise<string[]> {
    const total = await totalSteps(page);
    const titles: string[] = [];
    await eachStep(page, async (index) => {
        titles.push(await title(page).textContent() ?? "");
        await expect(next(page)).toHaveText(index === total - 1 ? "Close" : "Next");
    });
    await next(page).click();
    await expect(page.locator("#guide-root")).toBeHidden();
    await expect(page.locator("#guide-open")).toBeFocused();
    return titles;
}

test("the entry shows on the analysis, fleet and disclosure screens, and not on a missing page", async ({page}) => {
    for (const route of ["/#/new", "/#/recent", `/#/run/${state.succeeded}`, `/#/report/${state.succeeded}`,
        "/#/sources", "/#/incidents", "/#/correlations", "/#/disclosures", `/#/disclosures/${state.disclosure}`]) {
        await page.goto(route);
        await expect(page.locator("#guide-open"), route).toBeVisible({timeout: 30_000});
    }
    await page.goto("/#/nowhere");
    await expect(page.locator("#main h1")).toBeVisible();
    await expect(page.locator("#guide-open")).toBeHidden();
});

test("the New walk on a daemon skips the backend's fields", async ({page}) => {
    await page.goto("/#/new");
    await page.locator("button.source-row", {hasText: "Checkout production"}).click();
    await openHere(page, '[data-guide="new.daemon"]');
    const titles = await walkTitles(page);
    expect(titles[0]).toBe("The source");
    for (const wanted of ["A daemon takes no parameter", "What this analysis costs", "Run the analysis"]) {
        expect(titles).toContain(wanted);
    }
    for (const absent of ["Service or trace ID", "Max traces", "Simultaneous reads", "Prefer your terminal?",
        "Nothing to run here"]) expect(titles).not.toContain(absent);
    expect(titles[titles.length - 1]).toBe("You can now run a measured analysis");
});

test("the New walk on Tempo reads the form, its reads and its command", async ({page}) => {
    await page.goto("/#/new");
    await page.locator("button.source-row", {hasText: "Tempo EU"}).click();
    await openHere(page, '[data-guide="new.traces"]');
    await goTo(page, "Max traces");
    expect(await framed(page, ["new.traces"])).toBe(true);
    await page.keyboard.press("Escape");
    await screenGuide(page);
    const titles = await walkTitles(page);
    for (const wanted of ["Service or trace ID", "The period", "Max traces", "Simultaneous reads",
        "For advanced users only", "Run the analysis", "Prefer your terminal?"]) expect(titles).toContain(wanted);
    expect(titles).not.toContain("A daemon takes no parameter");
});

test("the Recent walk reads the first card", async ({page}) => {
    await openOn(page, "/#/recent", ".run-card");
    await goTo(page, "One analysis");
    expect(await framed(page, ["recent.card"])).toBe(true);
    await page.keyboard.press("Escape");
    await screenGuide(page);
    const titles = await walkTitles(page);
    expect(titles[0]).toBe("Analyses this Hub still holds");
    expect(titles).not.toContain("No recent analysis");
    expect(titles[titles.length - 1]).toBe("You can find an analysis again");
});

test("the run walk names its state and frames its outcome", async ({page}) => {
    await openOn(page, `/#/run/${state.succeeded}`, ".outcome");
    await expect(title(page)).toHaveText("It succeeded");
    expect(await framed(page, ["run.head", "run.headline"])).toBe(true);
    await goTo(page, "The result");
    expect(await framed(page, ["run.outcome"])).toBe(true);
    await expect(card(page).locator("#guide-body")).toContainText("The link expires in");
    await page.keyboard.press("Escape");
    await screenGuide(page);
    expect(await walkTitles(page)).toEqual(["It succeeded", "Service events", "Elapsed time", "What it asked for",
        "The result", "You can read this analysis"]);
});

test("an unknown run offers only to close", async ({page}) => {
    await openOn(page, "/#/run/ffffffffffffffff", ".gone");
    await expect(title(page)).toHaveText("No analysis with this ID");
    await expect(card(page).locator(".guide-quit")).toBeHidden();
    await expect(next(page)).toHaveText("Close");
});

test("the report walk stops at the frame", async ({page}) => {
    await openOn(page, `/#/report/${state.succeeded}`, "iframe.report-frame");
    await goTo(page, "The engine's report");
    expect(await framed(page, ["report.frame"])).toBe(true);
    await page.keyboard.press("Escape");
    await screenGuide(page);
    expect(await walkTitles(page)).toEqual(["You are in the report", "The engine's report", "You can open a report"]);
});

/** Unfolds the first row of the screen's table and waits for its anchor, before the guide opens. */
async function unfold(page: Page, route: string, anchor: string): Promise<void> {
    await page.goto(route);
    await page.locator(".sources-wrap .row-toggle").first().click();
    await expect(page.locator(`[data-guide="${anchor}"]`)).toBeVisible({timeout: 30_000});
}

test("the Sources walk reads the first row and the fleet bar, and no gauges while every row is folded", async ({page}) => {
    await openOn(page, "/#/sources", '[data-guide="sources.table"]');
    await goTo(page, "Health");
    expect(await framed(page, ["sources.health"])).toBe(true);
    await page.keyboard.press("Escape");
    await screenGuide(page);
    await goTo(page, "A fleet behind");
    expect(await framed(page, ["shell.skew"])).toBe(true);
    await page.keyboard.press("Escape");
    await screenGuide(page);
    expect(await walkTitles(page)).toEqual(["Fleet health", "Health", "Last success, unreachable for",
        "Each source's engine", "A fleet behind", "Env (declared)", "Last error", "A daemon's detail",
        "You can tell whether the data arrives"]);
});

test("an unfolded daemon row adds its gauges to the Sources walk", async ({page}) => {
    await unfold(page, "/#/sources", "sources.gauges");
    await openHere(page, '[data-guide="sources.gauges"]');
    await goTo(page, "The daemon right now");
    expect(await framed(page, ["sources.gauges"])).toBe(true);
});

test("the Incidents walk asks to unfold a row", async ({page}) => {
    await openOn(page, "/#/incidents", '[data-guide="incidents.table"]');
    await goTo(page, "What was already burning");
    expect(await framed(page, ["incidents.row"])).toBe(true);
    await expect(card(page).locator("#guide-body")).toContainText("Unfold a row, then open the guide again");
    await page.keyboard.press("Escape");
    await screenGuide(page);
    expect(await walkTitles(page)).toEqual(["Incidents the daemons recorded", "Narrow the list", "One row per incident",
        "Capture", "What was already burning", "When each daemon was read",
        "You can link an incident to what came before it"]);
});

test("an incident unfolded before the guide is read, and refolded it is skipped", async ({page}) => {
    await unfold(page, "/#/incidents", "incidents.panel");
    await openHere(page, '[data-guide="incidents.panel"]');
    await goTo(page, "What was already burning");
    await expect(card(page).locator("#guide-body")).not.toContainText("Unfold a row");
    await goTo(page, "An unfolded incident");
    expect(await framed(page, ["incidents.panel"])).toBe(true);
    await expect(card(page).locator("#guide-body")).toContainText("\"Analyse this window\"");
    await page.keyboard.press("Escape");
    await page.locator(".sources-wrap .row-toggle").first().click();
    await screenGuide(page);
    expect(await walkTitles(page)).not.toContain("An unfolded incident");
});

test("the Correlations walk says a pair is no cause, and an unfolded pair reads its episodes", async ({page}) => {
    await openOn(page, "/#/correlations", '[data-guide="correlations.table"]');
    expect(await walkTitles(page)).toEqual(["Findings that keep following each other", "A correlation is not a cause",
        "Narrow the list", "One row per pair", "Unfold a pair", "Where the pairs come from", "You can read a correlation"]);
    await unfold(page, "/#/correlations", "correlations.panel");
    await openHere(page, '[data-guide="correlations.panel"]');
    await goTo(page, "The episodes");
    expect(await framed(page, ["correlations.panel"])).toBe(true);
});

test("an incidents read that fell back to an empty copy fails with its own words", async ({page}) => {
    let stored = 200;
    await page.route((url) => url.pathname === "/api/incidents/refresh",
        (route) => route.fulfill({status: 500, json: {error: "demo"}}));
    await page.route((url) => url.pathname === "/api/incidents",
        (route) => route.fulfill(stored === 200 ? {json: []} : {status: stored, json: {error: "demo"}}));
    await openOn(page, "/#/incidents", '.banner[data-tone="warn"]');
    await expect(title(page)).toHaveText("The daemons were not read just now");
    await expect(next(page)).toHaveText("Close");
    await next(page).click();
    // The stored copy fails too: the Hub's own failure, in the shared words of the guide.
    stored = 500;
    await page.reload();
    await openHere(page, '.banner[data-tone="crit"]');
    await expect(title(page)).toHaveText("The Hub did not return the incidents");
});

test("the Disclosures walk reads the rail, the legend and the first report", async ({page}) => {
    await openOn(page, "/#/disclosures", '[data-guide="disc.card"]');
    await goTo(page, "A report");
    expect(await framed(page, ["disc.card"])).toBe(true);
    await expect(card(page).locator("#guide-body")).toContainText("from half to twice the figure");
    await page.keyboard.press("Escape");
    await screenGuide(page);
    expect(await walkTitles(page)).toEqual(["Energy and carbon, period by period", "The quarters", "Declared or checked",
        "A report", "Publishing the next one", "You can read a public report"]);
});

test("the walk of a disclosure frames its title and says who checked what", async ({page}) => {
    await openOn(page, `/#/disclosures/${state.disclosure}`, '[data-guide="disc.integrity"]');
    expect(await framed(page, ["disc.head", "disc.head.sub"])).toBe(true);
    await goTo(page, "Who checked what");
    expect(await framed(page, ["disc.integrity"])).toBe(true);
    await page.keyboard.press("Escape");
    await screenGuide(page);
    expect(await walkTitles(page)).toEqual(["A disclosure", "Download, share, withdraw", "At a glance",
        "The applications", "Who checked what", "The files", "The share links", "Method and limits",
        "You know what this report guarantees"]);
});

test("a disclosure the Hub does not return offers only to close", async ({page}) => {
    await openOn(page, "/#/disclosures/ffffffffffffffff", '.banner[data-tone="crit"]');
    await expect(title(page)).toHaveText("The Hub did not return this disclosure");
    await expect(next(page)).toHaveText("Close");
});

// ---- « Je suis… » and the role paths

const prev = (page: Page) => card(page).locator(".guide-nav .pill-button:not(.pill-primary)");

/** The entry's menu, I am…, then the role: the path starts on its first screen. */
async function rolePath(page: Page, role: string): Promise<void> {
    await page.locator("#guide-open").click();
    await page.locator("#guide-menu-role").click();
    await page.locator(`#guide-menu [data-role="${role}"]`).click();
    await expect(card(page)).toBeVisible();
}

/** The route first, then the entry, which shows once the screen is drawn. */
async function landOn(page: Page, route: string): Promise<void> {
    await page.goto(route);
    await expect(page.locator("#guide-open")).toBeVisible({timeout: 30_000});
}

/** Next until the screen's last card, after `first` titled its first one. */
async function toScreenEnd(page: Page, first: string, screen: string): Promise<void> {
    await expect(title(page)).toHaveText(first, {timeout: 60_000});
    await expect(prev(page)).toBeHidden();
    const total = Number((await count(page).textContent() ?? "").match(/^\d+ of (\d+)/)?.[1]);
    for (let index = 1; index < total; index += 1) {
        await expect(count(page)).toHaveText(`${index} of ${total}${screen}`);
        await next(page).click();
    }
    await expect(count(page)).toHaveText(`${total} of ${total}${screen}`);
}

test("the CTO path takes the recap in its management view, then Disclosures", async ({page}) => {
    await landOn(page, "/#/new");
    await rolePath(page, "cto");
    await expect(page).toHaveURL(/#\/recap\?view=pilotage$/);
    await toScreenEnd(page, "You are in the Management view", " · screen 1 of 2");
    await expect(next(page)).toHaveText("Next screen");
    await next(page).click();
    await expect(page).toHaveURL(/#\/disclosures$/);
    await toScreenEnd(page, "Energy and carbon, period by period", " · screen 2 of 2");
    await expect(next(page)).toHaveText("Close");
    await expect(card(page).locator("#guide-body")).not.toContainText("Skipped");
    await next(page).click();
    await expect(page.locator("#guide-root")).toBeHidden();
    await expect(page).toHaveURL(/#\/disclosures$/);
    await expect(page.locator("#guide-open")).toBeFocused();
});

test("the lead path reads the recap in its technical view, then New analysis, and Back ends it", async ({page}) => {
    await landOn(page, RECAP);
    await rolePath(page, "lead");
    await expect(page).toHaveURL(/#\/recap$/);
    await toScreenEnd(page, "You are in the Engineering view", " · screen 1 of 2");
    // Previous stays in the screen.
    await prev(page).click();
    await expect(count(page)).toHaveText(/ · screen 1 of 2$/);
    await page.keyboard.press("ArrowRight");
    await next(page).click();
    await expect(page).toHaveURL(/#\/new$/);
    await expect(count(page)).toHaveText(/^1 of \d+ · screen 2 of 2$/, {timeout: 30_000});
    await expect(prev(page)).toBeHidden();
    // A route that is not the path's next stop ends it.
    await page.goBack();
    await expect(page.locator("#guide-root")).toBeHidden();
});

test("a screen of a path that fails hands over to the next one", async ({page}) => {
    await page.route((url) => url.pathname.startsWith("/api/recap/jobs"),
        (route) => route.fulfill({status: 500, json: {error: "demo"}}));
    await landOn(page, "/#/sources");
    await rolePath(page, "lead");
    await expect(title(page)).toHaveText("This screen did not show", {timeout: 15_000});
    await expect(next(page)).toHaveText("Next screen");
    await expect(next(page)).toBeFocused();
    await next(page).click();
    await expect(page).toHaveURL(/#\/new$/);
    await expect(count(page)).toHaveText(/^1 of \d+ · screen 2 of 2$/, {timeout: 30_000});
});

test("the menu opens on the keyboard, Escape closes it and gives the focus back", async ({page}) => {
    await landOn(page, "/#/sources");
    await page.locator("#guide-open").focus();
    await page.keyboard.press("Enter");
    await expect(page.locator("#guide-open")).toHaveAttribute("aria-expanded", "true");
    await expect(page.locator("#guide-menu-screen")).toBeFocused();
    await page.keyboard.press("ArrowDown");
    await expect(page.locator("#guide-menu-role")).toBeFocused();
    await page.keyboard.press("ArrowDown");
    await expect(page.locator("#guide-menu-screen")).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(page.locator("#guide-menu")).toHaveCount(0);
    await expect(page.locator("#guide-open")).toBeFocused();
    await expect(page.locator("#guide-open")).toHaveAttribute("aria-expanded", "false");
    // Shift+Tab back onto the entry leaves the menu open, and Escape there still closes it.
    await page.keyboard.press("Enter");
    await page.keyboard.press("Shift+Tab");
    await expect(page.locator("#guide-open")).toBeFocused();
    await expect(page.locator("#guide-menu")).toHaveCount(1);
    await page.keyboard.press("Escape");
    await expect(page.locator("#guide-menu")).toHaveCount(0);
    await expect(page.locator("#guide-open")).toBeFocused();
    // A click on the backdrop closes it too, and the focus goes back to the entry.
    await page.keyboard.press("Enter");
    await page.locator("#guide-menu-backdrop").click({position: {x: 2, y: 2}});
    await expect(page.locator("#guide-menu")).toHaveCount(0);
    await expect(page.locator("#guide-open")).toBeFocused();
    await page.keyboard.press("Enter");
    await page.keyboard.press("End");
    await page.keyboard.press("Enter");
    // Nothing remembered yet: the first role takes the focus, none is ticked.
    await expect(page.locator('#guide-menu [data-role="dev"]')).toBeFocused();
    await expect(page.locator('#guide-menu [aria-checked="true"]')).toHaveCount(0);
    await expectInWindow(page, "#guide-menu");
    await page.keyboard.press("End");
    await expect(page.locator('#guide-menu [data-role="cto"]')).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(page.locator("#guide-menu")).toHaveCount(0);
    await expect(page.locator("#guide-open")).toBeFocused();
    // Understand this screen, from the menu, still opens the screen's own guide.
    await page.keyboard.press("Enter");
    await page.keyboard.press("Enter");
    await expect(title(page)).toHaveText("Fleet health");
});

test("the role chosen is ticked next time, and quitting a path stays where it is", async ({page}) => {
    await landOn(page, "/#/sources");
    await rolePath(page, "architect");
    await expect(page).toHaveURL(/#\/correlations$/);
    await expect(count(page)).toHaveText(/^1 of \d+ · screen 1 of 3$/, {timeout: 30_000});
    await page.keyboard.press("Escape");
    await expect(page.locator("#guide-root")).toBeHidden();
    await expect(page).toHaveURL(/#\/correlations$/);
    await page.locator("#guide-open").click();
    await page.locator("#guide-menu-role").click();
    await expect(page.locator('#guide-menu [aria-checked="true"]')).toHaveAttribute("data-role", "architect");
    await expect(page.locator('#guide-menu [data-role="architect"]')).toBeFocused();
});

test("a storage that throws leaves the menu and the path working", async ({page}) => {
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.addInitScript(() => Object.defineProperty(window, "localStorage", {
        get() {
            throw new DOMException("denied", "SecurityError");
        }
    }));
    await landOn(page, "/#/sources");
    await page.locator("#guide-open").click();
    await page.locator("#guide-menu-role").click();
    await expect(page.locator('#guide-menu [aria-checked="true"]')).toHaveCount(0);
    await page.locator('#guide-menu [data-role="dev"]').click();
    await expect(page).toHaveURL(/#\/recap$/);
    await expect(title(page)).toHaveText("You are in the Engineering view", {timeout: 60_000});
    await page.keyboard.press("Escape");
    await page.locator("#guide-open").click();
    await page.locator("#guide-menu-role").click();
    await expect(page.locator('#guide-menu [aria-checked="true"]')).toHaveCount(0);
    expect(errors).toEqual([]);
});
