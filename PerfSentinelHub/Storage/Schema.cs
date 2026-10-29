namespace PerfSentinelHub.Storage;

internal static class Schema
{
    internal const string V1 = """
                               CREATE TABLE IF NOT EXISTS schema_migrations (
                                 version INTEGER PRIMARY KEY,
                                 applied_at_ms INTEGER NOT NULL
                               );
                               CREATE TABLE IF NOT EXISTS findings (
                                 signature TEXT PRIMARY KEY,
                                 finding_json TEXT NOT NULL,
                                 service TEXT NOT NULL,
                                 finding_type TEXT NOT NULL,
                                 severity TEXT NOT NULL,
                                 endpoint TEXT NOT NULL,
                                 template_hash TEXT NOT NULL,
                                 sample_trace_id TEXT,
                                 first_seen_ms INTEGER NOT NULL,
                                 last_seen_ms INTEGER NOT NULL,
                                 max_confidence TEXT NOT NULL,
                                 max_confidence_rank INTEGER NOT NULL
                               );
                               CREATE INDEX IF NOT EXISTS ix_findings_service_type_severity_last_seen
                                 ON findings(service, finding_type, severity, last_seen_ms DESC);
                               CREATE INDEX IF NOT EXISTS ix_findings_trace ON findings(sample_trace_id);
                               CREATE TABLE IF NOT EXISTS finding_sources (
                                 signature TEXT NOT NULL REFERENCES findings(signature) ON DELETE CASCADE,
                                 source_id TEXT NOT NULL,
                                 source_name TEXT NOT NULL,
                                 environment TEXT NOT NULL,
                                 producer_version TEXT NOT NULL,
                                 first_seen_ms INTEGER NOT NULL,
                                 last_seen_ms INTEGER NOT NULL,
                                 PRIMARY KEY(signature, source_id)
                               );
                               CREATE TABLE IF NOT EXISTS source_state (
                                 source_id TEXT PRIMARY KEY,
                                 last_attempt_ms INTEGER NOT NULL,
                                 last_success_ms INTEGER,
                                 unreachable_since_ms INTEGER,
                                 producer_version TEXT,
                                 last_error_code TEXT
                               );
                               CREATE TABLE IF NOT EXISTS endpoint_heartbeats (
                                 source_id TEXT NOT NULL,
                                 service TEXT NOT NULL,
                                 endpoint TEXT NOT NULL,
                                 last_seen_any_ms INTEGER NOT NULL,
                                 PRIMARY KEY(source_id, service, endpoint)
                               );
                               """;

    // Lineage between a finding and the one it most likely mutated from: same
    // service, detector and endpoint, different template hash. The chain's
    // origin and depth are denormalized onto each row at link time (copied
    // from the predecessor's own row when it has one), so a finding's full
    // lineage survives the retention purge of every earlier hop: the
    // intermediate hops' rows cascade away with their findings, this row
    // does not need them. The heartbeat index serves the status CASE's
    // (service, endpoint) probe, which the PK cannot (it leads with
    // source_id).
    internal const string V2 = """
                               CREATE TABLE IF NOT EXISTS finding_lineage (
                                 successor_signature TEXT NOT NULL REFERENCES findings(signature) ON DELETE CASCADE,
                                 predecessor_signature TEXT NOT NULL,
                                 predecessor_first_seen_ms INTEGER NOT NULL,
                                 origin_first_seen_ms INTEGER NOT NULL,
                                 depth INTEGER NOT NULL,
                                 linked_at_ms INTEGER NOT NULL,
                                 method TEXT NOT NULL,
                                 PRIMARY KEY(successor_signature, predecessor_signature)
                               );
                               CREATE INDEX IF NOT EXISTS ix_lineage_predecessor
                                 ON finding_lineage(predecessor_signature);
                               CREATE INDEX IF NOT EXISTS ix_heartbeats_service_endpoint
                                 ON endpoint_heartbeats(service, endpoint, last_seen_any_ms);
                               """;

