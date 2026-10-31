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
    text.en = {
        'shell.wordmark': 'Perf Sentinel Hub, new analysis',
        'shell.tabs': 'Screens',
        'shell.tab.new': 'New analysis',
        'shell.tab.recent': 'Recent',
        'shell.tab.sources': 'Sources',
        'shell.tab.incidents': 'Incidents',
        'shell.tab.correlations': 'Correlations',
        'shell.tab.recap': 'Recap',
        'shell.tab.disclosures': 'Disclosures',
        'shell.tab.coverage': 'Coverage',
        'shell.version.hub': 'hub',
        'shell.version.engine': 'engine',
        'shell.version.unknown': 'unknown',
        'shell.version.none': 'none',
        'shell.update.title': 'The newest release published on GitHub, read by this Hub on its update-check '
            + 'interval. It says a newer version exists, not that this one is wrong.',
        'shell.update.news': 'update available',
        'shell.skew': 'fleet {oldest} → {engine}',
        'shell.identity.title': 'Established by the Hub\'s sign-in, or by the reverse proxy in front of it. '
            + 'There is no account to manage here.',
        'shell.identity.access': 'Your access: who you are, your groups and what you reach.',
        'shell.theme.auto': 'System',
        'shell.theme.light': 'Light',
        'shell.theme.dark': 'Dark',
        'shell.theme.button': 'Theme: {label}. Click to cycle.',
        'shell.lang.switch': 'Show the Hub in French',
        'shell.credit.tool': 'Powered by',
        'shell.credit.design': 'design & logo by',
        'help.about': 'About {topic}',
        'help.label': 'Help',
        'steps.head': '// what you need first',
        'terminal.copy': 'Copy',
        'terminal.copied': 'Copied',
        'terminal.copied_status': 'Copied.',
        'terminal.copy_refused.command': 'This browser refused the copy. The command is selected, use your '
            + 'own copy key.',
        'terminal.copy_refused.file': 'This browser refused the copy. The file is selected, use your own copy key.',
        'terminal.download': 'Download',
        'terminal.download_label': 'Download {file}',
        'terminal.sent': 'Sent {file} to your browser. It decides where it lands, and may rename a file whose '
            + 'name starts with a dot.',
        'terminal.shells': 'Shell',
        'engine.need': 'Install the `perf-sentinel` binary on the machine you will type this into. This Hub '
            + 'runs {version} and the command is spelled for it, an older engine may refuse a flag it does '
            + 'not have yet. Take the same build: ',
        'engine.need_unknown': 'Install the `perf-sentinel` binary on the machine you will type this into. '
            + 'This Hub reports no engine version, so there is none to match here: ',
        'engine.release': 'perf-sentinel {version} on GitHub',
        'engine.releases': 'the perf-sentinel releases',
        'token.before': 'This source is behind an `{header}` header. Run this in the same terminal, before '
            + 'the command, with your own credential where the dots are:',
        'token.after': 'The whole header line goes in, name included, not the value on its own. The Hub '
            + 'holds a header of its own for this source and never discloses it, which is why the command '
            + 'reads yours from a variable rather than carrying a secret where `ps` would show it to everyone '
            + 'on the machine.'
    };
})(globalThis);
