namespace PerfSentinelHub.Tests.Storage;

internal static class LegacySchema
{
    // The finding tables as a build before version 22 left them, each envelope inline, for a test
    // that seeds what an older build wrote: the next boot moves the envelopes out. Run on an empty
    // database, since the envelopes already stored are dropped with their tables.
    internal const string InlineEnvelopes = """
                                            DROP TABLE finding_source_envelopes;
                                            DROP TABLE finding_envelopes;
                                            ALTER TABLE findings ADD COLUMN finding_json TEXT NOT NULL DEFAULT '{}';
                                            ALTER TABLE finding_sources ADD COLUMN finding_json TEXT;
                                            DELETE FROM schema_migrations WHERE version = 22;
                                            """;

    // A version 23 database: the links table and its version row are not there yet.
    internal const string WithoutLinks = """
                                         DROP TABLE incident_finding_links;
                                         DROP TRIGGER incident_signatures_insert;
                                         DROP TRIGGER incident_signatures_update;
                                         DROP TRIGGER incident_signatures_delete;
                                         DROP TABLE incident_signatures;
                                         DELETE FROM schema_migrations WHERE version = 24;
                                         """;

    // A version 24 database: detection_counts keyed without its grouping, detection_days without split.
    internal const string WithoutGroupings = """
                                             DROP TABLE detection_counts;
                                             CREATE TABLE detection_counts (
                                               signature TEXT NOT NULL,
                                               source_id TEXT NOT NULL,
                                               ledger_id TEXT NOT NULL,
                                               day INTEGER NOT NULL,
                                               detections INTEGER NOT NULL,
                                               extra_io_ops INTEGER,
                                               below_threshold_passes INTEGER,
                                               PRIMARY KEY(signature, source_id, ledger_id, day)
                                             );
                                             CREATE INDEX ix_detection_counts_day ON detection_counts(day);
                                             ALTER TABLE detection_days DROP COLUMN split;
                                             DELETE FROM schema_migrations WHERE version = 25;
                                             """;
}