    // One row per analysis run. The source's name, environment and kind are
    // denormalized at submission: a run outlives the configuration entry it
    // came from, and a card that lost its source name is unreadable.
    // `request_json` and `result_json` are opaque here on purpose, the shape
    // is the launcher's contract and varies with the source kind.
    internal const string V3 = """
                               CREATE TABLE IF NOT EXISTS analysis_runs (
                                 id TEXT PRIMARY KEY,
                                 status TEXT NOT NULL,
                                 source_id TEXT NOT NULL,
                                 source_name TEXT NOT NULL,
                                 environment TEXT NOT NULL,
                                 kind TEXT NOT NULL,
                                 request_json TEXT NOT NULL,
                                 requested_by TEXT NOT NULL,
                                 created_at_ms INTEGER NOT NULL,
                                 started_at_ms INTEGER,
                                 finished_at_ms INTEGER,
                                 expires_at_ms INTEGER,
                                 producer_version TEXT,
                                 error_code TEXT,
                                 result_json TEXT
                               );
                               CREATE INDEX IF NOT EXISTS ix_runs_status_created
                                 ON analysis_runs(status, created_at_ms);
                               CREATE INDEX IF NOT EXISTS ix_runs_created
                                 ON analysis_runs(created_at_ms DESC);
                               """;

    // When each source last pushed, kept apart from source_state on purpose.
    // source_state describes the poll, whose every column would have to be
    // nullable for a push-only source to own a row, and a push must never look
    // like a successful poll. This table answers a different question: when the
    // Hub last heard from a daemon on the path that is primary.
    internal const string V4 = """
                               CREATE TABLE IF NOT EXISTS source_imports (
                                 source_id TEXT PRIMARY KEY,
                                 last_import_ms INTEGER NOT NULL
                               );
                               """;

    // The daemon's incidents, copied by the poll. `at_ms` is the alerting clock
    // and the daemon's id already hashes it, so the Hub keeps its own clock in
    // first_seen_ms and last_seen_ms, which retention and ORDER BY read. The
    // id hashes service, kind and at_ms and nothing of the daemon, so two
    // daemons fed the same alert compute the same id and each freezes its own
    // ring: the key is (id, source_id), the way finding_sources is keyed. The
    // findings sit in findings_json, listed last because SQLite walks every
    // earlier column to reach a later one, so the listing, which reads none of
    // them, never pays for them. incident_reads is kept apart from
    // source_state for the reason source_imports is: a refused or absent
    // incidents route must never look like an unreachable daemon.
    internal const string V5 = """
                               CREATE TABLE IF NOT EXISTS incidents (
                                 id TEXT NOT NULL,
                                 source_id TEXT NOT NULL,
                                 service TEXT NOT NULL,
                                 kind TEXT NOT NULL,
                                 at_ms INTEGER NOT NULL,
                                 ended_at_ms INTEGER,
                                 window_from_ms INTEGER NOT NULL,
                                 window_to_ms INTEGER NOT NULL,
                                 oldest_finding_ms INTEGER,
                                 finding_count INTEGER NOT NULL,
                                 incident_json TEXT NOT NULL,
                                 first_seen_ms INTEGER NOT NULL,
                                 last_seen_ms INTEGER NOT NULL,
                                 findings_json TEXT NOT NULL,
                                 PRIMARY KEY(id, source_id)
                               );
                               CREATE INDEX IF NOT EXISTS ix_incidents_at ON incidents(at_ms DESC);
                               CREATE INDEX IF NOT EXISTS ix_incidents_service_at
                                 ON incidents(service, at_ms DESC);
                               CREATE TABLE IF NOT EXISTS incident_reads (
                                 source_id TEXT PRIMARY KEY,
                                 last_read_ms INTEGER NOT NULL,
                                 state TEXT NOT NULL,
                                 last_error_code TEXT
                               );
                               """;

