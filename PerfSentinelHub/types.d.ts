/**
 * The shapes `wwwroot/launcher.js`, `wwwroot/guide.js` and `wwwroot/app.js` annotate against, so
 * `tsc --noEmit` can check the JSDoc those files already carry.
 *
 * Hand-written rather than generated: the JSON the page reads is shaped by the
 * API records, and a generator would either pull in a build step the brief
 * forbids or drift silently. Each interface names the writer or record it
 * mirrors, snake_case as on the wire. A field the pages never read may be left
 * out, so one added to a response and never used here does not have to appear.
 */

/** The eleven codes an analysis run can fail with. Mirrors `ErrorCodes`. */
export type ErrorCode =
    | "source_unreachable"
    | "source_auth_failed"
    | "source_rejected_request"
    | "timeout"
    | "output_too_large"
    | "binary_failed"
    | "fetches_failed"
    | "engine_out_of_memory"
    | "invalid_request"
    | "hub_memory_low"
    | "internal";

/** What a source is read through. Mirrors the `kind` column. */
export type SourceKind = "daemon" | "tempo" | "jaeger_query";

/**
 * The six statuses `analysis_runs.status` holds. The page derives two more for
 * reading only, `empty` and `queued` (statusKey in launcher.js), never stored.
 */
export type AnalysisStatus =
    | "pending"
    | "running"
    | "succeeded"
    | "failed"
    | "interrupted"
    | "expired";

/** How a requested trace count sits against the Hub's caps. */
export type WeightBand = "invalid" | "safe" | "heavy" | "ceiling" | "over";

/** What the collector records when a poll fails, `SourcePoller.ErrorCode`. */
export type SourceErrorCode =
    | "timeout"
    | "response_too_large"
    | "invalid_status"
    | "invalid_findings"
    | "database_full"
    | "storage_error"
    | "http_error"
    | "network_error";

/** A configured source, as `GET /api/sources` returns it. */
export interface Source {
    id: string;
    name: string;
    environment: string;
    kind: SourceKind;
    retention_hours?: number | null;
    reachable?: boolean;
    last_attempt_ms?: number | null;
    last_success_ms?: number | null;
    unreachable_since_ms?: number | null;
    producer_version?: string | null;
    last_error_code?: SourceErrorCode | null;
    base_url?: string;
    engine_subcommand?: string | null;
    auth_header_name?: string | null;
    incidents_state?: string | null;
    incidents_read_ms?: number | null;
    ack_relay?: boolean;
    acks_state?: string | null;
    acks_read_ms?: number | null;
    new_signatures_last_hour?: number | null;
    signature_rate_warning?: boolean;
    /** What the caller may do here, with access on; absent otherwise. */
    permissions?: string[] | null;
}

/** One analysis run, as the analyses routes return it. */
export interface Analysis {
    id: string;
    status: AnalysisStatus;
    source_id?: string;
    source_name?: string;
    kind?: SourceKind;
    request?: Record<string, unknown> | null;
    result?: { empty?: boolean } & Record<string, unknown> | null;
    error_code?: ErrorCode | null;
    producer_version?: string | null;
    created_at_ms?: number;
    started_at_ms?: number | null;
    finished_at_ms?: number | null;
    expires_at_ms?: number | null;
}

/** What a run produced, `AnalysisRunWriter.SerializeSummary`. */
export interface RunResult {
    empty: boolean;
    findings: number;
    critical: number;
    warning: number;
    info: number;
    traces_analyzed: number;
    quality_gate_passed: boolean;
    /** Absent on a run whose file could not be measured. */
    report_bytes?: number;
    warnings: ResultWarning[];
}

/** A hint the engine or a daemon left, `ResultWarning`. */
export interface ResultWarning {
    kind: string;
    message: string;
}

/** The request a run stores, `AnalysisRequest.ToJson`: only what was sent. */
export interface RunRequest {
    service?: string;
    trace_id?: string;
    lookback?: string;
    from_ms?: number;
    to_ms?: number;
    max_traces?: number;
    fetch_concurrency?: number;
    detection?: Record<string, number | string>;
}

