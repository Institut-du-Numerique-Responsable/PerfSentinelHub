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
}