    // One row per day a source observed a finding, on the Hub clock, holding
    // the worst severity seen that day. No foreign key on purpose: a cascade
    // from one purge chunk of findings could delete a large multiple of it
    // while holding the write gate. An orphan row is never read, reads start
    // from finding_sources, and it leaves with its day. Kept a rowid table
    // because the purge deletes by rowid. The finding_sources index serves a
    // read scoped to a source, which the primary key cannot (it leads with
    // signature). That table's per-source columns are added by
    // HubDatabase.EnsureFindingSourceColumnsAsync, on a fresh database too, so
    // both end with one shape.
    internal const string V6 = """
                               CREATE TABLE IF NOT EXISTS finding_observations (
                                 signature TEXT NOT NULL,
                                 source_id TEXT NOT NULL,
                                 day INTEGER NOT NULL,
                                 severity TEXT NOT NULL,
                                 severity_rank INTEGER NOT NULL,
                                 PRIMARY KEY(signature, source_id, day)
                               );
                               CREATE INDEX IF NOT EXISTS ix_observations_day ON finding_observations(day);
                               CREATE INDEX IF NOT EXISTS ix_finding_sources_source
                                 ON finding_sources(source_id, signature);
                               """;

    // Each daemon's active acknowledgments, copied by the poll. A read replaces
    // its source's rows whole, so a row is never older than the last read that
    // succeeded and a revoked ack leaves with the next one. One row per
    // signature and source: the daemon lists a signature acked twice, at
    // runtime and in its CI baseline, and the baseline row is the one kept, as
    // the daemon's own lookup does. acked_at is text because the baseline
    // never constrains its format. expires_at_ms is what a read compares, the
    // text beside it is what it relays. Kept a rowid table because the purge
    // deletes by rowid. ack_reads is kept apart from source_state for the
    // reason incident_reads is.
    internal const string V7 = """
                               CREATE TABLE IF NOT EXISTS source_acks (
                                 source_id TEXT NOT NULL,
                                 signature TEXT NOT NULL,
                                 origin TEXT NOT NULL,
                                 acked_by TEXT NOT NULL,
                                 reason TEXT,
                                 acked_at TEXT NOT NULL,
                                 expires_at TEXT,
                                 expires_at_ms INTEGER,
                                 read_at_ms INTEGER NOT NULL,
                                 PRIMARY KEY(source_id, signature)
                               );
                               CREATE TABLE IF NOT EXISTS ack_reads (
                                 source_id TEXT PRIMARY KEY,
                                 last_read_ms INTEGER NOT NULL,
                                 state TEXT NOT NULL,
                                 last_error_code TEXT
                               );
                               """;

    // Every effective grouping a source saw a finding in, on the Hub clock. The
    // signature is grouping-blind, so one finding can live in several namespaces
    // while its envelope only names the last one, and a grouping filter must
    // match all of them. Kept a rowid table because the purge deletes by rowid.
    internal const string V8 = """
                               CREATE TABLE IF NOT EXISTS finding_groupings (
                                 signature TEXT NOT NULL,
                                 source_id TEXT NOT NULL,
                                 grouping TEXT NOT NULL,
                                 last_seen_ms INTEGER NOT NULL,
                                 PRIMARY KEY(signature, source_id, grouping)
                               );
                               CREATE INDEX IF NOT EXISTS ix_finding_groupings_last_seen
                                 ON finding_groupings(last_seen_ms);
                               """;