/** What a run costs, `StatusLimits`. */
export interface StatusLimits {
    max_traces_cap: number;
    analysis_timeout_seconds: number;
    report_retention_hours: number;
    max_traces_embedded: number;
    max_read_limit: number;
    fetch_concurrency_warn: number;
    fetch_concurrency_max: number;
    fetch_concurrency_supported: boolean;
    /** The engine's memory cap in MiB, 0 when off. */
    engine_memory_limit_mib: number;
}

/** One detection threshold a run may override, `DetectionKnob`. */
export interface DetectionKnob {
    name: string;
    kind: string;
    min: number | null;
    max: number | null;
    /** The engine's own value, relayed as JSON. */
    default: any;
    choices: string[] | null;
}

/** GET /api/status, `StatusResponse`. */
export interface HubStatus {
    service: string;
    version: string;
    identity: string | null;
    engine_version: string | null;
    latest_engine_version: string | null;
    latest_hub_version: string | null;
    queue_depth: number;
    workers: number;
    limits: StatusLimits;
    detection_knobs: DetectionKnob[];
    /** Omitted unless disclosures are on and readable. */
    disclosures?: boolean;
    /** Omitted when cards are off for the caller; false when the engine has no `card`. */
    cards?: boolean;
}

/** One gauge of a daemon, `DaemonViewWriter.WriteGauge`. */
export interface DaemonGauge {
    value: number | null;
    capacity: number | null;
    pct: number | null;
    at_capacity: boolean;
}

/** A daemon's configuration section, relayed verbatim: its keys are the daemon's. */
export type DaemonSettings = Record<string, unknown>;

/**
 * GET /api/sources/{id}/daemon, `DaemonViewWriter.WriteAsync`. The light read
 * (`WriteLightAsync`) carries the identity, version, uptime and gauges only,
 * hence the optional rest.
 */
export interface DaemonView {
    source_id: string;
    observed_at_ms: number;
    state?: string;
    error_code: string | null;
    version: string | null;
    uptime_seconds: number | null;
    config?: DaemonSettings | null;
    config_unavailable_reason?: string | null;
    detection_config?: DaemonSettings | null;
    daemon_defaults?: DaemonSettings | null;
    detection_defaults?: DaemonSettings | null;
    defaults_engine_version?: string;
    scoring_config?: DaemonSettings | null;
    energy_model?: string | null;
    energy_source?: string | null;
    traces: DaemonGauge | null;
    analysis_queue: DaemonGauge | null;
    findings: DaemonGauge | null;
    hints_unavailable_reason?: string | null;
    warnings_dropped?: number;
    warnings?: ResultWarning[];
}

/** Where a finding sits in code, the engine's `code_location`. */
export interface CodeLocation {
    function?: string | null;
    filepath?: string | null;
    lineno?: number | null;
    namespace?: string | null;
}

/** The engine's fix for a pattern, `suggested_fix` (and `RecapFix`). */
export interface SuggestedFix {
    pattern?: string;
    framework?: string | null;
    recommendation: string;
    reference_url?: string | null;
}

/** One finding as the engine writes it, relayed by the Hub unchanged. */
export interface Finding {
    type: string;
    severity: string;
    trace_id?: string;
    service: string;
    source_endpoint: string;
    pattern?: {
        template?: string;
        occurrences?: number;
        window_ms?: number;
        distinct_params?: number;
        span_duration_us_p50?: number;
        span_duration_us_p99?: number;
    };
    suggestion?: string;
    first_timestamp?: string;
    last_timestamp?: string;
    green_impact?: {
        estimated_extra_io_ops?: number;
        io_intensity_score?: number;
        io_intensity_band?: string;
    };
    confidence?: string;
    signature: string;
    code_location?: CodeLocation;
    suggested_fix?: SuggestedFix;
}

/** A finding as a daemon stores it: the finding and when it first saw it. */
export interface StoredFindingRow {
    finding: Finding;
    stored_at_ms?: number;
    first_seen_ms: number;
    seen_count?: number;
}

