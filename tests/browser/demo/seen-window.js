// The demo freezes a recap of the last closed UTC week, and the captured
// findings were first seen 1 to 5 days before their capture. Slid to now on a
// Sunday, none of them reaches that week and the frozen recap reads empty. So a
// finding first seen 4 days ago or more moves back across the start of the
// current week, the older the further, and the frozen recap holds the same
// findings whatever the weekday of the capture.
const DAY_MS = 86_400_000;

/** @param {number} firstSeenMs @param {number} [now] */
function reachLastClosedWeek(firstSeenMs, now = Date.now()) {
    const age = now - firstSeenMs;
    if (age < 4 * DAY_MS) return firstSeenMs;
    const monday = (Math.floor(now / DAY_MS) - ((new Date(now).getUTCDay() + 6) % 7)) * DAY_MS;
    return Math.min(firstSeenMs, monday - (age - 3 * DAY_MS));
}

module.exports = {reachLastClosedWeek};