    // Every grouping attribute a source captured on a finding, with the grouping
    // identity (first pair) of the observation it came with, on the Hub clock. The
    // signature is grouping-blind and the envelope keeps the newest observation only,
    // so a scope matched on attributes will read this table. ('', '', '', '') marks an
    // observation with no usable grouping. incident_attributes holds the pairs every
    // frozen finding of an incident copy carries, ('', '') when there is none, derived
    // from the stored findings_json whenever it changes and stamped with the copy's
    // last_seen_ms, so a start finds the copies an older build wrote since. Both are kept
    // rowid tables because the purge deletes by rowid, with no foreign key, like V6 and
    // V8. finding_sources gains its copy's own service through
    // HubDatabase.EnsureFindingSourceServiceColumnAsync, as V6's columns are added.
    internal const string V9 = """
                               CREATE TABLE IF NOT EXISTS finding_attributes (
                                 signature TEXT NOT NULL,
                                 source_id TEXT NOT NULL,
                                 grouping_key TEXT NOT NULL,
                                 grouping_value TEXT NOT NULL,
                                 attr_key TEXT NOT NULL,
                                 attr_value TEXT NOT NULL,
                                 last_seen_ms INTEGER NOT NULL,
                                 PRIMARY KEY(signature, source_id, grouping_key, grouping_value, attr_key, attr_value)
                               );
                               CREATE INDEX IF NOT EXISTS ix_finding_attributes_last_seen
                                 ON finding_attributes(last_seen_ms);
                               CREATE TABLE IF NOT EXISTS incident_attributes (
                                 id TEXT NOT NULL,
                                 source_id TEXT NOT NULL,
                                 attr_key TEXT NOT NULL,
                                 attr_value TEXT NOT NULL,
                                 last_seen_ms INTEGER NOT NULL,
                                 PRIMARY KEY(id, source_id, attr_key, attr_value)
                               );
                               """;

    // Personal tokens a browser session approved for a terminal (the device flow). Only the
    // SHA-256 of a token is kept, never the token. The owner is the approving session's subject;
    // its identity is shown and never compared. provider_groups_json and required_group freeze
    // what the session held at approval, so the token follows the session's rules. The pending
    // device codes live in memory and have no table.
    internal const string V10 = """
                                CREATE TABLE IF NOT EXISTS terminal_tokens (
                                  id TEXT PRIMARY KEY,
                                  token_hash BLOB NOT NULL UNIQUE,
                                  subject TEXT NOT NULL,
                                  identity TEXT NOT NULL,
                                  provider_groups_json TEXT NOT NULL,
                                  required_group TEXT,
                                  client_address TEXT,
                                  created_at_ms INTEGER NOT NULL,
                                  expires_at_ms INTEGER NOT NULL,
                                  last_used_at_ms INTEGER
                                );
                                CREATE INDEX IF NOT EXISTS ix_terminal_tokens_subject
                                  ON terminal_tokens(subject, created_at_ms);
                                """;

    // The correlation episodes the daemons push: one row per period during which a pair of
    // findings kept following each other, cut by the daemon, which alone knows whether the
    // pair still co-occurs. Keyed on (id, source_id) like incidents, since two daemons can
    // see one pair. Each side sits in columns so a read filters on a service. started_at_ms,
    // last_seen_ms and ended_at_ms are the daemon's clock, first_seen_ms and
    // last_seen_hub_ms the Hub's. A rowid table because the purge deletes by rowid.
    internal const string V11 = """
                                CREATE TABLE IF NOT EXISTS correlation_episodes (
                                  id TEXT NOT NULL,
                                  source_id TEXT NOT NULL,
                                  pair_key TEXT NOT NULL,
                                  source_type TEXT NOT NULL,
                                  source_service TEXT NOT NULL,
                                  source_template TEXT NOT NULL,
                                  source_grouping TEXT,
                                  source_grouping_value TEXT,
                                  target_type TEXT NOT NULL,
                                  target_service TEXT NOT NULL,
                                  target_template TEXT NOT NULL,
                                  target_grouping TEXT,
                                  target_grouping_value TEXT,
                                  started_at_ms INTEGER NOT NULL,
                                  last_seen_ms INTEGER NOT NULL,
                                  ended_at_ms INTEGER,
                                  gap_ms INTEGER NOT NULL,
                                  peak_confidence REAL NOT NULL,
                                  peak_co_occurrences INTEGER NOT NULL,
                                  last_confidence REAL NOT NULL,
                                  last_co_occurrences INTEGER NOT NULL,
                                  median_lag_ms REAL,
                                  sample_trace_id TEXT,
                                  source_sample_trace_id TEXT,
                                  first_seen_ms INTEGER NOT NULL,
                                  last_seen_hub_ms INTEGER NOT NULL,
                                  PRIMARY KEY(id, source_id)
                                );
                                CREATE INDEX IF NOT EXISTS ix_episodes_listing
                                  ON correlation_episodes(started_at_ms DESC, id, source_id);
                                CREATE INDEX IF NOT EXISTS ix_episodes_pair
                                  ON correlation_episodes(source_id, pair_key, started_at_ms);
                                CREATE INDEX IF NOT EXISTS ix_episodes_last_seen
                                  ON correlation_episodes(last_seen_ms);
                                """;