/** The detection volume of a windowed read, `FindingEnvelopeWriter.WriteVolume`. */
export interface DetectionVolume {
    detections: number | null;
    detections_complete: boolean | null;
    avoidable_io_ops: number | null;
    detections_gaps: string[];
    detections_sampling_rate: number | null;
}

/** One source of a finding, `FindingEnvelopeWriter.WriteSources`. */
export interface FindingSource extends Partial<DetectionVolume> {
    id: string;
    name: string;
    environment: string;
    producer_version: string | null;
    age_seconds: number | null;
    status: string;
}

/** One ack a source holds on a finding, `FindingEnvelopeWriter.WriteAcks`. */
export interface FindingAck {
    source_id: string;
    source: string;
    by: string;
    reason?: string;
    at: string;
    expires_at?: string;
}

/**
 * A row of GET /api/findings, `FindingEnvelopeWriter`: the daemon's envelope
 * with the Hub's fields over it. The volume rides on a windowed read only.
 */
export interface FindingEnvelope extends StoredFindingRow, Partial<DetectionVolume> {
    first_seen: number;
    last_seen: number;
    max_confidence: string;
    status: string;
    detections_from?: string;
    detections_to?: string;
    lineage?: { original_first_seen: number; predecessors: number };
    sources: FindingSource[];
    acks?: FindingAck[];
}

/** A row of GET /api/incidents, `IncidentWriter`: the daemon's document plus the Hub's fields. */
export interface Incident {
    id: string;
    service: string;
    namespace?: string;
    kind: string;
    at_ms: number;
    detail?: string;
    window_from_ms: number;
    window_to_ms: number;
    oldest_finding_ms?: number | null;
    /** Only on the single-incident read. */
    findings?: StoredFindingRow[];
    ended_at_ms: number | null;
    source_id: string;
    source_name: string | null;
    environment: string | null;
    first_seen: number;
    last_seen: number;
    finding_count: number;
    capture: "empty" | "complete" | "partial";
}

/** One side of a correlated pair, `EpisodeSide`. */
export interface EpisodeSide {
    finding_type: string;
    service: string;
    template: string;
    grouping_key: string | null;
    grouping_value: string | null;
}

/** A correlation episode of GET /api/correlations, `StoredEpisode`. */
export interface Episode {
    id: string;
    source_id: string;
    source_name?: string | null;
    pair_key: string;
    source: EpisodeSide;
    target: EpisodeSide;
    started_at_ms: number;
    last_seen_ms: number;
    ended_at_ms: number | null;
    ended_by: string | null;
    gap_ms: number;
    peak_confidence: number;
    peak_co_occurrences: number;
    last_confidence: number;
    last_co_occurrences: number;
    median_lag_ms: number | null;
    sample_trace_id: string | null;
    source_sample_trace_id: string | null;
    first_seen_ms: number;
    last_seen_hub_ms: number;
}

/** The episodes of one pair and daemon, as `PSL.correlationPairs` groups them. */
export interface CorrelationPair {
    key: string;
    source_id: string;
    pair_key: string;
    episodes: Episode[];
    active: boolean;
    peak: number;
    last: Episode;
}

/** A source row of the ack page, as `PSL.ackRows` builds it. */
export interface AckRow {
    id: string;
    name: string;
    environment: string;
    relay: boolean;
    ack: FindingAck | null;
    action: "ack" | "revoke" | "either" | "none";
    checked: boolean;
    note: string | null;
}

/** An action as the recap shows it, `RecapAction`. */
export interface RecapAction {
    status: string;
    owner: string | null;
    owner_display: string | null;
    updated_at_ms: number;
    done_at_ms: number | null;
    confirmation: string | null;
    /** Omitted unless asked for. */
    events?: RecapActionEvent[];
}

/** One change of an action, `RecapActionEvent`. */
export interface RecapActionEvent {
    from: string | null;
    to: string;
    owner: string | null;
    owner_display: string | null;
    by: string;
    at_ms: number;
}

/** A recap period, `RecapPeriodInfo`. */
export interface RecapPeriodInfo {
    kind: string;
    from: string;
    to: string;
    in_progress: boolean;
    incomplete: string[];
    notes: string[];
    sampling_rate: number | null;
    truncated_to_days: number | null;
}

