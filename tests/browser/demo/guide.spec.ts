import {expect, Page, Request, Route, test} from "@playwright/test";

// The pilotage guide walked on the real recap of the demo Hub. Assertions, not captures: nothing
// here writes to docs/img, and `npm run demo` does not run it (see README).

const RECAP = "/#/recap?view=pilotage";
const LANG_KEY = "perf-sentinel:lang";

const card = (page: Page) => page.locator(".guide-card");
const title = (page: Page) => page.locator("#guide-title");
const next = (page: Page) => card(page).locator(".guide-nav .pill-primary");
const count = (page: Page) => card(page).locator(".guide-count");

async function useLang(page: Page, lang: "en" | "fr"): Promise<void> {
    await page.addInitScript(([key, value]) => localStorage.setItem(key, value), [LANG_KEY, lang]);
}

/** The recap in its management view, drawn, then the guide opened on it. */
async function openGuide(page: Page): Promise<void> {
    await page.goto(RECAP);
    await expect(page.locator(".recap-row").first()).toBeVisible({timeout: 60_000});
    await page.locator("#guide-open").click();
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
    await page.locator("#guide-open").click();
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
    await page.locator("#guide-open").click();
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
    await page.goto("/#/sources");
    await expect(page.locator("#guide-open")).toBeHidden();
    await openGuide(page);
    await page.goBack();
    await expect(page).toHaveURL(/#\/sources/);
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
    await page.locator("#guide-open").click();
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
    await page.locator("#guide-open").click();
    const titles: string[] = [];
    await eachStep(page, async () => {
        titles.push(await title(page).textContent() ?? "");
    });
    expect(titles).toContain("Take an action");
    expect(titles).not.toContain("How to fix");
});