    // The signatures a source first saw since a moment, which SignatureRate reads every
    // five minutes: a range on this index rather than a scan of every row the source holds.
    internal const string V12 = """
                                CREATE INDEX IF NOT EXISTS ix_finding_sources_first_seen
                                  ON finding_sources(source_id, first_seen_ms);
                                """;

    // Periodic disclosures a scheduled job publishes. The files are kept exactly as received in BLOBs,
    // so the backup's VACUUM INTO carries them, and a listing never selects those columns.
    internal const string V13 = """
                                CREATE TABLE IF NOT EXISTS disclosures (
                                  id TEXT PRIMARY KEY,
                                  report_uuid TEXT NOT NULL UNIQUE,
                                  organisation TEXT NOT NULL,
                                  period_type TEXT NOT NULL,
                                  period_from TEXT NOT NULL,
                                  period_to TEXT NOT NULL,
                                  intent TEXT NOT NULL,
                                  confidentiality TEXT NOT NULL,
                                  content_hash TEXT NOT NULL,
                                  integrity_level TEXT,
                                  figures_json TEXT,
                                  report BLOB NOT NULL,
                                  attestation BLOB,
                                  bundle BLOB,
                                  pdf BLOB,
                                  render_reason TEXT,
                                  engine_version TEXT,
                                  hash_checked INTEGER,
                                  binding_checked INTEGER,
                                  published_at_ms INTEGER NOT NULL,
                                  signature_added_at_ms INTEGER,
                                  withdrawn_at_ms INTEGER,
                                  withdrawn_reason TEXT,
                                  withdrawn_by TEXT
                                );
                                CREATE INDEX IF NOT EXISTS ix_disclosures_period
                                  ON disclosures(period_to DESC, id);
                                CREATE TABLE IF NOT EXISTS disclosure_share_links (
                                  id TEXT PRIMARY KEY,
                                  disclosure_id TEXT NOT NULL,
                                  token_hash BLOB NOT NULL UNIQUE,
                                  created_by TEXT NOT NULL,
                                  created_at_ms INTEGER NOT NULL,
                                  expires_at_ms INTEGER NOT NULL,
                                  revoked_at_ms INTEGER,
                                  revoked_by TEXT,
                                  downloads INTEGER NOT NULL DEFAULT 0,
                                  last_used_at_ms INTEGER,
                                  last_client TEXT
                                );
                                CREATE INDEX IF NOT EXISTS ix_share_links_disclosure
                                  ON disclosure_share_links(disclosure_id);
                                CREATE TABLE IF NOT EXISTS disclosure_deliveries (
                                  id TEXT PRIMARY KEY,
                                  disclosure_id TEXT NOT NULL,
                                  webhook TEXT NOT NULL,
                                  event TEXT NOT NULL,
                                  state TEXT NOT NULL,
                                  attempts INTEGER NOT NULL DEFAULT 0,
                                  created_at_ms INTEGER NOT NULL,
                                  last_attempt_ms INTEGER,
                                  next_attempt_ms INTEGER,
                                  last_status INTEGER
                                );
                                CREATE INDEX IF NOT EXISTS ix_deliveries_due
                                  ON disclosure_deliveries(state, next_attempt_ms);
                                CREATE INDEX IF NOT EXISTS ix_deliveries_disclosure
                                  ON disclosure_deliveries(disclosure_id);
                                """;

