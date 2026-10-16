// What every still spec shares: where the files land, the state global-setup left,
// and the wait for a launcher screen to be drawn.
import {expect, Page} from "@playwright/test";
import {readFileSync} from "node:fs";
import {join} from "node:path";
import {tmpdir} from "node:os";

const OUT = join(__dirname, "..", "..", "..", "docs", "img", "hub");
// Written by global-setup once the Hub is up and the runs have settled. Read at
// module scope, so a setup that died earlier would otherwise fail every test
// here with a bare ENOENT naming a temp path and nothing else.
const STATE_FILE = join(tmpdir(), "perf-sentinel-hub-demo", "state.json");
type DemoState = {
    succeeded: string, disclosure: string, manager: string, ackSignature: string, accessBaseUrl: string
};
function readState(): DemoState {
    try {
        return JSON.parse(readFileSync(STATE_FILE, "utf8")) as DemoState;
    } catch (error) {
        throw new Error(
            `No demo state at ${STATE_FILE}. global-setup did not finish: it stands up the fake ` +
            `daemons, the Hub and the analysis runs these stills need. Its own error is above. ` +
            `(${String(error)})`);
    }
}

export const state = readState();

export const nameFor = (screen: string, project: string) =>
    join(OUT, project.endsWith("-dark") ? `${screen}-dark.png` : `${screen}.png`);

// Every still fast-forwards finite CSS transitions. The fold chevron rotates
// from > to v over 150 ms, and a panel that fills faster than that lands the
// shot mid-rotation, where the two arms read as a check mark rather than a
// chevron.

// The launcher renders into #main after it has fetched, so a screenshot taken
// on load catches an empty shell.
export async function settled(page: Page, marker: string | RegExp): Promise<void> {
    await expect(page.locator("#main")).toContainText(marker, {timeout: 15_000});
    // A sticky footer lands wherever the viewport sat, so a full-page capture
    // bakes it into the middle of the page with content below it.
    await page.addStyleTag({content: ".shell-footer { position: static; }"});
    await expect
        .poll(() => page.locator(".shell-footer")
            .evaluate((node) => getComputedStyle(node).position))
        .toBe("static");
}

