using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Storage;

public sealed class StorageTests
{
    private const string Groupings = """
                                     SELECT group_concat(source_id || ' ' || grouping || ' ' || last_seen_ms, ',')
                                     FROM (SELECT * FROM finding_groupings ORDER BY source_id);
                                     """;

    [Fact]
    public async Task Initialize_is_idempotent_and_creates_the_schema()
    {
        using var fixture = TestDatabase.Create();
        var database = fixture.Database;
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.False(database.IsReady);

        await database.InitializeAsync(cancellationToken);
        await database.InitializeAsync(cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var names = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT name FROM sqlite_master
                              WHERE type = 'table' AND name NOT LIKE 'sqlite_%';
                              """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        Assert.Equal(
            [
                "ack_reads", "analysis_runs", "card_keys", "correlation_episodes", "detection_counts", "detection_days",
                "detection_ledgers", "disclosure_deliveries", "disclosure_share_links",
                "disclosures", "endpoint_heartbeats", "endpoint_labels", "finding_action_events",
                "finding_actions", "finding_attributes", "finding_groupings", "finding_lineage", "finding_observations",
                "finding_sources", "findings", "incident_attributes", "incident_reads", "incidents",
                "recap_deliveries", "recap_reports", "schema_migrations", "service_labels", "source_acks",
                "source_finding_imports", "source_imports", "source_state", "terminal_tokens"
            ],
            names.Order(StringComparer.Ordinal));
        Assert.True(database.IsReady);
    }

    [Fact]
    public async Task Initialize_records_every_schema_version()
    {
        using var fixture = TestDatabase.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Database.InitializeAsync(cancellationToken);

        await using var connection = await fixture.Database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT group_concat(version, ',') FROM (SELECT version FROM schema_migrations ORDER BY version);";
        Assert.Equal("1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20",
            (string)(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task Reinitializing_the_same_file_preserves_rows()
    {
        using var fixture = TestDatabase.Create();
        var path = fixture.DatabasePath;
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = fixture.Database;
        await first.InitializeAsync(cancellationToken);

        await using (var connection = await first.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  INSERT INTO source_state(source_id, last_attempt_ms)
                                  VALUES ('production', 1234);
                                  """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using var restarted = TestDatabase.Create(path);
        await restarted.Database.InitializeAsync(cancellationToken);
        await using var reopened = await restarted.Database.OpenConnectionAsync(cancellationToken);
        await using var query = reopened.CreateCommand();
        query.CommandText = "SELECT last_attempt_ms FROM source_state WHERE source_id = 'production';";
        Assert.Equal(1234L, (long)(await query.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task Push_into_a_new_source_does_not_record_a_poll_attempt()
    {
        using var fixture = TestDatabase.Create();
        var database = fixture.Database;
        var cancellationToken = TestContext.Current.CancellationToken;
        await database.InitializeAsync(cancellationToken);
        var finding = new ParsedFinding(
            "signature", "{}", "checkout", "slow_sql", "warning", "POST /checkout",
            "template-hash", "trace", "daemon_production", 4, null);

        Assert.True(await database.TryUpsertBatchAsync(
            new SourceSnapshot("push-only", "Push only", "production", "0.11.3"),
            new ParsedBatch([finding], 0),
            1234,
            cancellationToken));

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM source_state WHERE source_id = 'push-only';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(cancellationToken))!);

        command.CommandText = """
                              SELECT producer_version FROM finding_sources
                              WHERE source_id = 'push-only' AND signature = 'signature';
                              """;
        Assert.Equal("0.11.3", (string)(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task Initialize_adds_the_lineage_columns_to_a_pre_migration_database()
    {
        using var fixture = TestDatabase.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Database.InitializeAsync(cancellationToken);

        // Roll one linked row back to the shape finding_lineage had before the
        // origin and depth columns existed. Dropping them beats hand-writing
        // the old DDL: Schema.V1's CREATE TABLE IF NOT EXISTS would leave a
        // duplicated definition behind and it would drift silently.
        await using (var connection = await fixture.Database.OpenConnectionAsync(cancellationToken))
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                               INSERT INTO findings(
                                 signature, finding_json, service, finding_type, severity, endpoint,
                                 template_hash, sample_trace_id, first_seen_ms, last_seen_ms,
                                 max_confidence, max_confidence_rank)
                               VALUES ('successor', '{}', 'order-service', 'n_plus_one_sql', 'warning',
                                 'GET /orders', 'hash-b', 'trace', 500, 900, 'daemon_staging', 2);
                               INSERT INTO finding_lineage(
                                 successor_signature, predecessor_signature, predecessor_first_seen_ms,
                                 origin_first_seen_ms, depth, linked_at_ms, method)
                               VALUES ('successor', 'predecessor', 100, 100, 1, 500, 'endpoint_template');
                               ALTER TABLE finding_lineage DROP COLUMN origin_first_seen_ms;
                               ALTER TABLE finding_lineage DROP COLUMN depth;
                               """;
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        // A NEW instance, because InitializeAsync short-circuits on an
        // in-process flag: the upgrade only ever happens in a fresh process
        // opening a file an older binary wrote.
        using var restarted = TestDatabase.Create(fixture.DatabasePath);
        await restarted.Database.InitializeAsync(cancellationToken);
        // And once more, since a third boot must not replay the ALTER: the
        // probe short-circuits, and a repeated ALTER throws "duplicate column".
        using var restartedAgain = TestDatabase.Create(fixture.DatabasePath);
        await restartedAgain.Database.InitializeAsync(cancellationToken);

        await using var reopened = await restartedAgain.Database.OpenConnectionAsync(cancellationToken);
        await using var read = reopened.CreateCommand();
        read.CommandText = """
                           SELECT origin_first_seen_ms, depth FROM finding_lineage
                           WHERE successor_signature = 'successor';
                           """;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        // Backfilled from the only date the old row carried, not left at the
        // column default: a zero origin would date every pre-migration chain
        // to 1970.
        Assert.Equal(100, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task Initialize_adds_the_source_copy_columns_to_a_pre_migration_database()
    {
        using var fixture = TestDatabase.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Database.InitializeAsync(cancellationToken);
        List<string> freshShape;

        // Rolled back the way the lineage case above is: drop everything the
        // migration adds rather than hand-write the old seven-column DDL.
        // Dropping the table takes ix_observations_day with it.
        await using (var connection = await fixture.Database.OpenConnectionAsync(cancellationToken))
        {
            freshShape = await SourceColumnsAsync(connection, cancellationToken);
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                               INSERT INTO findings(
                                 signature, finding_json, service, finding_type, severity, endpoint,
                                 template_hash, sample_trace_id, first_seen_ms, last_seen_ms,
                                 max_confidence, max_confidence_rank)
                               VALUES ('signature', '{}', 'checkout', 'slow_sql', 'warning',
                                 'POST /checkout', 'template-hash', 'trace', 500, 900, 'daemon_production', 4);
                               INSERT INTO finding_sources(
                                 signature, source_id, source_name, environment, producer_version,
                                 first_seen_ms, last_seen_ms)
                               VALUES ('signature', 'production-a', 'Production A', 'production', '0.11.2', 500, 900);
                               ALTER TABLE finding_sources DROP COLUMN finding_json;
                               ALTER TABLE finding_sources DROP COLUMN severity;
                               ALTER TABLE finding_sources DROP COLUMN first_observed_day;
                               ALTER TABLE finding_sources DROP COLUMN service;
                               DROP INDEX ix_finding_sources_source;
                               DROP TABLE finding_observations;
                               DELETE FROM schema_migrations WHERE version = 6;
                               """;
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        using var restarted = TestDatabase.Create(fixture.DatabasePath);
        await restarted.Database.InitializeAsync(cancellationToken);
        // A third boot must not replay the ALTER, which throws "duplicate column".
        using var restartedAgain = TestDatabase.Create(fixture.DatabasePath);
        await restartedAgain.Database.InitializeAsync(cancellationToken);

        await using var reopened = await restartedAgain.Database.OpenConnectionAsync(cancellationToken);
        var upgradedShape = await SourceColumnsAsync(reopened, cancellationToken);
        Assert.Equal(freshShape, upgradedShape);
        Assert.Equal(
            ["finding_json TEXT 0", "severity TEXT 0", "first_observed_day INTEGER 0", "service TEXT 0"],
            upgradedShape.Skip(7));

        await using var read = reopened.CreateCommand();
        read.CommandText = """
                           SELECT COUNT(*) FROM sqlite_master WHERE type = 'index'
                             AND name IN ('ix_observations_day', 'ix_finding_sources_source');
                           """;
        Assert.Equal(2L, (long)(await read.ExecuteScalarAsync(cancellationToken))!);
        read.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 6;";
        Assert.Equal(1L, (long)(await read.ExecuteScalarAsync(cancellationToken))!);

        // No backfill: the old row reads NULL until its source is observed again.
        read.CommandText = """
                           SELECT COUNT(*) FROM finding_sources
                           WHERE finding_json IS NULL AND severity IS NULL AND first_observed_day IS NULL;
                           """;
        Assert.Equal(1L, (long)(await read.ExecuteScalarAsync(cancellationToken))!);

        // The columns existing is not enough: the upgraded table must accept
        // the write the production path issues.
        await restartedAgain.Database.UpsertBatchAsync(
            new SourceSnapshot("production-a", "Production A", "production", "0.11.3"),
            new ParsedBatch(
                [
                    new ParsedFinding(
                        "signature", "{}", "checkout", "slow_sql", "warning", "POST /checkout",
                        "template-hash", "trace", "daemon_production", 4, null)
                ],
                0),
            86_400_000,
            cancellationToken);
        read.CommandText = "SELECT severity || ' ' || first_observed_day || ' ' || service FROM finding_sources;";
        Assert.Equal("warning 1 checkout", (string)(await read.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task Initialize_backfills_groupings_from_each_source_copy_once()
    {
        using var fixture = TestDatabase.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Database.InitializeAsync(cancellationToken);

        // A database from before version 8: the groupings only in the envelopes. A row
        // without its own copy must not take the shared one, which may be another source's.
        await using (var connection = await fixture.Database.OpenConnectionAsync(cancellationToken))
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                               INSERT INTO findings(
                                 signature, finding_json, service, finding_type, severity, endpoint,
                                 template_hash, sample_trace_id, first_seen_ms, last_seen_ms,
                                 max_confidence, max_confidence_rank)
                               VALUES ('signature', '{"finding":{"grouping":[{"key":"k8s.namespace.name","value":"shared"}]}}',
                                 'checkout', 'slow_sql', 'warning', 'POST /checkout', 'template-hash', 'trace',
                                 500, 900, 'daemon_production', 4);
                               INSERT INTO finding_sources(
                                 signature, source_id, source_name, environment, producer_version,
                                 first_seen_ms, last_seen_ms, finding_json)
                               VALUES
                                 ('signature', 'own', 'Own', 'staging', '0.26.2', 500, 800,
                                  '{"finding":{"grouping":[{"key":"k8s.namespace.name","value":"test"}]}}'),
                                 ('signature', 'legacy', 'Legacy', 'staging', '0.11.2', 500, 900, NULL),
                                 ('signature', 'none', 'None', 'staging', '0.26.2', 500, 700,
                                  '{"finding":{"grouping":[]}}'),
                                 ('signature', 'garbled', 'Garbled', 'staging', '0.26.2', 500, 600, '{not json');
                               DROP TABLE finding_groupings;
                               DELETE FROM schema_migrations WHERE version = 8;
                               """;
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        using var restarted = TestDatabase.Create(fixture.DatabasePath);
        await restarted.Database.InitializeAsync(cancellationToken);
        await using (var connection = await restarted.Database.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = Groupings;
            Assert.Equal("own test 800", (string)(await command.ExecuteScalarAsync(cancellationToken))!);
            // What the retention leaves: a later boot must not read it back from the envelopes.
            command.CommandText = "DELETE FROM finding_groupings;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using var restartedAgain = TestDatabase.Create(fixture.DatabasePath);
        await restartedAgain.Database.InitializeAsync(cancellationToken);

        await using var reopened = await restartedAgain.Database.OpenConnectionAsync(cancellationToken);
        await using var read = reopened.CreateCommand();
        read.CommandText = Groupings;
        Assert.IsType<DBNull>(await read.ExecuteScalarAsync(cancellationToken));
    }

    [Fact]
    public async Task A_v15_database_gains_the_carbon_columns()
    {
        using var fixture = TestDatabase.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Database.InitializeAsync(cancellationToken);

        // Dropped rather than hand-written as the old DDL, as the cases above do.
        await using (var connection = await fixture.Database.OpenConnectionAsync(cancellationToken))
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                               ALTER TABLE detection_days DROP COLUMN avoidable_gco2;
                               ALTER TABLE detection_days DROP COLUMN avoidable_io_ops;
                               ALTER TABLE detection_days DROP COLUMN avoidable_gco2_low;
                               ALTER TABLE detection_days DROP COLUMN total_io_ops;
                               INSERT INTO detection_days(
                                 source_id, ledger_id, day, sealed, gap, overflow_detections, shed_traces,
                                 ingest_rejected, sampling_rate, last_read_ms)
                               VALUES ('prod', 'ledger', 20400, 1, 0, 0, 0, 0, 1.0, 500);
                               """;
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        using var restarted = TestDatabase.Create(fixture.DatabasePath);
        await restarted.Database.InitializeAsync(cancellationToken);
        // A third boot must not replay the ALTER.
        using var restartedAgain = TestDatabase.Create(fixture.DatabasePath);
        await restartedAgain.Database.InitializeAsync(cancellationToken);

        await using var reopened = await restartedAgain.Database.OpenConnectionAsync(cancellationToken);
        await using var read = reopened.CreateCommand();
        read.CommandText = """
                           SELECT (SELECT group_concat(name || ' ' || type, ',') FROM
                                    (SELECT name, type FROM pragma_table_info('detection_days')
                                     WHERE name IN ('avoidable_gco2', 'avoidable_io_ops', 'avoidable_gco2_low',
                                                    'total_io_ops') ORDER BY cid))
                             || ' | ' || (SELECT IFNULL(avoidable_gco2, '-') || IFNULL(avoidable_io_ops, '-')
                                          || IFNULL(avoidable_gco2_low, '-') || IFNULL(total_io_ops, '-')
                                          FROM detection_days);
                           """;
        Assert.Equal(
            "avoidable_gco2 REAL,avoidable_io_ops INTEGER,avoidable_gco2_low REAL,total_io_ops INTEGER | ----",
            (string)(await read.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task A_v16_database_gains_the_owner_display_columns()
    {
        using var fixture = TestDatabase.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Database.InitializeAsync(cancellationToken);

        // Dropped rather than hand-written as the old DDL, as the cases above do.
        await using (var connection = await fixture.Database.OpenConnectionAsync(cancellationToken))
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                               ALTER TABLE finding_actions DROP COLUMN owner_display;
                               ALTER TABLE finding_action_events DROP COLUMN owner_display;
                               INSERT INTO finding_actions(signature, status, owner, created_by, created_at_ms, updated_at_ms)
                               VALUES ('sig', 'todo', 'alice', 'alice', 1, 1);
                               """;
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        using var restarted = TestDatabase.Create(fixture.DatabasePath);
        await restarted.Database.InitializeAsync(cancellationToken);
        // A third boot must not replay the ALTER.
        using var restartedAgain = TestDatabase.Create(fixture.DatabasePath);
        await restartedAgain.Database.InitializeAsync(cancellationToken);

        await using var reopened = await restartedAgain.Database.OpenConnectionAsync(cancellationToken);
        await using var read = reopened.CreateCommand();
        read.CommandText = """
                           SELECT (SELECT group_concat(table_name || '.' || name || ' ' || type, ',') FROM
                                    (SELECT 'finding_actions' AS table_name, name, type FROM pragma_table_info('finding_actions')
                                     WHERE name = 'owner_display'
                                     UNION ALL
                                     SELECT 'finding_action_events', name, type FROM pragma_table_info('finding_action_events')
                                     WHERE name = 'owner_display'))
                             || ' | ' || (SELECT IFNULL(owner_display, '-') FROM finding_actions);
                           """;
        Assert.Equal(
            "finding_actions.owner_display TEXT,finding_action_events.owner_display TEXT | -",
            (string)(await read.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task A_v19_database_gains_the_signal_columns()
    {
        using var fixture = TestDatabase.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Database.InitializeAsync(cancellationToken);

        // Dropped rather than hand-written as the old DDL, as the cases above do.
        await using (var connection = await fixture.Database.OpenConnectionAsync(cancellationToken))
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                               ALTER TABLE detection_counts DROP COLUMN below_threshold_passes;
                               ALTER TABLE detection_days DROP COLUMN slow_query_threshold_ms;
                               ALTER TABLE detection_days DROP COLUMN slow_http_threshold_ms;
                               ALTER TABLE detection_days DROP COLUMN max_fanout;
                               ALTER TABLE detection_days DROP COLUMN chatty_threshold;
                               ALTER TABLE detection_days DROP COLUMN pool_saturation_threshold;
                               ALTER TABLE detection_days DROP COLUMN serialized_min_calls;
                               INSERT INTO detection_days(
                                 source_id, ledger_id, day, sealed, gap, overflow_detections, shed_traces,
                                 ingest_rejected, sampling_rate, last_read_ms)
                               VALUES ('prod', 'ledger', 20400, 1, 0, 0, 0, 0, 1.0, 500);
                               INSERT INTO detection_counts(signature, source_id, ledger_id, day, detections)
                               VALUES ('sig', 'prod', 'ledger', 20400, 5);
                               """;
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        using var restarted = TestDatabase.Create(fixture.DatabasePath);
        await restarted.Database.InitializeAsync(cancellationToken);
        // A third boot must not replay the ALTER.
        using var restartedAgain = TestDatabase.Create(fixture.DatabasePath);
        await restartedAgain.Database.InitializeAsync(cancellationToken);

        await using var reopened = await restartedAgain.Database.OpenConnectionAsync(cancellationToken);
        await using var read = reopened.CreateCommand();
        read.CommandText = """
                           SELECT (SELECT group_concat(name || ' ' || type, ',') FROM
                                    (SELECT name, type FROM pragma_table_info('detection_counts')
                                     WHERE name = 'below_threshold_passes'
                                     UNION ALL
                                     SELECT name, type FROM pragma_table_info('detection_days')
                                     WHERE name IN ('slow_query_threshold_ms', 'slow_http_threshold_ms', 'max_fanout',
                                                    'chatty_threshold', 'pool_saturation_threshold',
                                                    'serialized_min_calls')))
                             || ' | ' || (SELECT IFNULL(below_threshold_passes, '-') FROM detection_counts)
                             || (SELECT IFNULL(slow_query_threshold_ms, '-') || IFNULL(slow_http_threshold_ms, '-')
                                        || IFNULL(max_fanout, '-') || IFNULL(chatty_threshold, '-')
                                        || IFNULL(pool_saturation_threshold, '-') || IFNULL(serialized_min_calls, '-')
                                 FROM detection_days);
                           """;
        Assert.Equal(
            "below_threshold_passes INTEGER,slow_query_threshold_ms INTEGER,slow_http_threshold_ms INTEGER," +
            "max_fanout INTEGER,chatty_threshold INTEGER,pool_saturation_threshold INTEGER," +
            "serialized_min_calls INTEGER | -------",
            (string)(await read.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task A_v19_database_gains_the_frozen_finding_column()
    {
        using var fixture = TestDatabase.Create();
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Database.InitializeAsync(cancellationToken);

        await using (var connection = await fixture.Database.OpenConnectionAsync(cancellationToken))
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                               ALTER TABLE finding_actions DROP COLUMN frozen_finding_json;
                               INSERT INTO finding_actions(
                                 signature, status, created_by, created_at_ms, updated_at_ms, done_at_ms)
                               VALUES ('sig', 'done', 'alice', 100, 100, 100);
                               """;
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        using var restarted = TestDatabase.Create(fixture.DatabasePath);
        await restarted.Database.InitializeAsync(cancellationToken);
        // A third boot must not replay the ALTER.
        using var restartedAgain = TestDatabase.Create(fixture.DatabasePath);
        await restartedAgain.Database.InitializeAsync(cancellationToken);

        await using var reopened = await restartedAgain.Database.OpenConnectionAsync(cancellationToken);
        await using var read = reopened.CreateCommand();
        read.CommandText = """
                           SELECT (SELECT name || ' ' || type FROM pragma_table_info('finding_actions')
                                   WHERE name = 'frozen_finding_json')
                             || ' | ' || (SELECT IFNULL(frozen_finding_json, '-') FROM finding_actions);
                           """;
        Assert.Equal("frozen_finding_json TEXT | -", (string)(await read.ExecuteScalarAsync(cancellationToken))!);
    }

    private static async Task<List<string>> SourceColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT name || ' ' || type || ' ' || "notnull"
                              FROM pragma_table_info('finding_sources') ORDER BY cid;
                              """;
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));
        return columns;
    }

    private sealed class TestDatabase : IDisposable
    {
        public readonly HubDatabase Database;

        public readonly string DatabasePath;

        private TestDatabase(string path)
        {
            DatabasePath = path;
            Database = new HubDatabase(
                Options.Create(new HubOptions { DatabasePath = path }),
                TimeProvider.System);
        }

        public void Dispose()
        {
            TestPool.ClearFor(DatabasePath);
            File.Delete(DatabasePath);
            File.Delete($"{DatabasePath}-shm");
            File.Delete($"{DatabasePath}-wal");
        }

        public static TestDatabase Create(string? path = null)
        {
            return new TestDatabase(
                path ?? Path.Combine(
                    Path.GetTempPath(),
                    $"perf-sentinel-hub-{Guid.NewGuid():N}.db"));
        }
    }
}