    // When each source last pushed findings, apart from source_imports, which
    // incident and correlation pushes refresh too: only a findings push delivers
    // what a capped or outrun poll missed. A table of its own rather than a
    // column, so an existing database needs no ALTER.
    internal const string V14 = """
                                CREATE TABLE IF NOT EXISTS source_finding_imports (
                                  source_id TEXT PRIMARY KEY,
                                  last_import_ms INTEGER NOT NULL
                                );
                                """;

    // The daemons' detection ledgers: absolute values per UTC epoch day, merged by MAX
    // (MIN for a sampling rate), so a poll, a push, a replay or an overlap all land on
    // the same row. read_from_day is the poll's cursor. Kept rowid tables with no foreign
    // key, like V6, because the purge deletes them in chunks by rowid.
    internal const string V15 = """
                                CREATE TABLE IF NOT EXISTS detection_ledgers (
                                  source_id TEXT NOT NULL,
                                  ledger_id TEXT NOT NULL,
                                  started_ms INTEGER NOT NULL,
                                  first_read_ms INTEGER NOT NULL,
                                  last_read_ms INTEGER NOT NULL,
                                  polled INTEGER NOT NULL,
                                  pushed INTEGER NOT NULL,
                                  read_from_day INTEGER,
                                  PRIMARY KEY(source_id, ledger_id)
                                );
                                CREATE TABLE IF NOT EXISTS detection_days (
                                  source_id TEXT NOT NULL,
                                  ledger_id TEXT NOT NULL,
                                  day INTEGER NOT NULL,
                                  sealed INTEGER NOT NULL,
                                  gap INTEGER NOT NULL,
                                  overflow_detections INTEGER NOT NULL,
                                  overflow_extra_io_ops INTEGER,
                                  shed_traces INTEGER NOT NULL,
                                  ingest_rejected INTEGER NOT NULL,
                                  sampling_rate REAL NOT NULL,
                                  last_read_ms INTEGER NOT NULL,
                                  PRIMARY KEY(source_id, ledger_id, day)
                                );
                                CREATE INDEX IF NOT EXISTS ix_detection_days_day ON detection_days(day);
                                CREATE TABLE IF NOT EXISTS detection_counts (
                                  signature TEXT NOT NULL,
                                  source_id TEXT NOT NULL,
                                  ledger_id TEXT NOT NULL,
                                  day INTEGER NOT NULL,
                                  detections INTEGER NOT NULL,
                                  extra_io_ops INTEGER,
                                  PRIMARY KEY(signature, source_id, ledger_id, day)
                                );
                                CREATE INDEX IF NOT EXISTS ix_detection_counts_day ON detection_counts(day);
                                """;

    // What a team declared about a finding, one action per signature, grouping-blind like an ack
    // so a finding seen under two groupings carries one. The events are append-only, with no
    // foreign key and no purge: an action outlives its finding's retention, so a recap can still
    // confirm it once the finding is gone.
    internal const string V16 = """
                                CREATE TABLE IF NOT EXISTS finding_actions (
                                  signature TEXT PRIMARY KEY,
                                  status TEXT NOT NULL,
                                  owner TEXT,
                                  created_by TEXT NOT NULL,
                                  created_at_ms INTEGER NOT NULL,
                                  updated_at_ms INTEGER NOT NULL,
                                  done_at_ms INTEGER
                                );
                                CREATE TABLE IF NOT EXISTS finding_action_events (
                                  id INTEGER PRIMARY KEY,
                                  signature TEXT NOT NULL,
                                  from_status TEXT,
                                  to_status TEXT NOT NULL,
                                  owner TEXT,
                                  by TEXT NOT NULL,
                                  at_ms INTEGER NOT NULL
                                );
                                CREATE INDEX IF NOT EXISTS ix_action_events_signature ON finding_action_events(signature, at_ms);
                                """;
}