/** One ranked finding of a recap, `RecapPriority`. The last seven are omitted when null. */
export interface RecapPriority {
    rank: number;
    finding_type: string;
    severity: string;
    service: string;
    plain: string;
    detections: number | null;
    avoidable_io_ops: number | null;
    co2_grams: number | null;
    co2_partial: boolean;
    volume_known: boolean;
    trend: string;
    change: number | null;
    label: string | null;
    service_label: string | null;
    action: RecapAction | null;
    signature?: string;
    endpoint?: string;
    template?: string;
    suggestion?: string;
    suggested_fix?: SuggestedFix;
    code_location?: CodeLocation;
    confidence?: string;
}

/** One coloured piece of a recap priority's SQL, as `PSL.sqlTokens` cuts it. Joined, the texts are the statement. */
export interface SqlToken {
    kind: 'keyword' | 'table' | 'param' | 'column' | 'text';
    text: string;
}

/** One finding that moved, `RecapMove`. */
export interface RecapMove {
    finding_type: string;
    service: string;
    plain: string;
    trend: string;
    change: number | null;
    label: string | null;
    service_label: string | null;
    signature?: string;
    endpoint?: string;
}

/** What moved since the previous period, `RecapMovements`. */
export interface RecapMovements {
    new: RecapMove[];
    gone: RecapMove[];
    gone_with_confirmed_action: number;
    up: RecapMove[];
    down: RecapMove[];
    came_back: RecapMove[];
    done_still_detected: RecapMove[];
    /** The whole counts, omitted unless the lists were cut. */
    counts?: {
        new: number;
        gone: number;
        up: number;
        down: number;
        came_back: number;
        done_still_detected: number;
    };
}

/** The figures on top of a recap, `RecapTiles`. */
export interface RecapTiles {
    avoidable_io_ops: number | null;
    previous_avoidable_io_ops: number | null;
    co2_grams: number | null;
    previous_co2_grams: number | null;
    co2_sources_without_coefficient: number;
    co2_missing_reason: string | null;
    active_findings: number;
    previous_active_findings: number;
    acked_findings: number;
    actions: {
        todo: number;
        in_progress: number;
        done: number;
        confirmed: number;
        contradicted: number;
        came_back: number;
    };
}

/** GET /api/recap/filters, `RecapFilterOptions`. Each list is sorted by name. */
export interface RecapFilterOptions {
    services: string[];
    groupings: string[];
}

/** GET /api/recap, `RecapSnapshot`. */
export interface RecapSnapshot {
    schema_version: number;
    view: string;
    generated_at_ms: number;
    period: RecapPeriodInfo;
    previous: RecapPeriodInfo;
    filters: {
        environment: string | null;
        source_id: string | null;
        service: string | null;
        grouping: string | null;
        service_label: string | null;
        /** Several services chosen, sorted; `service` is then null. Absent while one or none is chosen. */
        services?: string[];
        /** Several namespaces chosen, sorted; `grouping` is then null. Absent while one or none is chosen. */
        groupings?: string[];
    };
    comparison_exact: boolean;
    tiles: RecapTiles;
    priorities: RecapPriority[];
    more_priorities: number;
    movements: RecapMovements;
    nothing_moved: boolean;
    sources_without_volume: string[];
    truncated: boolean;
    late_sources: string[] | null;
}

/**
 * One step of a screen's guide, as `PSL.recapPilotageSteps` works it out. `anchors` names the elements it
 * points at; none makes a centred card with nothing cut out. Each mark is numbered on its own anchor.
 */
export interface GuideStep {
    id: string;
    anchors: string[];
    title: string;
    body: string[];
    marks?: { anchor: string; text: string }[];
}

/** What a screen hands its guide on each read: still loading, failed, or the steps of what it shows. */
export interface GuideSource {
    status: "wait" | "failed" | "ready";
    steps: GuideStep[];
    /** Which of the guide's wait or failed texts, `guide.<id>.<status>.<variant>.*`, when it has several. */
    variant?: string;
}

