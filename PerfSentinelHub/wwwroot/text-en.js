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
            + 'on the machine.',
        'problem.generic': 'The Hub refused the request ({status}).',
        'problem.invalid_json': 'The body is not valid JSON.',
        'problem.invalid_request': 'The request is invalid.',
        'problem.unknown_source': 'Unknown source_id.',
        'problem.unknown_scope': 'Unknown environment or source_id.',
        'problem.unknown_caller': 'The Hub does not know who is asking, and this is done in somebody\'s name.',
        'problem.foreign_origin': 'A request from another site is refused.',
        'problem.machine_token_read_only': 'A machine token only reads.',
        'problem.no_grant': 'No grant on this Hub reaches you.',
        'problem.no_findings_read': 'No grant gives you findings.read on this Hub.',
        'problem.analysis_engine_missing': 'No analysis engine is configured on this Hub.',
        'problem.detection_knob_unsupported': 'A detection setting of this request needs a newer engine than '
            + 'the one this Hub runs.',
        'problem.analysis_forbidden': 'You may not start this analysis.',
        'problem.browser_session_required': 'Approving a sign-in or managing tokens needs a browser session.',
        'problem.browser_not_allowed': 'A browser never calls this route.',
        'problem.unknown_token': 'No such token.',
        'problem.session_ending': 'Your session ends within the hour. Reload the page to sign in again.',
        'problem.invalid_user_code': 'Enter the eight letters your terminal shows.',
        'problem.no_pending_sign_in': 'No sign-in is waiting for this code: it was decided, it expired, or '
            + 'it was mistyped. Run perf-sentinel login again.',
        'problem.too_many_user_codes': 'Too many codes that match no sign-in. Wait, then try again.',
        'problem.actions_forbidden': 'You may not act on findings.',
        'problem.action_read_only': 'You can read this finding but not act on it.',
        'problem.unknown_finding': 'The Hub holds no such finding.',
        'problem.action_exists': 'This finding already has an action; change it with PATCH.',
        'problem.no_action': 'This finding has no action yet.',
        'problem.endpoint_labels_forbidden': 'You may not name endpoints.',
        'problem.service_labels_forbidden': 'You may not name services.',
        'problem.unknown_endpoint': 'The Hub holds no finding at this endpoint.',
        'problem.unknown_service': 'The Hub holds no finding of this service.',
        'problem.endpoint_label_read_only': 'You can read this endpoint but not name it.',
        'problem.service_label_read_only': 'You can read this service but not name it.',
        'problem.acks_not_relayed': 'This source relays no acknowledgments.',
        'problem.acks_forbidden': 'You may not acknowledge findings at this source.',
        'problem.ack_read_only': 'You can read this finding at this source but not acknowledge it there.',
        'problem.unknown_finding_at_source': 'The Hub holds no such finding at this source.',
        'problem.unknown_ack_at_source': 'The Hub holds no such finding at this source and mirrors no ack of '
            + 'it.',
        'problem.daemon_unreachable': 'The daemon could not be reached.',
        'problem.daemon_timeout': 'The daemon did not answer in time.',
        'problem.daemon_refused_signature': 'The daemon refused the signature as not canonical.',
        'problem.daemon_refused_credential': 'The daemon refused the Hub\'s ack credential.',
        'problem.daemon_ack_not_found': 'This finding is not acked at this daemon, or only by its CI '
            + 'baseline, which no API revokes.',
        'problem.daemon_already_acked': 'This finding is already acked at the daemon or by its CI baseline.',
        'problem.daemon_acks_disabled': 'Acknowledgments are disabled on this daemon.',
        'problem.daemon_ack_store_full': 'The ack store of this daemon is full.',
        'problem.daemon_unexpected_answer': 'The daemon gave an answer the Hub does not expect.',
        'problem.not_a_daemon': 'Only a daemon publishes its settings; this source is a trace backend.',
        'problem.invalid_recap_query': 'The recap query is invalid.',
        'problem.recap_reports_forbidden': 'You may not read recap reports.',
        'problem.invalid_card_query': 'The card options query is invalid.',
        'problem.cards_forbidden': 'You may not create cards.',
        'problem.card_sources_forbidden': 'You may not create cards for these sources.',
        'problem.card_engine_missing': 'This Hub\'s engine cannot make cards yet.',
        'problem.cards_busy': 'Another card is being made. Try again in a few seconds.',
        'problem.card_not_rendered': 'The engine could not render this card.',
        'problem.card_field_undrawable': 'The card\'s font cannot draw a character of this field. Remove it, '
            + 'or write it another way.',
        'problem.card_organisation_undrawable': 'The card\'s font cannot draw a character of the '
            + 'organisation\'s name. Ask an administrator to change Hub:Cards:Organisation.',
        'problem.card_domain_too_long': 'The card\'s QR code cannot hold this organisation\'s domain. Ask an '
            + 'administrator for a shorter Hub:Cards:Domain.',
        'problem.card_language_unsupported': 'This Hub\'s engine cannot print cards in this language yet.',
        'problem.card_domain_missing': 'Set Hub:Cards:Domain to check an attachment.',
        'problem.card_attachment_unchecked': 'The engine could not check this card\'s attachment.',
        'problem.card_key_forbidden': 'You may not manage the card signing key.',
        'problem.card_key_failed': 'The engine did not make a signing key.',
        'problem.unknown_card_key': 'The Hub holds no card key with this fingerprint.',
        'problem.invalid_revocation_date': 'The revocation date must lie between the key\'s creation and '
            + 'today.',
        'problem.disclosures_read_forbidden': 'You may not read disclosures.',
        'problem.disclosures_manage_forbidden': 'You may not manage disclosures.',
        'problem.withdrawn_not_shared': 'A withdrawn disclosure is not shared.',
        'problem.invalid_share_days': 'days must be a whole number of days from 1 to 30.',
        'problem.invalid_withdraw_reason': 'reason is required, 500 characters at most and no control '
            + 'character.',
        'problem.already_withdrawn': 'This disclosure is already withdrawn.',
        'problem.delivery_done': 'This delivery already went through.',
        'problem.disclosure_busy': 'A disclosure is being published; try again in a moment.',
        'problem.open_period': 'A card covers a closed period. Pick an earlier one.',
        'problem.uncovered_days': 'Some days of this period were not recorded, so its figures are not whole.',
        'problem.open_day': 'Some days of this period are still being counted. Try again tomorrow.',
        'problem.overflow': 'On some days of this period there were too many different problems to count '
            + 'each one, so its figures are not whole.',
        'problem.previous_uncovered_days': 'No curve for this period. The curve compares with the period '
            + 'before, and some of its days were not recorded. A story or a collective card still works.',
        'problem.previous_open_day': 'No curve for this period. The curve compares with the period before, '
            + 'and some of its days are still being counted. A story or a collective card still works.',
        'problem.previous_overflow': 'No curve for this period. The curve compares with the period before, '
            + 'and on some of its days there were too many different problems to count each one. A story or '
            + 'a collective card still works.',
        'problem.no_confirmed_fix': 'No fix of this period is confirmed by the measure yet.',
        'problem.unknown_fix': 'This fix is not among the confirmed fixes of the period. Pick one from the '
            + 'list.',
        'problem.unprintable_fix': 'This fix\'s service or endpoint name is too long or holds a hidden '
            + 'character, so it cannot be printed on a card.',
        'problem.no_total_io': 'The curve needs the total activity of each day, which this period or the one '
            + 'before does not hold yet.',
        'problem.curve_needs_whole_sources': 'The curve covers whole sources, and you read only part of one.',
        'problem.unprintable_scope': 'A source or environment name is too long or holds a hidden character, '
            + 'so it cannot be printed on a card. Ask an administrator to rename it.',
        'problem.scope_too_long': 'The environment and source names of this card are too long together to '
            + 'print on it. Pick an environment or a source.',
        'problem.unkept_fixes': 'Some fixes of this period were marked done before this Hub kept their '
            + 'details, so it cannot count them all. A story or a curve card still works.'
    };
})(globalThis);
