/**
 * Every English text the launcher shows, read through `PSL.t(key, vars)`.
 *
 * Flat keys `area.name`, values with `{name}` placeholders. The French twin in
 * text-fr.js carries the same keys and placeholders, which tests/text.test.js
 * checks. Classic script like launcher.js: each table adds its own language to
 * the shared object, so either file can load first.
 */
(function (global) {
    'use strict';
    const text = global.PSL_TEXT || (global.PSL_TEXT = {});
    text.en = {};
})(globalThis);
