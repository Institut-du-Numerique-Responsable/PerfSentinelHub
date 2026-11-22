const test = require("node:test");
const assert = require("node:assert/strict");
const {reachLastClosedWeek} = require("./seen-window.js");

const DAY_MS = 86_400_000;
const lastClosedWeekEnd = (now) => {
    const monday = (Math.floor(now / DAY_MS) - ((new Date(now).getUTCDay() + 6) % 7)) * DAY_MS;
    return monday - 1;
};

test("a finding first seen 4 or 5 days ago lands in the last closed week on every weekday", () => {
    // 2026-11-16 is a Monday: one capture per weekday, at noon UTC.
    for (let weekday = 0; weekday < 7; weekday++) {
        const now = Date.UTC(2026, 10, 16 + weekday, 12);
        for (const days of [4, 5]) {
            const moved = reachLastClosedWeek(now - days * DAY_MS, now);
            assert.ok(moved <= lastClosedWeekEnd(now), `weekday ${weekday}, ${days} days`);
            assert.ok(moved > lastClosedWeekEnd(now) - 7 * DAY_MS, `weekday ${weekday}, ${days} days`);
        }
    }
});

test("a recent finding keeps its stamp, and the older one stays the older", () => {
    const now = Date.UTC(2026, 10, 22, 12);
    assert.equal(reachLastClosedWeek(now - 3 * DAY_MS, now), now - 3 * DAY_MS);
    assert.ok(reachLastClosedWeek(now - 5 * DAY_MS, now) < reachLastClosedWeek(now - 4 * DAY_MS, now));
});