/** What the Sources guide reads off the fleet the screen shows. */
export interface SourcesGuideContext {
    /** The sources the Sources tab counts as unreachable. */
    unreachable: number;
    /** The top bar draws the fleet's oldest version behind the Hub's engine. */
    behind: boolean;
    /** A daemon's producer cell reads unknown. */
    unknownProducer: boolean;
    /** The first daemon listed does not unfold for the caller. */
    lockedDaemon: boolean;
    /** The first open daemon row that has read, or failed to read, its daemon. */
    gauges: "read" | "failed" | null;
}

/** What the Incidents guide reads off the listing. */
export interface IncidentsGuideContext {
    /** The rows are the stored copy of the last read. */
    stale: boolean;
    /** The name of the first daemon that refused the Hub's key. */
    unauthorized: string | null;
    filtered: boolean;
    /** The first unfolded incident whose detail was read: with findings, none, or gone. */
    panel: "findings" | "none" | "gone" | null;
}

/** What the Correlations guide reads off the listing. */
export interface CorrelationsGuideContext {
    /** The route names one pair. */
    pairOnly: boolean;
    /** The cap the read reached, or null below it. */
    limit: number | null;
    /** A pair is unfolded. */
    opened: boolean;
    /** A trace backend exists, so episodes carry the Explain link. */
    traceable: boolean;
}

/** What the Access guide reads off the caller's access. */
export interface AccessGuideContext {
    /** Signed in with a machine token, whose group is configuration rather than the provider's. */
    machine: boolean;
    noGroup: boolean;
    noReach: boolean;
    /** A grant names the project `*`. */
    everything: boolean;
    /** The caller's own terminal tokens, as the tokens card lists them. */
    tokens: "listed" | "none" | "failed";
}

/** What the Coverage guide reads off an admin's access. */
export interface CoverageGuideContext {
    /** The Hub sent what no project covers, and it is empty. */
    covered: boolean;
    /** The uncovered lists stop at their cap. */
    truncated: boolean;
    /** An uncovered source is a trace backend, which stores no finding. */
    backend: boolean;
    /** The Hub sent the conflicts, and there are none. */
    noConflict: boolean;
}

/** What the New screen's guide reads off the form and the Hub's status. */
export interface NewGuideContext {
    /** Every configured source, and those the caller may run on. */
    sources: number;
    runnable: number;
    windowHandoff: boolean;
    traceHandoff: boolean;
    /** The chosen source is a daemon. */
    daemon: boolean;
    /** A runnable source's row carries a version pill. */
    skewPill: boolean;
    mode: string;
    rangeMode: string;
    spanMs: number;
    retentionHours: number | null;
    maxTraces: number;
    /** The source has a weight history under the trace count. */
    weighed: boolean;
    fetchBand: "ok" | "over" | "blocked" | "invalid" | null;
    queue: number;
    limits: StatusLimits;
    /** The sentence beside the button when something blocks it. */
    blocker: string | null;
    unreachable: boolean;
    /** The chosen source's engine is behind the Hub's. */
    skewBehind: boolean;
    /** The form spells a terminal command. */
    command: boolean;
}

/** `window.PSL_GUIDE`, the controller guide.js defines. */
export interface GuideController {
    /** `guide`, the PSL.guideIdFor id, picks `guide.<last part>.wait.*` and `.failed.*` when they exist. */
    start(source: () => GuideSource, guide?: string): void;
    refresh(): void;
    stop(): void;
    isOpen(): boolean;
}

/** One stage of a recap job, `RecapJobStageItem`. */
export interface RecapJobStage {
    name: string;
    state: string;
    ms: number | null;
}

/** GET /api/recap/jobs/{id}, `RecapJobState`. */
export interface RecapJob {
    id: string;
    status: string;
    stage: string | null;
    stages: RecapJobStage[];
    progress: number;
    started_at_ms: number;
    elapsed_ms: number;
    finished_at_ms: number | null;
    expires_at_ms: number | null;
    error_code: string | null;
}

/** One delivery of a frozen recap report, `RecapDeliveryView`. */
export interface RecapDelivery {
    channel: string;
    target: string;
    state: string;
    attempts: number;
    last_attempt_ms: number | null;
    next_attempt_ms: number | null;
    last_status: number | null;
}

