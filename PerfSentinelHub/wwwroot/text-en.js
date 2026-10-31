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
            + 'details, so it cannot count them all. A story or a curve card still works.',
        'error.run.source_unreachable': 'nothing answered at its address. Check the daemon or backend is up and '
            + 'that the Hub still has a route to it, then run this again.',
        'error.run.source_auth_failed': 'it answered and refused the Hub\'s credentials. Rotate the configured '
            + 'auth header or API key in the source\'s Secret. Nothing here will work until that is done.',
        'error.run.source_rejected_request': 'it answered and refused these arguments, usually an unknown '
            + 'service name or a window it does not keep. Check the service against the backend and try a '
            + 'shorter lookback.',
        'error.run.timeout': 'the run passed this Hub\'s time ceiling, or the backend took too long to answer. A '
            + 'wide window is the usual cause, halve the trace cap or shorten it. If it repeats instantly '
            + 'whatever the window, the address is probably not answering at all, check the endpoint.',
        'error.run.output_too_large': 'the source returned more than one run is allowed to hold. Narrow the '
            + 'window or lower the trace cap so less comes back, then run it again.',
        'error.run.binary_failed': 'the analysis binary failed for a reason none of the other codes covers, and '
            + 'nothing was stored. Run it once more. If it repeats, send this analysis ID to whoever operates '
            + 'the Hub.',
        'error.run.invalid_request': 'the arguments were rejected before the run started, so nothing was read '
            + 'and nothing was spent. Fix the trace ID or the lookback value and submit again.',
        'error.run.hub_memory_low': 'the Hub refused to start the analysis binary: its container had too little '
            + 'memory left for it, and running it anyway would have got the whole Hub killed. Retry when the '
            + 'Hub is quieter, or have whoever operates it raise its memory limit.',
        'error.run.internal': 'the Hub itself failed and never touched the source. Retry now. If it fails the '
            + 'same way, the Hub needs attention rather than your request.',
        'error.read.network_error': 'nothing answered at its address. Check the daemon is up and that the Hub '
            + 'still has a route to it.',
        'error.read.http_error': 'it answered with an error status, so it is running and reachable. This is the '
            + 'daemon refusing or failing the request rather than a network problem.',
        'error.read.timeout': 'it did not answer inside this Hub\'s HTTP timeout. Busy is as likely as down.',
        'error.read.invalid_status': 'it answered, but not with a status this Hub can read. Its /api/status has '
            + 'to carry a version string.',
        'error.read.response_too_large': 'it answered with more than this Hub reads in one go. A `[daemon]` '
            + 'section past that cap is worth reporting.',
        'error.read.hub_busy': 'the Hub capped how many daemon reads run at once and this one hit the cap. It '
            + 'clears in about a second.',
        'error.read.invalid_findings': 'it answered, but not with a findings list this Hub can read. A daemon '
            + 'this Hub does not support yet is the usual cause.',
        'error.read.database_full': 'the daemon answered, but the Hub\'s own database is full and could not '
            + 'store it. Grow the Hub\'s volume or shorten Hub:Retention, then look for a source creating '
            + 'abnormally many signatures.',
        'error.read.storage_error': 'the daemon answered, but the Hub\'s own database refused to store it. Check '
            + 'the Hub\'s logs and volume, not the daemon.',
        'error.title.source_unreachable': 'No connection could be opened.',
        'error.title.source_auth_failed': 'The source rejected the Hub\'s credentials.',
        'error.title.source_rejected_request': 'The source refused the request.',
        'error.title.timeout': 'The run exceeded the execution ceiling.',
        'error.title.output_too_large': 'The source returned too much data.',
        'error.title.binary_failed': 'The analysis binary failed.',
        'error.title.invalid_request': 'The arguments were rejected before the run started.',
        'error.title.hub_memory_low': 'The Hub had too little memory left to run it.',
        'error.title.internal': 'The Hub failed on its own side.',
        'health.database_full': 'Hub database full',
        'health.unreachable': 'unreachable {duration}',
        'health.signature_rate': '{count} new signatures in the last hour',
        'health.unobserved': 'not yet observed',
        'health.reachable': 'reachable',
        'unit.na': 'n/a',
        'unit.s': 's',
        'unit.min': 'm',
        'unit.h': 'h',
        'unit.d': 'd',
        'unit.word.m.one': '{count} minute',
        'unit.word.m.many': '{count} minutes',
        'unit.word.h.one': '{count} hour',
        'unit.word.h.many': '{count} hours',
        'unit.word.d.one': '{count} day',
        'unit.word.d.many': '{count} days',
        'unit.byte': 'B',
        'unit.kb': 'KB',
        'unit.mb': 'MB',
        'skew.behind': '{count} {unit} behind',
        'skew.ahead': '{count} {unit} ahead',
        'skew.unit.major.one': 'major',
        'skew.unit.major.many': 'major',
        'skew.unit.minor.one': 'minor',
        'skew.unit.minor.many': 'minor',
        'skew.unit.patch.one': 'patch',
        'skew.unit.patch.many': 'patch',
        'run.args.none': 'no parameters  ·  daemon in-memory snapshot',
        'new.sources.count': '{total} configured',
        'new.sources.count_missing.one': '{runnable} of {total} configured · {missing} needs analyses.run',
        'new.sources.count_missing.many': '{runnable} of {total} configured · {missing} need analyses.run',
        'weight.invalid.label': 'not a count',
        'weight.invalid.body': 'A run needs at least one trace. Drag the handle or type a number between 1 and '
            + '{cap}.',
        'weight.safe.label': 'comfortable',
        'weight.safe.body': 'Well inside what the sink returns whole. At this count the report is bounded by '
            + 'how much your traffic is doing wrong, not by the sink.',
        'weight.heavy.label': 'heavy',
        'weight.heavy.body': 'More traces means more findings, and every one of them reaches the report. The '
            + 'span trees stop at the embed cap, so the extra weight here is the list itself, not the trees.',
        'weight.ceiling.label': 'at the ceiling',
        'weight.ceiling.body': 'At this count the report still keeps every finding, and that is what makes it '
            + 'heavy: the file has no fixed ceiling and takes a moment to open. The run is also long enough to '
            + 'be worth watching against this Hub\'s time limit.',
        'weight.over.label': 'above the hard cap',
        'weight.over.body': 'The service rejects this before the run starts. Nothing is read and nothing is '
            + 'spent.',
        'daemon.monitor.api_key': 'Set `PERF_SENTINEL_DAEMON_API_KEY` to this daemon\'s read key first.',
        'daemon.monitor.header': 'This daemon is behind an `{header}` header. `query monitor` takes no such '
            + 'flag, so this command works only from somewhere that reaches the daemon without it.',
        'sources.overline': '// sources',
        'sources.title': 'Fleet health',
        'sources.sub': 'Every column is observed except the environment, which each source declares about '
            + 'itself. Sources are configured at deploy time, not from here.',
        'sources.hub_down': 'The Hub is not answering, so fleet health is unknown. This is the Hub itself, not '
            + 'any one source. Nothing below is shown rather than showing values that may be stale.',
        'sources.col.source': 'Source',
        'sources.col.type': 'Type',
        'sources.col.env': 'Env (declared)',
        'sources.col.health': 'Health',
        'sources.col.last_success': 'Last success',
        'sources.col.down_for': 'Unreachable for',
        'sources.col.producer': 'Producer',
        'sources.col.last_error': 'Last error',
        'sources.group.daemons': 'daemons',
        'sources.group.backends': 'trace backends',
        'sources.locked': 'details need sources.read',
        'sources.ago': '{duration} ago',
        'sources.never': 'never',
        'sources.producer.unknown': 'unknown',
        'sources.producer.unknown_title': 'This daemon reports a producer version, but the Hub has not had a '
            + 'successful response from it yet.',
        'sources.producer.none_title': 'A trace backend stores traces and detects nothing, so it reports no '
            + 'producer version.',
        'sources.skew_title': 'perf-sentinel is pre-1.0, so detectors change between minors. The Hub compares '
            + 'two version strings and cannot know whether this minor changed detection.',
        'daemon.setting.environment': 'The label this daemon stamps on every finding, which sets their '
            + 'confidence: staging reads as medium, production as high. Declared, like the column on the row '
            + 'above.',
        'daemon.setting.sampling_rate': 'The share of arriving traces it analyses. Below 100 % every aggregate '
            + 'in a snapshot is a sample of the traffic, not the traffic.',
        'daemon.setting.max_active_traces': 'How many traces it correlates in memory at once. The oldest is '
            + 'evicted past this, and a trace evicted while spans are still arriving is analysed incomplete.',
        'daemon.setting.trace_ttl_ms': 'How long a trace waits for more spans before it is closed and analysed.',
        'daemon.setting.max_events_per_trace': 'The ring buffer inside one trace. Its oldest spans drop once it '
            + 'fills, and the finding says nothing about what left.',
        'daemon.setting.max_payload_size': 'The largest single request body it will deserialize. Anything '
            + 'larger is refused whole, never truncated.',
        'daemon.setting.ingest_queue_capacity': 'Span batches buffered between the listeners and the event '
            + 'loop. A full queue pushes back on the sender as an OTLP 503.',
        'daemon.setting.analysis_queue_capacity': 'Batches waiting for detection. A full queue sheds whole '
            + 'batches, and a shed batch is silent to whoever sent it.',
        'daemon.setting.memory_high_water_pct': 'The share of memory above which it refuses new spans rather '
            + 'than meet the OOM killer. Zero disables the guard entirely.',
        'daemon.setting.max_retained_findings': 'Findings held for the query API. The oldest are evicted past '
            + 'this, so an old problem can leave a snapshot without anyone fixing it.',
        'daemon.setting.max_export_findings': 'Findings one export snapshot carries. The quality gate inside '
            + 'that snapshot counts those and no others.',
        'daemon.setting.max_retained_traces': 'Span trees kept so an export can draw them. Zero keeps none, and '
            + 'every finding in that export opens without a tree.',
        'daemon.setting.per_service_labels': 'Whether the finding, slow-duration and I/O series carry the '
            + 'service they came from. Off empties the label, and every service folds into one series.',
        'daemon.setting.per_grouping_labels': 'The same for the grouping beside it, the namespace by default. '
            + 'Off empties that label, and past the daemon\'s own cap on (service, grouping) pairs a grouping '
            + 'folds into `_other` on its own.',
        'daemon.setting.api_enabled': 'Whether the query API is served at all. The Hub reads this daemon '
            + 'through it, so a run from here needs it on.',
        'daemon.setting.listen_addr': 'Where the OTLP receivers and /metrics bind. An address outside loopback '
            + 'exposes both without authentication.',
        'daemon.setting.json_socket': 'Unix socket for native NDJSON ingestion, alongside OTLP.',
        'daemon.setting.tls_configured': 'TLS on the OTLP listeners. The Hub is told whether a certificate and '
            + 'key are set, never where they are.',
        'daemon.setting.ack_enabled': 'The daemon\'s own acknowledgement store. An acknowledged finding stays in '
            + 'the data and stops counting against the gate.',
        'daemon.setting.ack_api_key_set': 'Whether the acknowledgement routes require a key. The Hub is told '
            + 'that one exists, never what it is.',
        'daemon.setting.cors_allowed_origins': 'Browser origins the query API answers. Empty sends no CORS '
            + 'headers at all.',
        'daemon.setting.archive_configured': 'Whether it writes a report archive per window, for a later '
            + 'disclosure to read back.',
        'daemon.setting.correlation_enabled': 'Whether the cross-trace correlator runs.',
        'daemon.setting.correlation_window_ms': 'The rolling window over which two findings count as having '
            + 'happened together.',
        'daemon.setting.correlation_lag_threshold_ms': 'The largest gap between two findings that still counts '
            + 'as together.',
        'daemon.setting.correlation_min_co_occurrences': 'How many times a pair has to happen before it is '
            + 'reported at all.',
        'daemon.setting.correlation_min_confidence': 'How often the second finding follows the first, as a '
            + 'share of the first\'s own occurrences, before the pair is worth reporting.',
        'daemon.setting.correlation_max_tracked_pairs': 'Cap on tracked pairs. The least frequent are evicted '
            + 'past it, and the daemon says so above when that happens.',
        'daemon.setting.energy_source': 'Where the energy figure comes from: the power backends that measured '
            + 'each covered service, and how many services were modeled from I/O counts instead. Calibrated '
            + 'means calibration factors rescaled that model. Database or broker hosts the daemon counts inside '
            + 'its totals follow, each with its own source.',
        'daemon.setting.api_version': 'The Electricity Maps API version these figures were scored against.',
        'daemon.setting.emission_factor_type': 'Lifecycle counts the whole chain behind the electricity, direct '
            + 'counts only what the generation itself emits.',
        'daemon.setting.temporal_granularity': 'How finely grid intensity is resolved in time.',
        'daemon.setting.electricity_maps': 'Whether Electricity Maps is configured for live grid intensity. Off '
            + 'means each region took its grid operator\'s feed when one is configured, or the embedded table, '
            + 'which is a vintage rather than a reading.',
        'daemon.setting.grid_feeds': 'The grid operator feeds configured, each giving its regions a live '
            + 'intensity drawn from its operator\'s data. They count direct emissions only, so they read lower '
            + 'than a lifecycle figure, and Electricity Maps wins for a region both cover.',
        'daemon.setting.fingrid_dataset': 'The Fingrid dataset scored. 266 counts Finnish production, 265 '
            + 'consumption with imports included, and the two bases give different figures.',
        'daemon.setting.per_operation_coefficients': 'Whether each operation kind carries its own energy '
            + 'coefficient instead of one average across all I/O.',
        'daemon.setting.use_hourly_profiles': 'Whether the hour-by-hour shape of the grid is applied rather '
            + 'than a flat average.',
        'daemon.setting.embodied_per_request_gco2': 'Embodied carbon charged per request, the manufacture share '
            + 'of the figures rather than the electricity.',
        'daemon.setting.network_energy_per_byte_kwh': 'A coefficient the engine deprecated and no longer '
            + 'applies, published for configurations that still set it.',
        'detect.n_plus_one_min_occurrences': 'How many near-identical queries in one trace count as an N+1. '
            + 'Raise it and smaller loops stop being reported at all.',
        'detect.window_duration_ms': 'How close together those queries have to be. A shorter window splits one '
            + 'slow loop into several groups that each fall under the count.',
        'detect.slow_query_threshold_ms': 'Above this, one operation is called slow.',
        'detect.slow_query_min_occurrences': 'How many times a slow template has to appear before it is worth '
            + 'reporting. One slow query stays invisible below this.',
        'detect.max_fanout': 'Child spans under one parent before it counts as excessive fanout. The engine '
            + 'warns outside 5 to 1 000: too low floods the list, too high hides real fan-outs.',
        'detect.chatty_service_min_calls': 'Outbound HTTP calls in one trace before a service is called chatty. '
            + 'Critical fires at three times this.',
        'detect.pool_saturation_concurrent_threshold': 'Peak concurrent SQL spans on one service before the '
            + 'connection pool is called at risk. Set it to the pool size you actually run.',
        'detect.serialized_min_sequential': 'Sequential independent calls under one parent before they are '
            + 'worth parallelising.',
        'detect.sanitizer_aware_classification': 'How a run of identical parameterised queries is read once the '
            + 'agent has hidden their literals. `auto` calls it an N+1 on the ORM scope alone, `strict` also '
            + 'wants the timings to spread, `never` leaves it a redundant query, `always` reports an N+1.',
        'detect.sanitizer_aware_min_cv': 'How much those timings have to spread (standard deviation over mean) '
            + 'before `strict` or `auto` call the run an N+1 rather than a cached repeat. The same bar reads a '
            + 'repeated HTTP call. Raise it on a jittery runtime such as PHP-FPM, where repeats of one cached '
            + 'query spread past 0.5.',
        'daemon.group.declared': '// declared',
        'daemon.group.declared.sub': 'Not measured. This is what the daemon says it is.',
        'daemon.group.ingestion': '// ingestion and memory',
        'daemon.group.ingestion.sub': 'What it takes in, and what it drops when it cannot keep up.',
        'daemon.group.readers': '// what it keeps for readers',
        'daemon.group.readers.sub': 'Ring buffers behind the query API and the export. Past each one, the '
            + 'oldest goes.',
        'daemon.group.labels': '// metrics labels',
        'daemon.group.labels.sub': 'How /metrics splits its series. Emptying one folds its label away, which is '
            + 'breaking for whatever already reads it.',
        'daemon.group.listeners': '// listeners',
        'daemon.group.listeners.sub': 'Where it accepts spans, and whether it answers questions at all.',
        'daemon.group.subsystems': '// sub-systems',
        'daemon.group.subsystems.sub': 'Off unless somebody turned them on.',
        'daemon.group.correlation': '// correlation',
        'daemon.group.correlation.sub': 'Off by default. Every field under the first one applies only while it '
            + 'is on.',
        'daemon.reading': 'Reading {name}.',
        'daemon.audience': '// intended for devops',
        'daemon.source_note': 'Reported by this daemon over its query API. The Hub relays it and verifies none '
            + 'of it. Everything here is read-only, and every setting below is changed where the daemon is '
            + 'deployed, in its Helm values or its own configuration file, never from this Hub.',
        'daemon.terminal.head': '// the same view in your terminal',
        'daemon.terminal.sub': 'The same figures, plus the tabs this screen leaves out.',
        'daemon.terminal.copy': 'Copy the monitor command for {name}',
        'daemon.terminal.note': '`query monitor` carries the energy and carbon breakdown this screen only '
            + 'summarises. It re-reads on the interval chosen above, and this line changes with it, so the '
            + 'terminal and this row never disagree about how often the daemon is asked. Set that to off and '
            + 'the command drops `--refresh`, leaving the engine its own default of five seconds.',
        'daemon.error.returned': 'Reading this daemon\'s settings returned ',
        'daemon.error.meaning': ': {meaning}',
        'daemon.error.unreached': 'the Hub could not reach it.',
        'daemon.error.sub': 'The row above still shows the last collection state, which is a different '
            + 'observation made at a different time.',
        'daemon.error.retry': 'This row asks again on its own, and shows the daemon as soon as it answers.',
        'daemon.verdict.ok': 'nominal',
        'daemon.verdict.near_capacity': 'near capacity',
        'daemon.verdict.advised': 'advised',
        'daemon.verdict.unknown': 'not measurable',
        'daemon.now.head': '// right now',
        'daemon.now.help': 'Read from the daemon on the interval below. A tick asks the daemon for its status '
            + 'alone, and once a minute the full export runs to refresh the hints, so the interval prices a '
            + 'small read, not the heavy one.',
        'daemon.gauge.traces': 'active traces',
        'daemon.gauge.queue': 'analysis queue',
        'daemon.gauge.findings': 'findings stored',
        'daemon.gauge.uptime': 'uptime',
        'daemon.unknown': 'unknown',
        'daemon.now.lead': 'Each figure is shown against the cap it runs into. Being near one is not a problem '
            + 'by itself, and this screen does not decide that it is: the daemon does, from counters the Hub '
            + 'cannot see.',
        'daemon.hints.head': '// what the daemon recommends',
        'daemon.hints.help': 'These come from counters inside the daemon that no report and no dashboard '
            + 'carries. The Hub relays the sentences and writes none of its own.',
        'daemon.hints.sub': 'Written by the daemon, not by the Hub.',
        'daemon.hints.unread': 'The export this screen reads hints from could not be read: `{reason}`. Whatever '
            + 'the daemon recommends right now is unknown, which is not the same thing as nothing.',
        'daemon.hints.none': 'Nothing. The daemon emits a hint when its own counters show a setting is '
            + 'undersized for the load it is taking. Silence here means those counters were clean at the '
            + 'instant it was read, not that the settings are right.',
        'daemon.refresh.label': 'Re-read interval',
        'daemon.refresh.off': 'off',
        'daemon.refresh.every': 'every',
        'daemon.refresh.read': 'Read {duration} ago.',
        'daemon.refresh.stopped': 'Not re-reading.',
        'daemon.refresh.next': 'Next in {seconds} s.',
        'daemon.settings.more': 'The {count} applied settings, and what this daemon changed',
        'daemon.settings.detects': 'What this daemon detects with',
        'daemon.config.api_disabled': 'This daemon does not serve its configuration: `api_enabled` is off in '
            + 'its own `[daemon]` section. Everything above came from the export instead.',
        'daemon.config.unreadable': 'This daemon answered for its configuration with something the Hub could '
            + 'not relay, an error status or a body that is not the `[daemon]` object. The gauges above are the '
            + 'same daemon answering fine.',
        'daemon.config.unanswered': 'This daemon did not answer for its configuration, so none is shown rather '
            + 'than a copy from some earlier moment.',
        'daemon.group.detection': '// detection thresholds',
        'daemon.group.detection.sub': 'What counts as a problem. The same knobs the launcher lets a run '
            + 'override on a backend.',
        'daemon.group.scoring': '// carbon scoring',
        'daemon.group.scoring.sub': 'Where the energy figures come from, not what they are.',
        'daemon.settings.preamble': 'A value this daemon changed is marked, with the engine\'s default beside '
            + 'it. Compared against perf-sentinel `{version}`, the binary this Hub embeds. It covers the '
            + '`[daemon]` and `[detection]` sections and the scoring half of `[green]`. The gate thresholds '
            + 'under `[thresholds]` are not published as a section, so a value set there is real and simply not '
            + 'visible here.',
        'daemon.settings.preamble_skew': ' This daemon runs `{version}`, so a default could have moved between '
            + 'the two and a value marked as changed may only be a different default.',
        'daemon.settings.default': 'default {value}',
        'daemon.value.not_set': '(not set)',
        'daemon.value.off': 'off',
        'daemon.value.none': '(none)',
        'daemon.value.configured': 'configured',
        'daemon.value.unconfigured': 'not configured',
        'daemon.value.set': 'set',
        'daemon.value.unset': 'unset',
        'daemon.value.yes': 'yes',
        'daemon.value.no': 'no',
        'daemon.settings.changed.one': '{count} changed',
        'daemon.settings.changed.many': '{count} changed',
        'daemon.hints.dropped.one': '{count} more arrived than the Hub relays in one view.',
        'daemon.hints.dropped.many': '{count} more arrived than the Hub relays in one view.',
        'new.overline': '// new analysis',
        'new.title': 'Run an analysis',
        'new.no_source': 'This Hub has no configured source.',
        'new.no_grant': 'No grant lets you start an analysis on this Hub. An administrator grants access in the '
            + 'Hub\'s configuration.',
        'new.terminal.head': '// prefer your terminal?',
        'new.terminal.sub': 'The same run, spelled out.',
        'new.terminal.help': 'The Hub runs this same binary. What is missing here is the JSON output and the '
            + 'second command that renders it, which exist so the Hub can build a dashboard. A terminal does '
            + 'not need either.',
        'new.terminal.copy': 'Copy the analysis command',
        'new.terminal.same': 'This is the same request the button above sends, written as the engine\'s own '
            + 'arguments. It runs wherever perf-sentinel is installed and does not pass through this Hub: no '
            + 'worker slot, no queue, and no report kept here for {hours} hours.',
        'new.terminal.prints': 'It prints its findings to the terminal. There is no dashboard at the end of it '
            + 'and no link to share, which is the trade for not spending a worker.',
        'new.terminal.trace': 'An ID resolves to exactly one trace, so the engine takes neither a window nor a '
            + 'trace cap here, exactly as the form above stops offering them.',
        'new.terminal.fill_service': 'Fill in the service name above. The command carries an empty one as it '
            + 'stands, and the engine refuses it as it stands.',
        'new.terminal.toml.one': 'Put the `perf-sentinel.toml` below next to where you run the command. The '
            + 'threshold you moved has no command-line flag, so the engine reads it from that file.',
        'new.terminal.toml.many': 'Put the `perf-sentinel.toml` below next to where you run the command. The '
            + 'thresholds you moved have no command-line flag, so the engine reads them from that file.',
        'new.toml.sub': 'Only the thresholds you changed.',
        'new.toml.copy': 'Copy the perf-sentinel.toml fragment',
        'new.toml.defaults': 'Every threshold this file leaves out keeps the engine\'s own default, and the Hub '
            + 'only records a value that actually departs from one. A run launched from the button above '
            + 'carries the same numbers, so the two are comparable with each other.',
        'new.toml.where': 'Put it in the directory you run the command from. The `-c` above makes it required, '
            + 'so a run that cannot find it stops instead of quietly falling back to the engine\'s own defaults, '
            + 'which are the numbers you just moved away from.',
        'new.hub_down': 'The Hub is not answering. This is the Hub itself and not any one source, so nothing '
            + 'can be launched from here until it is back. Reload once it responds again.',
        'new.handoff.long': '{duration} long.',
        'new.handoff.around': '{before} before it to {after} after.',
        'new.handoff.episode': 'a correlation episode of {name} from the correlations screen, ',
        'new.handoff.incident': 'the {kind} of {name} from the incidents screen, ',
        'new.handoff.incident_kind': 'incident',
        'new.handoff.window': 'Window of {origin}{span}',
        'new.handoff.namespace': ' An analysis takes a service and no namespace, so this one covers {service} '
            + 'in every namespace.',
        'new.handoff.daemon': ' A daemon takes no window. Pick a trace backend on the left to run it.',
        'new.trace_handoff.swap': 'Use the other trace',
        'new.trace_handoff.lead': 'A trace of the last co-occurrence of a correlated pair, from the '
            + 'correlations screen.',
        'new.trace_handoff.other': ' The pair\'s other trace is {trace}.',
        'new.trace_handoff.daemon': ' A daemon takes no trace ID. Pick a trace backend on the left to run it.',
        'new.source.head': '// source',
        'new.source.label': 'Source',
        'new.source.note': 'A dashed outline marks a value the source declares about itself. The Hub never '
            + 'measures it. A misconfigured deployment can label production as staging.',
        'new.source.aria_reachable': '{name}, reachable',
        'new.source.aria_unreachable': '{name}, unreachable',
        'new.source.producer': 'producer {version}',
        'new.source.producer_unknown': 'producer unknown',
        'new.source.no_producer': 'no producer version',
        'new.params.pick': 'Pick a source to see what it takes.',
        'new.params.head': '// parameters',
        'new.query.head': '// query',
        'new.daemon.none': 'No parameters. A daemon snapshot is whatever it holds in memory right now.',
        'new.daemon.sub': 'The window is its ring buffer, so there is nothing to widen. What it holds and how '
            + 'it is configured are on its row in Sources.',
        'new.daemon.open_row': 'Open its row on Sources',
        'new.field.trace': 'Trace ID',
        'new.field.trace_note': 'An ID resolves to exactly one trace, so neither the window nor the trace cap '
            + 'applies.',
        'new.field.service': 'Service name',
        'new.field.range': 'Time range',
        'new.field.range_absolute': 'absolute, fixed at submission',
        'new.field.range_relative': 'relative to the moment the run starts',
        'new.mode.label': 'Selection mode',
        'new.mode.service': 'Service',
        'new.mode.field': 'Select traces by',
        'new.mode.gloss': 'one or the other, never both',
        'new.range.last': 'Last {span}',
        'new.range.aria': 'Time range, {window}',
        'new.range.wide': 'A wider window returns no more data. The run still stops at the trace cap, so the '
            + 'result is a sample spread over the period rather than the period itself.',
        'new.range.timeout': 'The whole scan has to finish inside the {seconds}-second ceiling, which is '
            + 'usually the limit met first. Expect a timeout rather than a result.',
        'new.range.retention': 'This source declares it keeps {duration} of traces. A window beyond that comes '
            + 'back short, or is refused as source_rejected_request.',
        'new.range.undeclared': 'Nobody declared how far back this source keeps traces, so the Hub cannot tell '
            + 'whether it can answer this window at all.',
        'new.picker.apply_range': 'Apply range',
        'new.picker.order': 'The start must come before the end.',
        'new.picker.future': 'The end cannot be in the future.',
        'new.picker.selected': '{duration} selected',
        'new.picker.from': 'From',
        'new.picker.to': 'To',
        'new.picker.custom': 'Custom relative',
        'new.picker.quick': 'Quick ranges',
        'new.picker.unit': 'Unit',
        'new.picker.apply': 'Apply',
        'new.picker.last': 'Last',
        'new.traces.label': 'Max traces',
        'new.traces.help': 'This and the window travel to the backend in one search, the window as its time '
            + 'bounds, this as its limit. It is a ceiling and not a target, so a window holding fewer traces '
            + 'returns fewer. When it holds more, the backend picks which ones and they are not an even spread, '
            + 'so narrow the window when you need a given stretch covered. Traces come back whole rather than '
            + 'sampled, so this number bounds what the run costs.',
        'new.traces.topic': 'max traces',
        'new.traces.gloss': 'how much comes back, not how far back',
        'new.weighed.head': '// what your own runs weighed',
        'new.weighed.sub': 'Measured on this source, not predicted.',
        'new.weighed.row': '{traces} traces, {findings} findings, {duration} ago',
        'new.traces.at_least': 'at least 1',
        'new.traces.over': 'above the hard cap of {cap}, the service will reject this',
        'new.traces.cap': 'hard cap {cap}',
        'new.band.safe': 'safe',
        'new.band.heavy': 'heavy',
        'new.band.cap': 'cap',
        'new.sink.floor': 'The floor. Fonts, styles and the dashboard itself, present in every report whether '
            + 'it found one problem or none.',
        'new.sink.every': 'every finding',
        'new.sink.every_body': 'Every run this Hub executes renders every finding it found, at any size. The '
            + 'count on the dashboard is the count that was found.',
        'new.sink.trees': '{count} trees',
        'new.sink.trees_body': 'Span trees embedded, for the findings with the highest aggregate impact. The '
            + 'rest open without a tree and say so, with the trace id to read that one on its own, which the '
            + 'Trace ID mode above runs directly. Set by whoever operates this Hub.',
        'new.sink.offenders': 'Hard cap on the top offenders embedded for the Carbon tab, whatever the run '
            + 'size. The full ranking is still computed, only the embed is capped.',
        'new.sink.no_ceiling': 'no ceiling',
        'new.sink.no_ceiling_body': 'The file has no size target of its own once every finding is kept. A run '
            + 'that finds a great deal produces a report that takes a moment to open.',
        'new.sink.head': '// what comes back, and what it caps',
        'new.sink.sub': 'From the sink and this Hub\'s settings, not predictions.',
        'new.traces.ack': 'I accept a long run and a heavy report.',
        'new.skew.title': '{name} runs {version}, {gap} the {engine} binary embedded in the Hub.',
        'new.skew.behind': 'perf-sentinel is pre-1.0, so detectors change between minors. A detector added in '
            + '{engine} does not run on this producer at all, and its absence looks exactly like a clean '
            + 'service. Read a low finding count from this source as unmeasured, not as healthy.',
        'new.skew.ahead': 'Envelopes are additive, so nothing breaks. Findings from a detector this Hub does '
            + 'not know about arrive unnamed. The Hub compares two version strings and cannot know whether this '
            + 'minor changed detection at all.',
        'new.upgrade.get': 'Get {engine}: ',
        'new.upgrade.release': 'release notes and binaries',
        'new.upgrade.or_chart': ', or the chart this daemon is deployed from, ',
        'new.upgrade.charts': 'every chart version and the engine it ships',
        'new.upgrade.chart_is': '. The chart itself is ',
        'new.upgrade.end': '.',
        'new.unreachable.title': '{name} has been unreachable for {duration}.',
        'new.unreachable.last_success': 'Last successful contact {duration} ago.',
        'new.unreachable.returned': 'The last attempt returned ',
        'new.unreachable.cost': 'Running now will consume a worker slot and will almost certainly end with the '
            + 'same code.',
        'new.unreachable.anyway': 'Run it anyway',
        'new.cost.traces': 'traces',
        'new.cost.cap': 'Hard cap per run',
        'new.cost.cap_note': 'The service rejects anything above it.',
        'new.cost.timeout': 'Timeout',
        'new.cost.timeout_note': 'Then the run is killed, marked timeout.',
        'new.cost.workers': 'workers',
        'new.cost.queued.one': '1 job queued now',
        'new.cost.queued.many': '{count} jobs queued now',
        'new.cost.workers_note': 'That many runs at a time across the whole Hub.',
        'new.cost.retention': 'Report retention',
        'new.cost.retention_note': 'Then the file is deleted. Links die.',
        'new.cost.head': '// what this run costs',
        'new.cost.sub': 'Reported by the service, not assumed.',
        'new.block.source': 'Pick a source.',
        'new.block.engine': 'This Hub has no analysis engine configured.',
        'new.block.unreachable': 'Confirm you want to run against an unreachable source.',
        'new.block.trace': 'Enter a trace ID.',
        'new.block.service': 'Enter a service name.',
        'new.block.over': 'The trace cap is above what the service accepts.',
        'new.block.invalid': 'A run needs at least one trace.',
        'new.block.heavy': 'Confirm the long run and heavy report.',
        'new.submit.daemon': 'Takes a snapshot of what {name} holds in memory. No query is sent to a trace '
            + 'backend. {queue}',
        'new.submit.trace': 'Fetches one trace by ID from {name}. {queue}',
        'new.submit.service': 'Reads up to {count} traces for {service} across {window} of {name}. {queue}',
        'new.submit.a_service': 'a service',
        'new.submit.selected_window': 'the selected window',
        'new.submit.last': 'the last {span}',
        'new.queue.none': 'Nothing is queued ahead of it.',
        'new.queue.one': 'Queued behind 1 job.',
        'new.queue.many': 'Queued behind {count} jobs.',
        'new.submit.button': 'Run analysis',
        'new.submit.refused': 'The Hub refused the request.',
        'status.queued': 'queued',
        'status.running': 'running',
        'status.succeeded': 'succeeded',
        'status.failed': 'failed',
        'status.interrupted': 'interrupted',
        'status.expired': 'expired',
        'status.empty': 'empty',
        'run.unknown.title': 'No analysis with that ID.',
        'run.unknown.sub': 'This Hub holds no analysis under it. The link may be mistyped, or come from another '
            + 'Hub.',
        'run.all': 'All analyses',
        'run.queued.headline': 'Waiting for a worker.',
        'run.queued.sub': 'Every worker is busy. Nothing has been read from {name} yet, so nothing has been '
            + 'spent.',
        'run.running.headline': 'Reading {name}.',
        'run.running.sub': 'A worker holds this job. The next thing that happens is a result or a failure, with '
            + 'nothing in between.',
        'run.empty.headline': 'It succeeded, and there is nothing in it.',
        'run.empty.sub': 'This is not a failure and not an error. The source answered correctly, and the answer '
            + 'was zero traces.',
        'run.succeeded.headline_caveat.one': '{findings} findings, and a caveat you should read first.',
        'run.succeeded.headline_caveat.many': '{findings} findings, and {caveats} caveats you should read '
            + 'first.',
        'run.succeeded.headline': '{findings} findings.',
        'run.succeeded.sub': 'The report is ready and will be deleted in {duration}.',
        'run.interrupted.headline': 'The service restarted while this was running.',
        'run.interrupted.sub': 'It stopped after {duration} of work. This is a resumption, not an error to '
            + 'investigate.',
        'run.expired.headline': 'This report was deleted.',
        'run.expired.sub': 'Reports live {hours} hours. This one expired {duration} ago and the file is gone.',
        'run.failed.headline': 'Failed: {code}.',
        'run.failed.sub': 'The Hub does not expose the process\'s error output by design. It gives one code out '
            + 'of eight, and this is what that code means.',
        'run.log.accepted': 'accepted',
        'run.log.accepted_detail': 'the request was validated and queued',
        'run.log.started': 'started',
        'run.log.started_daemon': 'reading the daemon\'s in-memory store',
        'run.log.started_backend': 'reading {kind}',
        'run.log.running': 'running',
        'run.log.running_detail': 'no further event until the engine returns',
        'run.log.waiting': 'waiting',
        'run.log.waiting_detail': 'every worker is busy, nothing has been read yet',
        'run.log.succeeded': 'succeeded',
        'run.log.written_retained': 'report written, retained {hours} h',
        'run.log.failed': 'failed',
        'run.log.interrupted': 'interrupted',
        'run.log.interrupted_detail': 'the Hub restarted, the run was abandoned and not replayed',
        'run.log.written': 'report written',
        'run.log.deleted': 'deleted',
        'run.log.deleted_detail': 'retention reached, the report no longer exists',
        'run.log.label': 'Service events',
        'run.log.head': '// service events',
        'run.log.note': 'Only what the Hub actually recorded.',
        'run.log.closing_live': 'The engine reports nothing between start and finish. There is no percentage to '
            + 'show and no arrival time to predict, so this screen shows neither. Only the events above, the '
            + 'time spent, and the ceiling at which the service gives up. Expect one more line, not a stream.',
        'run.log.closing_instant': 'This run was read and finished in one step, so every line above was written '
            + 'at once. It is a receipt of what happened, not a feed.',
        'run.log.closing': 'Every line above is a timestamp the Hub wrote. Nothing here is interpolated.',
        'run.elapsed': '// elapsed',
        'run.ceiling.queued': 'The {seconds}-second ceiling starts when a worker picks the job up, not now.',
        'run.ceiling.total': 'Total time the run occupied a worker.',
        'run.ceiling.past': 'Past the {seconds} s ceiling. The run should already have been killed and marked '
            + 'timeout.',
        'run.ceiling.left': 'Hard stop at {seconds} s, then the run is killed and marked timeout. {duration} of '
            + 'ceiling left.',
        'run.fact.source': 'source',
        'run.fact.type': 'type',
        'run.fact.window': 'window',
        'run.fact.parameters': 'parameters',
        'run.fact.none': 'none',
        'run.fact.requested_by': 'requested by',
        'run.fact.detected_by': 'detected by',
        'run.fact.producer': 'producer',
        'run.fact.engine': 'engine',
        'run.fact.unknown': 'not yet known',
        'run.fact.expires': 'expires',
        'run.request': '// request',
        'run.expiry.pending': 'not until it succeeds',
        'run.expiry.in': 'in {duration}',
        'run.outcome.result': 'result',
        'run.outcome.gate_passed': 'The quality gate passed. The dashboard holds the full detail.',
        'run.outcome.gate_failed': 'The quality gate did not pass. The dashboard holds the full detail.',
        'run.count.gate': 'quality gate',
        'run.count.findings': 'findings',
        'run.count.found': 'found',
        'run.count.critical': 'critical',
        'run.count.warning': 'warning',
        'run.count.info': 'info',
        'run.count.traces': 'traces read',
        'run.outcome.open': 'Open the dashboard',
        'run.outcome.open_note': 'Opens on this origin. The link dies in {duration}.',
        'run.outcome.empty': 'empty result',
        'run.outcome.empty_body': '{name} had nothing for the engine to analyse. The report exists, and it is '
            + 'blank. Opening it will show an empty dashboard. That is the expected outcome, not a rendering '
            + 'fault.',
        'run.outcome.wait_rerun': 'Wait and run it again',
        'run.outcome.open_blank': 'Open the blank dashboard anyway',
        'run.outcome.empty_note': 'A quality gate that passes on zero traces has not measured anything.',
        'run.outcome.failed_body': '{name}: {meaning}',
        'run.outcome.unnamed': 'it failed for an unnamed reason.',
        'run.outcome.rerun': 'Run it again',
        'run.outcome.check_source': 'Check the source',
        'run.outcome.failed_note': 'Nothing was stored, so nothing expires.',
        'run.outcome.resume': 'resume',
        'run.outcome.resume_body': 'The Hub never replays an interrupted run on its own. A silent retry could '
            + 'fire a second heavy query at {name} without anyone asking for it, so the decision stays yours. '
            + 'The parameters are unchanged and ready to send again.',
        'run.outcome.resume_button': 'Resume with the same parameters',
        'run.outcome.expired': 'expired',
        'run.outcome.expired_body': 'Retention is not configurable from here. Running the same analysis again '
            + 'produces a new report with a new clock. It will not reproduce the old one, because the source '
            + 'has moved on since then.',
        'run.trimmed': 'trimmed',
        'run.trimmed_body': '{found} findings were found and {kept} are in the report. The sink dropped the '
            + 'rest to fit, critical last, so what survived is what mattered most.',
        'run.resubmit_refused': 'The Hub refused the resubmission.',
        'run.hub_down': 'The Hub is not answering. This page keeps what it last read and asks again.',
        'new.advanced.warning': 'Warning, expert settings.',
        'new.advanced.head': '// advanced users only',
        'new.advanced.what': ' · what the analysis looks for',
        'new.advanced.title': 'For operators who know what these thresholds do.',
        'new.advanced.body': 'Moved carelessly, they hide real problems or flood the report with noise, and '
            + 'which direction does which differs per threshold. If you are not sure, leave them.',
        'new.advanced.lead': 'These are the engine\'s detection thresholds. They decide what counts as a '
            + 'problem, not how the report is written: raising one does not make the run lighter, it makes the '
            + 'engine stop reporting the smaller cases. A run records the ones you changed, and the recent list '
            + 'flags counts that came from different thresholds, because they are not comparable.',
        'new.advanced.reset': 'Reset every threshold',
        'new.knob.reset': 'Put {name} back to {value}',
        'new.toml.head': '// perf-sentinel.toml',
        'new.cost.queued.none': '0 jobs queued now',
        'incident.kind.oom_kill': 'OOM kill',
        'incident.kind.memory_saturation': 'memory saturation',
        'incident.kind.restart': 'restart',
        'incident.kind.deploy': 'deploy',
        'incident.kind.other': 'other',
        'finding.type.n_plus_one_sql': 'N+1 SQL',
        'finding.type.n_plus_one_http': 'N+1 HTTP',
        'finding.type.n_plus_one_messaging': 'N+1 messaging',
        'finding.type.redundant_sql': 'Redundant SQL',
        'finding.type.redundant_http': 'Redundant HTTP',
        'finding.type.slow_sql': 'Slow SQL',
        'finding.type.slow_http': 'Slow HTTP',
        'finding.type.slow_messaging': 'Slow messaging',
        'finding.type.excessive_fanout': 'Excessive fanout',
        'finding.type.chatty_service': 'Chatty service',
        'finding.type.pool_saturation': 'Pool saturation',
        'finding.type.serialized_calls': 'Serialized calls',
        'incident.filter.more': 'More filters',
        'incident.filter.more_set.one': 'More filters, {count} set',
        'incident.filter.more_set.other': 'More filters, {count} set',
        'incident.phase.before': ' before the incident',
        'incident.phase.after_restart': ' after the restart',
        'incident.phase.after_deploy': ' after the deploy',
        'incident.phase.after': ' after the incident',
        'incident.read.absent': 'it publishes no incidents route',
        'incident.read.unauthorized': 'it refused the Hub\'s key',
        'incident.read.error': 'the read failed',
        'incident.copy.never': '{name}: never read',
        'incident.copy.read': '{name}: read {duration} ago',
        'unit.ms': 'ms',
        'correlation.pair.side': '{type} in {service}',
        'correlation.pair.label': '{first}, then {then}',
        'correlation.ended_stale': 'stale',
        'ack.command.reason': 'why it can wait',
        'ack.note.unconfigured': 'The Hub no longer configures this source.',
        'ack.note.no_credential': 'The Hub holds no ack credential for this source.',
        'ack.note.ci_baseline': 'Acknowledged by the CI baseline, which is edited through a pull request.',
        'ack.note.unknown_state': 'The ack state of this daemon is unknown, acks_state is {state}. Acknowledge and '
            + 'Revoke both apply, the daemon refuses the one that does not, and its answer shows in the result line.',
        'ack.note.never_read': 'never read',
        'gap.tech.uncovered_days': 'days the ledger does not cover',
        'gap.tech.open_day': 'a day still open',
        'gap.tech.restart': 'a daemon restart',
        'gap.tech.sampling': 'sampled traces',
        'gap.tech.shed': 'traces shed under memory pressure',
        'gap.tech.ingest_rejected': 'rejected ingest',
        'gap.tech.overflow': 'a full ledger',
        'gap.tech.multi_instance': 'several instances behind one source',
        'gap.pilotage.uncovered_days': 'days with no data',
        'gap.pilotage.open_day': 'a day not finished yet',
        'gap.pilotage.restart': 'a restart of the measuring tool',
        'gap.pilotage.sampling': 'only part of the traffic was looked at',
        'gap.pilotage.shed': 'some activity was dropped under load',
        'gap.pilotage.ingest_rejected': 'some data was refused',
        'gap.pilotage.overflow': 'too many problems to count them all',
        'gap.pilotage.multi_instance': 'several copies of one service',
        'gap.pilotage.other': 'another measurement gap',
        'ack.expiry.not_a_date': 'The expiry is not a date.',
        'ack.expiry.past': 'The expiry is in the past.',
        'ack.expiry.permanent': 'No date: the ack is permanent.',
        'ack.expiry.until': 'Holds until {day} 23:59:59 UTC.',
        'ack.sources.one': '{count} source',
        'ack.sources.other': '{count} sources',
        'ack.plan.no_ack_source': 'No checked source can take an ack.',
        'ack.plan.no_reason': 'An ack needs a reason.',
        'ack.plan.ack_until': 'Acknowledge writes to {sources}, until {expires}.',
        'ack.plan.ack_permanent': 'Acknowledge writes to {sources}, with no expiry.',
        'ack.plan.revoke_unsure': 'Revoke asks {sources} to remove the ack, if there is one.',
        'ack.plan.revoke': 'Revoke removes the ack on {sources}.',
        'ack.plan.none': 'No checked source can take an ack or a revoke.',
        'ack.refusal.0': 'The Hub did not answer.',
        'ack.refusal.413': 'The request is larger than the Hub accepts.',
        'ack.refusal.503': 'The Hub is already relaying two acks. Try again in a moment.',
        'ack.result.revoked': 'ack revoked.',
        'ack.result.acknowledged': 'acknowledged.',
        'ack.result.refused': 'The Hub refused the request with status {status}.',
        'ack.result.line': '{name}: {said}',
        'access.no_grant': 'No grant on this Hub reaches you. An administrator grants access in the Hub\'s '
            + 'configuration.',
        'access.expiry.expired': 'expired',
        'access.expiry.within_day': 'in less than a day',
        'access.expiry.days.one': 'in {count} day',
        'access.expiry.days.other': 'in {count} days',
        'access.caller.session': 'a browser session',
        'access.caller.terminal_token': 'a terminal token',
        'access.caller.machine_token': 'a machine token',
        'access.until': ', until {when}',
        'access.signed_in': 'Signed in as {who}, through {via}{until}.',
        'access.group_note': 'A group change at your provider applies at your next sign-in.',
        'access.subject_label': 'Your id, for an administrator: ',
        'access.no_reach': 'No grant reaches your groups. An administrator grants access in the Hub\'s configuration.',
        'access.reach.everything': 'Everything, including records no project covers',
        'access.reach.admins': 'your id, in Admins',
        'coverage.snippet.line1': '# Add the lines a team owns to the patterns of its project in hub.access.projects,',
        'coverage.snippet.line2': '# then restart the Hub. A finding without a service needs a source or attribute '
            + 'pattern.',
        'access.request.none': 'none',
        'access.request.text': 'Please grant access to the Perf Sentinel Hub.\nname: {name}\nid: {id}\ngroups: '
            + '{groups}',
        'access.revoke.done': 'Revoked the token of {owner} created {created}. That terminal gets a 401 on its next '
            + 'request.',
        'access.revoke.gone': 'The token of {owner} created {created} was already gone.',
        'access.revoke.no_answer': 'The Hub did not answer. The token is still valid. Try again in a moment.',
        'access.revoke.failed': 'The Hub could not revoke this token (status {status}). It is still valid. Try again '
            + 'in a moment.',
        'access.token.unknown': 'unknown',
        'access.token.never': 'never',
        'incidents.col.started': 'Started',
        'incidents.col.namespace': 'Namespace',
        'incidents.col.service': 'Service',
        'incidents.col.kind': 'Kind',
        'incidents.col.ended': 'Ended',
        'incidents.col.findings': 'Findings',
        'incidents.col.capture': 'Capture',
        'incidents.col.source': 'Source',
        'incidents.capture.complete': 'The ring still reached the whole window when the incident was frozen: what is '
            + 'here is what fired.',
        'incidents.capture.partial': 'The ring had already evicted part of the window when the incident was frozen. '
            + 'Findings that fired earlier in the window are missing here, and the daemon\'s NDJSON archive may '
            + 'still hold them.',
        'incidents.capture.empty': 'The ring held nothing when the incident was frozen.',
        'incidents.capture_word.complete': 'complete',
        'incidents.capture_word.partial': 'partial',
        'incidents.capture_word.empty': 'empty',
        'incidents.overline': '// incidents',
        'incidents.title': 'Incidents the daemons recorded',
        'incidents.sub': 'What was already burning when your alerting posted a restart, an OOM kill or a deploy: '
            + 'each row carries the findings its daemon froze from the minutes before. ',
        'incidents.help': 'The daemon is the author: the Hub copies its record, re-derives nothing, and keeps the '
            + 'copy after the daemon\'s own ring has let it go. Opening this screen reads every daemon, so the rows '
            + 'are what the fleet holds now, and under the table is when each one was last read. A deploy is posted '
            + 'to freeze what was already firing before the rollout, so a restart it causes is not read as a crash.',
        'incidents.topic': 'incidents',
        'incidents.empty.title': 'No incident recorded.',
        'incidents.empty.body': 'An incident exists only when a daemon with `[daemon.incidents]` enabled receives '
            + 'one from the operator\'s alerting. Nothing here means none was posted, or that no daemon read just '
            + 'now publishes them yet.',
        'incidents.empty.body_filtered': 'An incident exists only when a daemon with `[daemon.incidents]` enabled '
            + 'receives one from the operator\'s alerting. Nothing here means none was posted for this filter, or '
            + 'that no daemon read just now publishes them yet.',
        'incidents.older': 'Load older incidents',
        'incidents.note': 'Started and Ended are the alerting\'s own stamps, relayed by the daemon. The Hub keeps an '
            + 'incident for its findings retention, on its own clock, and a daemon that answers 404 on this route '
            + 'runs a release before 0.20.0.',
        'incidents.unauthorized.lead': '{name} refused the Hub\'s key on its incidents route, so its incidents are '
            + 'not here. Its findings are still collected. The daemon\'s ',
        'incidents.unauthorized.goes': ' goes in this source\'s ',
        'incidents.unauthorized.sent_as': ', sent as ',
        'filter.kind': 'kind',
        'filter.kind.every': 'every kind',
        'filter.service': 'service',
        'filter.service.every': 'every service',
        'filter.namespace': 'namespace',
        'filter.namespace.every': 'every namespace',
        'filter.environment': 'environment',
        'filter.environment.every': 'every environment',
        'filter.daemon': 'daemon',
        'filter.daemon.every': 'every daemon',
        'incidents.reading': 'Reading the daemons',
        'incidents.read_now': 'Read the daemons now',
        'incidents.error.stale': 'The daemons were not read just now, so these rows are the copy the last read left. '
            + 'The line under the table says how old each one is. Read again in a moment.',
        'incidents.error.unread': 'The Hub did not return the incidents, so nothing here is a reading of the fleet. '
            + 'This is the Hub itself, not any daemon.',
        'incidents.error.page': 'The Hub is not answering, so the older rows are unknown. This is the Hub itself, '
            + 'not any daemon. Rows already on screen are the last page it did answer.',
        'incidents.copy_note': 'Every row comes from a daemon, copied here when its ring was read. ',
        'incidents.ended_after': 'after {duration}',
        'incidents.still_open': 'still open',
        'incidents.unknown': 'unknown',
        'incidents.panel.reading': 'Reading the incident.',
        'incidents.panel.gone': 'The Hub no longer holds this incident. Retention removed it between the listing and '
            + 'this read.',
        'incidents.panel.failed': 'The Hub could not read this incident. Fold the row and open it again.',
        'incidents.analyse': 'Analyse this window',
        'incidents.panel.overline': '// frozen by the daemon',
        'incidents.panel.window': 'The window ran from {before} before the incident to {after} after it, and a '
            + 'finding stamped after the incident belongs to the replacement, not to what died.',
        'incidents.panel.no_finding': 'The daemon froze no finding for this incident.',
        'incidents.filter.placeholder': 'endpoint or template',
        'incidents.filter.shown': '{shown} of {total} shown',
        'incidents.filter.label': 'Filter the findings',
        'incidents.findings.no_credential': 'No source in this Hub has an ack credential, so these findings are '
            + 'acknowledged in each daemon, not from here.',
        'incidents.finding.col.type': 'Type',
        'incidents.finding.col.severity': 'Severity',
        'incidents.finding.col.impact': 'Est. impact (ops)',
        'incidents.finding.col.endpoint': 'Endpoint',
        'incidents.finding.col.template': 'Template',
        'incidents.finding.col.seen': 'Seen',
        'incidents.finding.col.first': 'First seen',
        'incidents.finding.col.ack': 'Ack',
        'incidents.ack.finding': 'finding',
        'incidents.ack.label': 'Ack {label} on {endpoint}',
        'incidents.ack.unknown_endpoint': 'an unknown endpoint',
        'incidents.ack.link': 'Ack',
        'severity.critical': 'critical',
        'severity.warning': 'warning',
        'severity.info': 'info',
        'correlations.col.pair': 'Pair',
        'correlations.col.episodes': 'Episodes',
        'correlations.col.last': 'Last active',
        'correlations.col.peak': 'Peak',
        'correlations.col.lag': 'Lag',
        'correlations.col.source': 'Source',
        'correlations.col.episode': 'Episode',
        'correlations.col.lasted': 'Lasted',
        'correlations.col.peak_conf': 'Peak conf.',
        'correlations.col.peak_co': 'Peak co-occ.',
        'correlations.col.incidents': 'Incidents',
        'correlations.overline': '// correlations',
        'correlations.title': 'Findings that keep following each other',
        'correlations.sub': 'Each pair is a finding that, again and again, fires within seconds after another one. '
            + 'Its periods of activity stay here after the daemon forgets them. ',
        'correlations.help': 'A daemon with `[daemon.correlation]` on and `[daemon.hub_export]` set pushes each '
            + 'period a pair stays active as an episode, and closes it once the pair has not co-occurred for its '
            + 'gap, ten minutes by default. The Hub keeps what it is pushed and re-derives nothing. An episode whose '
            + 'daemon went silent past its gap and a quarter of an hour reads as ended where it was last seen.',
        'correlations.topic': 'correlations',
        'correlations.error': 'The Hub did not return the correlations. This is the Hub itself, not any daemon.',
        'correlations.empty.title': 'No correlation in this range.',
        'correlations.empty.body': 'A pair shows here once a daemon with `[daemon.correlation]` and '
            + '`[daemon.hub_export]` on pushes an episode of it.',
        'correlations.limit': 'The Hub returned the {limit} most recent episodes of this range and stopped there. '
            + 'Narrow the range or the filters to see the others.',
        'correlations.note.lead': 'Pushed by daemons with ',
        'correlations.note.and': ' and ',
        'correlations.note.tail': ' on. A daemon that pushes none has no rows here.',
        'correlations.range': 'range',
        'correlations.range.option': 'last {span}',
        'correlations.every_pair': 'Show every pair',
        'correlations.active_only': 'active now only',
        'correlations.then': '↳ then',
        'correlations.stale_title': 'The daemon stopped reporting this episode before closing it, so it reads as '
            + 'ended where it was last seen.',
        'correlations.active': 'active',
        'correlations.strip_label': '{episodes} episodes and {incidents} incidents over the last {span}',
        'correlations.now': 'now',
        'correlations.no_incident': 'none',
        'correlations.explain': 'Explain the last pair',
        'correlations.incident.title': 'Correlations active during this incident',
        'correlations.incident.sub': 'Episodes whose activity overlaps the incident\'s window, from the same daemon.',
        'correlations.incident.reading': 'Reading the correlations.',
        'correlations.incident.error': 'The Hub did not return the correlations of this window.',
        'correlations.incident.none': 'No correlation was active in this window.',
        'correlations.incident.no_push': '{name} pushes no correlation you can read, so none can show here.',
        'correlations.all_episodes': 'All its episodes',
        'ack.back': 'Back to the incident',
        'ack.overline': '// acknowledgment',
        'ack.title': 'Acknowledge a finding',
        'ack.sub': 'An ack lives in one daemon\'s own store, so it is taken source by source, and one submit writes '
            + 'to every checked source, in the name this Hub knows you by.',
        'ack.incomplete.title': 'This link is incomplete.',
        'ack.incomplete.body': 'It names no finding signature, or one the Hub refuses: longer than 1,024 characters, '
            + 'or carrying a control character. Open it again from the dashboard that gave it.',
        'ack.read_failed': 'The Hub could not read this finding or its sources. Reload the page.',
        'ack.missing.title': 'The Hub holds no finding with this signature.',
        'ack.missing.body': 'Retention removed it, or no source this Hub reads ever reported ',
        'ack.fact.detections': 'Detections (7 days)',
        'ack.fact.avoidable_io': 'Avoidable I/O (7 days)',
        'ack.detections.failed': 'The Hub could not read the detections of the last 7 days.',
        'ack.detections.none': 'No detection known in the last 7 days',
        'ack.detections.io': ', {count} avoidable I/O',
        'ack.detections.line': '{name}: {count}{io}{gaps}',
        'ack.fact.service': 'service',
        'ack.fact.endpoint': 'endpoint',
        'ack.fact.first_seen': 'first seen',
        'ack.fact.last_seen': 'last seen',
        'ack.fact.status': 'status',
        'ack.status.active': 'active',
        'ack.status.likely_resolved': 'likely resolved',
        'ack.status.not_observed': 'not observed',
        'ack.writing': 'Writing to the daemons.',
        'ack.button.ack': 'Acknowledge',
        'ack.button.revoke': 'Revoke',
        'ack.none.no_source': 'No source in this Hub carries this finding, so there is nothing to acknowledge from '
            + 'here.',
        'ack.none.no_writable': 'This Hub cannot write an ack to any source that carries this finding. Each row says '
            + 'why.',
        'ack.field.reason': 'Reason',
        'ack.field.reason_gloss': 'required, kept by each daemon beside your name',
        'ack.field.expires': 'Expires',
        'ack.field.expires_gloss': 'optional, the end of that day in UTC. Empty is a permanent ack',
        'ack.daemon.head': '// acknowledge it on the daemon',
        'ack.daemon.sub': 'The daemon takes the ack itself, with its own key.',
        'ack.daemon.copy': 'Copy the ack command for {name}',
        'ack.daemon.note': 'Set `PERF_SENTINEL_DAEMON_API_KEY` to this daemon\'s ack key first, and write the reason '
            + 'in place of the last value. Add `--expires 7d` for an ack that lapses.',
        'ack.col.source': 'Source',
        'ack.col.current': 'Current ack',
        'ack.col.can_do': 'What this page can do',
        'ack.row.revoke': 'Revoke this ack.',
        'ack.row.ack': 'Acknowledge.',
        'ack.cell.none': 'none known',
        'ack.cell.until': ' at {at}, until {until}',
        'ack.cell.no_expiry': ' at {at}, no expiry',
        'access.overline': '// access',
        'access.title': 'Your access',
        'access.sub': 'Rights are declared in the Hub\'s configuration, not on this page. Its one action revokes a '
            + 'terminal token you approved.',
        'access.card.signed_in': 'signed in as',
        'access.card.groups': 'your groups',
        'access.groups.none': 'No group.',
        'access.groups.label': 'Your groups',
        'access.request.head': '// send this to an administrator',
        'access.request.sub': 'Your name, the id an administrator lists, and the groups a grant can name.',
        'access.request.copy': 'Copy the access request',
        'access.card.reach': 'what you reach',
        'access.reach.caption': 'What your grants give you',
        'access.col.project': 'Project',
        'access.col.group': 'Group',
        'access.col.role': 'Role',
        'access.col.permissions': 'Permissions',
        'access.card.own_tokens': 'your terminal tokens',
        'access.admin.lead': 'You administer this Hub. ',
        'access.admin.tail': ' lists what no project covers, everyone\'s terminal tokens and the whole configuration.',
        'access.card.projects': 'projects you reach',
        'coverage.overline': '// coverage',
        'coverage.title': 'What access control covers',
        'coverage.sub': 'Findings no project covers are read only by grants on "*". Rights change in the Helm values '
            + 'hub.access and apply at the next restart.',
        'coverage.snippet.head': '// patterns to paste',
        'coverage.snippet.sub': 'One line per uncovered service, attribute and source.',
        'coverage.snippet.copy': 'Copy the patterns',
        'coverage.card.tokens': 'terminal tokens, everyone\'s',
        'access.tokens.intro': 'perf-sentinel login gets a terminal token once its code is approved. It carries the '
            + 'approver\'s access, never admin, until the session that approved it ends.',
        'access.tokens.failed': 'The Hub did not list the terminal tokens. Reload the page to try again.',
        'access.tokens.none': 'No terminal token.',
        'access.tokens.caption': 'Terminal tokens',
        'access.col.owner': 'Owner',
        'access.col.requested_from': 'Requested from',
        'access.col.created': 'Created',
        'access.col.expires': 'Expires',
        'access.col.last_used': 'Last used',
        'access.revoke.label': 'Revoke the token of {owner} created {created}',
        'access.revoke.confirm': 'Confirm revoke',
        'access.revoke.confirm_label': 'Confirm: {label}. Its terminal stops working.',
        'access.projects.caption': 'Projects and their patterns',
        'access.col.patterns': 'Patterns',
        'access.config.none': 'None declared.',
        'access.config.title': 'configuration',
        'access.config.loaded': 'As the Hub loaded it from hub.access at its last start.',
        'access.config.projects': 'Projects',
        'access.config.roles': 'Roles',
        'access.config.grants': 'Grants',
        'access.config.internal_groups': 'Internal groups',
        'access.config.admins': 'Admins',
        'access.config.machine_tokens': 'Machine tokens',
        'access.config.local_accounts': 'Local accounts',
        'access.config.built_in': '(built-in)',
        'access.config.no_member': 'no member',
        'access.config.by_id': 'By id',
        'access.config.by_group': 'By group',
        'access.config.nobody': 'none',
        'access.col.internal_group': 'Internal group',
        'access.col.members': 'Members',
        'access.col.machine_token': 'Machine token',
        'access.col.local_account': 'Local account',
        'access.col.signs_in_as': 'Signs in as',
        'coverage.attention.title': 'needs attention',
        'coverage.attention.none': 'Every finding the Hub holds belongs to a project. Nothing to assign.',
        'coverage.attention.count.one': '{count} entry is read by no project. Add a pattern that names it to the '
            + 'project of the team that owns it.',
        'coverage.attention.count.other': '{count} entries are read by no project. Add a pattern that names it to '
            + 'the project of the team that owns it.',
        'coverage.services.caption': 'Services no project covers',
        'coverage.no_service': '(no service)',
        'access.col.service': 'Service',
        'access.col.seen_at': 'Seen at',
        'access.col.findings': 'Findings',
        'coverage.attributes.caption': 'Attributes no project covers',
        'access.col.attribute': 'Attribute',
        'coverage.sources.caption': 'Sources no project names',
        'access.col.unnamed_source': 'Source no project names',
        'coverage.backend': 'none stored, trace backend',
        'coverage.truncated': 'Each list stops at its first 200 entries.',
        'coverage.conflicts.title': 'deployments that disagree on a project attribute',
        'coverage.conflicts.body': 'A project\'s attribute pattern reads these copies but cannot write them: the '
            + 'copy carries two values of the key, or was also seen under a grouping without it. An acknowledgment '
            + 'through an attribute grant is refused on them. A source or service grant for the team that owns the '
            + 'service can acknowledge them.',
        'coverage.conflicts.none': 'None.',
        'coverage.conflicts.caption': 'Findings whose deployments disagree',
        'coverage.conflicts.truncated': 'Only the first 200 are listed.',
        'access.col.source': 'Source',
        'access.col.signature': 'Signature',
        'access.col.key': 'Key',
        'access.col.values': 'Values',
        'access.col.missing_key': 'Groupings missing the key',
        'recent.overline': '// recent analyses',
        'recent.title': 'Analyses this Hub still holds',
        'recent.sub': 'The team\'s short memory, not an audit trail: reports are deleted {hours} hours after they '
            + 'succeed, so a link shared yesterday is already dead.',
        'recent.empty.title': 'No analysis in the last {hours} hours.',
        'recent.empty.body': 'Retention empties this list after {hours} idle hours.',
        'recent.run_new': 'Run an analysis',
        'recent.and': ' and ',
        'recent.binaries': 'These analyses were produced by {versions}. Counts from {count} binaries are not '
            + 'directly comparable: a detector added between minors changes what gets found, not only how much. The '
            + 'label on each card names which binary did the detecting.',
        'recent.tuned.one': '{count} run here changed the detection thresholds. Their counts are not comparable with '
            + 'the rest: a threshold decides what gets reported, so a lower count can mean a quieter service or '
            + 'simply a detector that was told to look for less. Each card names the thresholds it used.',
        'recent.tuned.other': '{count} runs here changed the detection thresholds. Their counts are not comparable '
            + 'with the rest: a threshold decides what gets reported, so a lower count can mean a quieter service or '
            + 'simply a detector that was told to look for less. Each card names the thresholds it used.',
        'recent.status.empty': 'succeeded · empty',
        'recent.env_title': 'Declared by the source\'s configuration, not measured.',
        'recent.not_started': 'not started',
        'recent.so_far': '{duration} so far',
        'recent.fact.by': 'by',
        'recent.fact.ran': 'ran',
        'recent.fact.started': 'started',
        'recent.fact.expires': 'expires',
        'recent.fact.thresholds': 'thresholds',
        'recent.fact.error': 'error',
        'recent.changed.one': '{count} changed',
        'recent.changed.other': '{count} changed',
        'missing.access.title': 'No access page on this Hub.',
        'missing.title': 'Nothing lives at this address.',
        'missing.access.body': 'Access control is off here, or the Hub could not read who you are, so this page has '
            + 'nothing to show.',
        'missing.body': 'The Hub has no screen called "{route}". The link may be mistyped, or older than this Hub.',
        'report.loading': 'Loading the report',
        'report.frame_title': 'Analysis report',
        'report.back': 'Back to the run',
        'report.path': 'report / {id}',
        'report.gone.queued.title': 'The report is not ready yet.',
        'report.gone.queued.body': 'The analysis is still waiting for a worker. Its page says when the report is '
            + 'ready.',
        'report.gone.running.title': 'The report is not ready yet.',
        'report.gone.running.body': 'The analysis is still reading {name}. Its page says when the report is ready.',
        'report.gone.failed.title': 'This analysis wrote no report.',
        'report.gone.failed.body': 'It failed before the engine produced one. Its page says why.',
        'report.gone.interrupted.title': 'This analysis wrote no report.',
        'report.gone.interrupted.body': 'It was interrupted before the engine produced one.',
        'report.gone.expired.title': 'This report is gone.',
        'report.gone.expired.body': 'Its retention ended and the Hub deleted it. Running the same analysis again '
            + 'writes a new report from what {name} holds now.',
        'report.gone.other.title': 'This report is gone.',
        'report.gone.other.body': 'The Hub no longer serves it.',
        'report.rerun': 'Run it again with the same parameters',
        'report.open_run': 'Open the run',
        'report.rendered': 'Rendered by perf-sentinel {version}',
        'report.expires_in': ' · expires in {duration}',
        'report.expired': ' · expired'
    };
})(globalThis);
