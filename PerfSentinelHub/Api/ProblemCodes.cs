namespace PerfSentinelHub.Api;

/// <summary>
///     The stable code beside every problem's English detail, which the launcher translates. A code
///     names the refusal, not the route, and is never renamed: tests/text.test.js reads this file to
///     check that every code has its sentence in both languages. A card refusal's code is its
///     <c>CardRules.Reasons</c> reason, a disclosure refusal keeps its <c>reason</c>.
/// </summary>
public static class ProblemCodes
{
    // The request itself.
    public const string InvalidJson = "invalid_json";
    public const string InvalidRequest = "invalid_request";
    public const string UnknownSource = "unknown_source";
    public const string UnknownScope = "unknown_scope";

    // Who asks, and from where.
    public const string UnknownCaller = "unknown_caller";
    public const string ForeignOrigin = "foreign_origin";
    public const string MachineTokenReadOnly = "machine_token_read_only";
    public const string NoGrant = "no_grant";
    public const string NoFindingsRead = "no_findings_read";

    // Analyses.
    public const string AnalysisEngineMissing = "analysis_engine_missing";
    public const string DetectionKnobUnsupported = "detection_knob_unsupported";
    public const string AnalysisForbidden = "analysis_forbidden";

    // Terminal sign-in and tokens.
    public const string BrowserSessionRequired = "browser_session_required";
    public const string BrowserNotAllowed = "browser_not_allowed";
    public const string UnknownToken = "unknown_token";
    public const string SessionEnding = "session_ending";
    public const string InvalidUserCode = "invalid_user_code";
    public const string NoPendingSignIn = "no_pending_sign_in";
    public const string TooManyUserCodes = "too_many_user_codes";

    // Findings: actions and labels.
    public const string ActionsForbidden = "actions_forbidden";
    public const string ActionReadOnly = "action_read_only";
    public const string UnknownFinding = "unknown_finding";
    public const string ActionExists = "action_exists";
    public const string NoAction = "no_action";
    public const string EndpointLabelsForbidden = "endpoint_labels_forbidden";
    public const string ServiceLabelsForbidden = "service_labels_forbidden";
    public const string UnknownEndpoint = "unknown_endpoint";
    public const string UnknownService = "unknown_service";
    public const string EndpointLabelReadOnly = "endpoint_label_read_only";
    public const string ServiceLabelReadOnly = "service_label_read_only";

    // Acknowledgments and their relay to a daemon.
    public const string AcksNotRelayed = "acks_not_relayed";
    public const string AcksForbidden = "acks_forbidden";
    public const string AckReadOnly = "ack_read_only";
    public const string UnknownFindingAtSource = "unknown_finding_at_source";
    public const string UnknownAckAtSource = "unknown_ack_at_source";
    public const string DaemonUnreachable = "daemon_unreachable";
    public const string DaemonTimeout = "daemon_timeout";
    public const string DaemonRefusedSignature = "daemon_refused_signature";
    public const string DaemonRefusedCredential = "daemon_refused_credential";
    public const string DaemonAckNotFound = "daemon_ack_not_found";
    public const string DaemonAlreadyAcked = "daemon_already_acked";
    public const string DaemonAcksDisabled = "daemon_acks_disabled";
    public const string DaemonAckStoreFull = "daemon_ack_store_full";
    public const string DaemonUnexpectedAnswer = "daemon_unexpected_answer";
    public const string NotADaemon = "not_a_daemon";

    // Recaps.
    public const string InvalidRecapQuery = "invalid_recap_query";
    public const string RecapReportsForbidden = "recap_reports_forbidden";

    // Cards.
    public const string InvalidCardQuery = "invalid_card_query";
    public const string CardsForbidden = "cards_forbidden";
    public const string CardSourcesForbidden = "card_sources_forbidden";
    public const string CardEngineMissing = "card_engine_missing";
    public const string CardsBusy = "cards_busy";
    public const string CardNotRendered = "card_not_rendered";
    public const string CardFieldUndrawable = "card_field_undrawable";
    public const string CardOrganisationUndrawable = "card_organisation_undrawable";
    public const string CardDomainTooLong = "card_domain_too_long";
    public const string CardLanguageUnsupported = "card_language_unsupported";
    public const string CardCountryUnsupported = "card_country_unsupported";
    public const string CardDomainMissing = "card_domain_missing";
    public const string CardAttachmentUnchecked = "card_attachment_unchecked";
    public const string CardKeyForbidden = "card_key_forbidden";
    public const string CardKeyFailed = "card_key_failed";
    public const string UnknownCardKey = "unknown_card_key";
    public const string InvalidRevocationDate = "invalid_revocation_date";

    // Disclosures.
    public const string DisclosuresReadForbidden = "disclosures_read_forbidden";
    public const string DisclosuresManageForbidden = "disclosures_manage_forbidden";
    public const string WithdrawnNotShared = "withdrawn_not_shared";
    public const string InvalidShareDays = "invalid_share_days";
    public const string InvalidWithdrawReason = "invalid_withdraw_reason";
    public const string AlreadyWithdrawn = "already_withdrawn";
    public const string DeliveryDone = "delivery_done";
    public const string DisclosureBusy = "disclosure_busy";
}