/** A frozen report of GET /api/recap/reports, `RecapReportItem`. */
export interface RecapReport {
    id: string;
    subscription: string | null;
    period: { kind: string; from: string; to: string };
    views: string[];
    complete: boolean;
    nothing_moved: boolean;
    created_by: string | null;
    generated_at_ms: number;
    deliveries: RecapDelivery[];
    language: string;
}

/** A fix a story card may tell, `CardFix`. */
export interface CardFix {
    signature: string;
    finding_type: string;
    service: string;
    endpoint: string;
    plain: string;
    done_at_ms: number;
    before_fix_ops: number | null;
    before_period_name: string | null;
    label: string | null;
}

/** What the card options route answers for a period, `CardOptions`. */
export interface CardOptions {
    period: { kind: string; from: string; to: string };
    period_name: string;
    refusal: string | null;
    refusal_text: string | null;
    restart_days: number;
    sampled_permille: number;
    environment: string | null;
    sources: string[];
    previous_complete: boolean;
    previous_whole: boolean;
    confirmed_fixes_count: number;
    fixes: CardFix[];
    default_signature: string | null;
    org: { name: string; domain: string | null };
    equivalence_country: string;
}

/** One signing key, `CardKeyItem`. */
export interface CardKey {
    fingerprint: string;
    public_key: string;
    from: string;
    to: string | null;
    revoked: string | null;
    current: boolean;
    dns: string;
}

/** GET /api/cards/keys, `CardKeysResponse`. */
export interface CardKeys {
    record_name: string | null;
    keys: CardKey[];
    well_known: string;
}

/** The figures a signed card carries, `CardSignedFigures`. */
export interface CardSignedFigures {
    angle: string;
    period: { kind: string; from: string; to: string };
    before_fix_period: { from: string; to: string } | null;
    cited_ops: number | null;
    cited_ms: number | null;
    before_fix_ops: number | null;
    threshold_ops: number | null;
    threshold_ms: number | null;
    last_waste_permille: number | null;
    previous_waste_permille: number | null;
    confirmed_fixes_count: number | null;
    services_count: number | null;
}

/** POST /api/cards/attachment, `CardAttachmentResponse`. */
export interface CardAttachment {
    integrity: string;
    content: string;
    level: number;
    domain: string | null;
    key: string;
    reason: string | null;
    figures: CardSignedFigures | null;
    contacted: string[];
}

/** The figures a disclosure is listed with, `DisclosureEnvelope.Figures`. */
export interface DisclosureFigures {
    energy_kwh: number | null;
    carbon_kgco2eq: number | null;
    avoidable_ratio: number | null;
    coverage: number | null;
    applications_declared: number | null;
    applications_measured: number | null;
    applications: Array<{ name: string; energy_kwh: number | null }>;
}

/** A disclosure of GET /api/disclosures, `DisclosureItem`. */
export interface Disclosure {
    id: string;
    report_uuid: string;
    organisation: string;
    period: { type: string; from: string; to: string };
    intent: string;
    confidentiality: string;
    content_hash: string;
    integrity_level: string | null;
    figures: DisclosureFigures | null;
    files: { report: boolean; attestation: boolean; bundle: boolean; pdf: boolean };
    pdf: string;
    render_reason: string | null;
    published_at_ms: number;
    signature_added_at_ms: number | null;
    withdrawn: { at_ms: number; reason: string | null; by: string | null } | null;
}

/** A share link without its token, `ShareLinkView`. */
export interface ShareLink {
    id: string;
    state: string;
    created_by: string;
    created_at_ms: number;
    expires_at_ms: number;
    revoked_at_ms: number | null;
    revoked_by: string | null;
    downloads: number;
    last_used_at_ms: number | null;
}

/** A webhook delivery by the webhook's name, `DeliveryView`. */
export interface DisclosureDelivery {
    id: string;
    webhook: string;
    event: string;
    state: string;
    attempts: number;
    last_attempt_ms: number | null;
    next_attempt_ms: number | null;
    last_status: number | null;
}

/** GET /api/disclosures/{id}, `DisclosureDetail`. */
export interface DisclosureDetail {
    disclosure: Disclosure;
    checks: { content_hash: string; binding: string; signature: string; engine_version: string | null };
    share_links: ShareLink[];
    deliveries: DisclosureDelivery[];
    expected_identity: string | null;
    expected_issuer: string | null;
    can_manage: boolean;
}

/** One application of a disclosure report, the engine's `applications[]`. */
export interface ReportApplication {
    service_name?: string;
    display_name?: string;
    total_requests?: number | null;
    energy_kwh?: number | null;
    carbon_kgco2eq?: number | null;
    anti_patterns_detected_count?: number | null;
}

/**
 * The report.json a disclosure serves: the engine's perf-sentinel-report,
 * read defensively since an older report may lack any part of it.
 */
export interface DisclosureReport {
    aggregate?: {
        total_energy_kwh?: number | null;
        /** The two host blocks `PSL.hostRows` reads, from v1.8. */
        database_waste?: { in_totals_windows?: number; windows_with_figure?: number; in_totals_energy_kwh?: number };
        messaging_waste?: { in_totals_windows?: number; windows_with_figure?: number; in_totals_energy_kwh?: number };
        carbon_breakdown?: { database_kgco2eq?: number | null; messaging_kgco2eq?: number | null };
    };
    applications?: ReportApplication[];
    scope_manifest?: {
        applications_excluded?: Array<{ service_name?: string; name?: string; reason?: string }>;
    };
    methodology?: {
        sci_specification?: string;
        conformance?: string;
        enabled_patterns?: string[];
        calibration_inputs?: { carbon_intensity_source?: string };
    };
    notes?: { disclaimers?: string[] };
}

/** A sign-in group, `AccessGroup`. Origin is provider or internal. */
export interface AccessGroup {
    name: string;
    origin: string;
}

/** What the caller reaches through one grant, `AccessReach`. Group null is Admins. */
export interface AccessReach {
    group: string | null;
    project: string;
    role: string;
    permissions: string[];
}

/** A pattern of a project, `PatternOptions`. */
export interface AccessPattern {
    source: string | null;
    service: string | null;
    attribute: string | null;
}

/** A declared project, `ProjectOptions`. */
export interface AccessProject {
    name: string;
    patterns: AccessPattern[];
}

/** The access configuration an admin sees, `AccessConfiguration`. */
export interface AccessConfiguration {
    projects: AccessProject[];
    roles: Array<{ name: string; permissions: string[]; built_in: boolean }>;
    grants: Array<{ group: string; project: string; role: string }>;
    internal_groups: Array<{ name: string; members: string[] }>;
    admins: string[];
    admin_groups: string[];
    machine_tokens: Array<{ name: string; group: string; expires_at_ms: number; active: boolean }>;
    local_accounts: string[];
}

/** Records no project matches, `AccessUnmatched`. */
export interface AccessUnmatched {
    services: Array<{ service: string | null; source_ids: string[]; findings: number }>;
    attributes: Array<{ key: string; value: string; findings: number }>;
    sources: Array<{ source_id: string; findings: number | null }>;
    truncated: boolean;
}

/** Copies whose attribute disagrees across sources, `AccessConflicts`. */
export interface AccessConflicts {
    copies: Array<{
        signature: string;
        source_id: string;
        service: string | null;
        key: string;
        values: string[];
        identities_without_key: number;
    }>;
    truncated: boolean;
}

/** GET /api/access, `AccessResponse`. The last three are null except for an admin. */
export interface AccessView {
    identity: string | null;
    subject: string;
    caller: string;
    expires_at_ms: number | null;
    admin: boolean;
    groups: AccessGroup[];
    reach: AccessReach[];
    projects: AccessProject[];
    configuration: AccessConfiguration | null;
    unmatched: AccessUnmatched | null;
    conflicts: AccessConflicts | null;
}

/** A terminal token of GET /api/tokens, `TerminalTokenResponse`. */
export interface TerminalToken {
    id: string;
    identity: string;
    subject: string;
    client_address: string | null;
    created_at_ms: number;
    expires_at_ms: number;
    last_used_at_ms: number | null;
}
