using Microsoft.Data.Sqlite;

namespace PerfSentinelHub.Storage;

public sealed partial class HubDatabase
{
    // Whether AttributesBackfill has anything to write, each arm its read-only mirror: the
    // backfill takes the write lock for as long as it scans, so it runs only when this says so.
    private const string AttributesBackfillNeeded =
        """
        SELECT EXISTS (
                 SELECT 1 FROM finding_sources
                 WHERE service IS NULL
                   AND json_type(CASE WHEN json_valid(finding_json) THEN finding_json END, '$.finding.service') = 'text')
            OR EXISTS (
                 SELECT 1 FROM finding_sources AS fs
                 WHERE json_valid(fs.finding_json)
                   AND NOT EXISTS (
                     SELECT 1 FROM finding_attributes AS fa
                     WHERE fa.signature = fs.signature AND fa.source_id = fs.source_id
                       AND fa.last_seen_ms >= fs.last_seen_ms))
            OR EXISTS (
                 SELECT 1 FROM incident_attributes AS ia
                 WHERE ia.last_seen_ms IS NOT (
                   SELECT i.last_seen_ms FROM incidents AS i WHERE i.id = ia.id AND i.source_id = ia.source_id))
            OR EXISTS (
                 SELECT 1 FROM incidents AS i
                 WHERE NOT EXISTS (
                   SELECT 1 FROM incident_attributes AS ia WHERE ia.id = i.id AND ia.source_id = i.source_id));
        """;

    private static readonly string AttributesBackfill =
        """
        UPDATE finding_sources SET service = json_extract(finding_json, '$.finding.service')
        WHERE service IS NULL
          AND json_type(CASE WHEN json_valid(finding_json) THEN finding_json END, '$.finding.service') = 'text';
        """
        + FindingAttributesFrom(
            """
            SELECT CASE WHEN json_valid(fs.finding_json) THEN fs.finding_json END,
                   fs.signature, fs.source_id, fs.last_seen_ms
            FROM finding_sources AS fs
            WHERE NOT EXISTS (
              SELECT 1 FROM finding_attributes AS fa
              WHERE fa.signature = fs.signature AND fa.source_id = fs.source_id
                AND fa.last_seen_ms >= fs.last_seen_ms)
            """)
        + """
          DELETE FROM incident_attributes WHERE last_seen_ms IS NOT (
            SELECT i.last_seen_ms FROM incidents AS i
            WHERE i.id = incident_attributes.id AND i.source_id = incident_attributes.source_id);
          """
        + IncidentAttributesWhere("true");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (IsReady)
                return;

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = "PRAGMA journal_mode=WAL;";
                await pragmas.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var transaction = connection.BeginTransaction(false);
            await using (var migration = connection.CreateCommand())
            {
                migration.Transaction = transaction;
                migration.CommandText =
                    Schema.V1 + Schema.V2 + Schema.V3 + Schema.V4 + Schema.V5 + Schema.V6 + Schema.V7 + Schema.V8 +
                    Schema.V9 + Schema.V10 + Schema.V11 + Schema.V12 + Schema.V13 + Schema.V14 +
                    Schema.V15 + Schema.V16 + Schema.V17 + Schema.V18 + Schema.V19 + Schema.V20;
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }

            await EnsureLineageColumnsAsync(connection, transaction, cancellationToken);
            await EnsureFindingSourceColumnsAsync(connection, transaction, cancellationToken);
            await EnsureFindingSourceServiceColumnAsync(connection, transaction, cancellationToken);
            await EnsureDetectionDayCarbonColumnsAsync(connection, transaction, cancellationToken);
            await EnsureActionOwnerDisplayColumnsAsync(connection, transaction, cancellationToken);
            await EnsureRecapCreatorSubjectColumnAsync(connection, transaction, cancellationToken);
            await EnsureDetectionSignalColumnsAsync(connection, transaction, cancellationToken);
            await EnsureActionFrozenFindingColumnAsync(connection, transaction, cancellationToken);
            await BackfillGroupingsAsync(connection, transaction, cancellationToken);

            await using (var version = connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = """
                                      INSERT OR IGNORE INTO schema_migrations(version, applied_at_ms)
                                      VALUES (1, $applied_at_ms), (2, $applied_at_ms), (3, $applied_at_ms),
                                             (4, $applied_at_ms), (5, $applied_at_ms), (6, $applied_at_ms),
                                             (7, $applied_at_ms), (8, $applied_at_ms), (9, $applied_at_ms),
                                             (10, $applied_at_ms), (11, $applied_at_ms), (12, $applied_at_ms),
                                             (13, $applied_at_ms), (14, $applied_at_ms), (15, $applied_at_ms),
                                             (16, $applied_at_ms), (17, $applied_at_ms), (18, $applied_at_ms),
                                             (19, $applied_at_ms), (20, $applied_at_ms);
                                      """;
                version.Parameters.AddWithValue(
                    "$applied_at_ms",
                    timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                await version.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            Volatile.Write(ref _ready, 1);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    /// <summary>
    ///     CREATE TABLE IF NOT EXISTS never alters a table that already
    ///     exists, so a database created before the lineage origin was
    ///     denormalized keeps the old three-column shape and every insert
    ///     fails on the missing columns. Add them when absent and backfill
    ///     from the row itself: every pre-existing link is a single hop, so
    ///     its origin is the predecessor's own first_seen and its depth is 1.
    /// </summary>
    private static async Task EnsureLineageColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        // Schema.V2's CREATE TABLE ran earlier in this transaction, so the
        // table exists. Only its column set is in question.
        if (await HasColumnAsync(
                connection, transaction, "finding_lineage", "origin_first_seen_ms", cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = """
                            ALTER TABLE finding_lineage ADD COLUMN origin_first_seen_ms INTEGER NOT NULL DEFAULT 0;
                            ALTER TABLE finding_lineage ADD COLUMN depth INTEGER NOT NULL DEFAULT 1;
                            UPDATE finding_lineage SET origin_first_seen_ms = predecessor_first_seen_ms;
                            """;
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Each source's own copy of a finding. The columns are nullable and
    ///     nothing is backfilled: a row written before them reads NULL until
    ///     its source is observed again, and a read falls back to the shared
    ///     findings row meanwhile. The three land together, so one probe
    ///     answers for all of them.
    /// </summary>
    private static async Task EnsureFindingSourceColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, transaction, "finding_sources", "finding_json", cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = """
                            ALTER TABLE finding_sources ADD COLUMN finding_json TEXT;
                            ALTER TABLE finding_sources ADD COLUMN severity TEXT;
                            ALTER TABLE finding_sources ADD COLUMN first_observed_day INTEGER;
                            """;
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Before version 8 a grouping lived only in each source's own copy of
    ///     the envelope. Read once, on the boot that creates the table: later
    ///     observations write their own rows, and the purge must not be undone.
    ///     A row without its own copy is left out rather than credited with the
    ///     shared envelope, which may be another source's. An envelope that is
    ///     not valid JSON is skipped, so it cannot keep the Hub from starting.
    /// </summary>
    private static async Task BackfillGroupingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE version = 8);";
        if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 1)
            return;

        command.CommandText = """
                              INSERT OR IGNORE INTO finding_groupings(signature, source_id, grouping, last_seen_ms)
                              SELECT signature, source_id, grouping, last_seen_ms FROM (
                                SELECT signature, source_id, last_seen_ms,
                                       CASE WHEN json_valid(finding_json)
                                         THEN json_extract(finding_json, '$.finding.grouping[0].value')
                                       END AS grouping
                                FROM finding_sources)
                              WHERE typeof(grouping) = 'text' AND grouping <> '';
                              """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Each copy's own service, which findings.service cannot stand for: that
    ///     column is fleet-wide, and the newest observation from any source
    ///     overwrites it. Filled from the copy's own envelope by
    ///     BackfillAttributesAsync.
    /// </summary>
    private static async Task EnsureFindingSourceServiceColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, transaction, "finding_sources", "service", cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = "ALTER TABLE finding_sources ADD COLUMN service TEXT;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     The day's avoidable grams (mid and low), avoidable I/O and total I/O, as a
    ///     daemon that scores carbon reports them. NULL on a row an older engine wrote.
    /// </summary>
    private static async Task EnsureDetectionDayCarbonColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, transaction, "detection_days", "avoidable_gco2", cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = """
                            ALTER TABLE detection_days ADD COLUMN avoidable_gco2 REAL;
                            ALTER TABLE detection_days ADD COLUMN avoidable_io_ops INTEGER;
                            ALTER TABLE detection_days ADD COLUMN avoidable_gco2_low REAL;
                            ALTER TABLE detection_days ADD COLUMN total_io_ops INTEGER;
                            """;
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     The C1 signal: the passes below the threshold of a watched signature, and the thresholds
    ///     the daemon applied that day, named as the engine sends them. NULL on a row an older engine wrote.
    /// </summary>
    private static async Task EnsureDetectionSignalColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(
                connection, transaction, "detection_counts", "below_threshold_passes", cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = """
                            ALTER TABLE detection_counts ADD COLUMN below_threshold_passes INTEGER;
                            ALTER TABLE detection_days ADD COLUMN slow_query_threshold_ms INTEGER;
                            ALTER TABLE detection_days ADD COLUMN slow_http_threshold_ms INTEGER;
                            ALTER TABLE detection_days ADD COLUMN max_fanout INTEGER;
                            ALTER TABLE detection_days ADD COLUMN chatty_threshold INTEGER;
                            ALTER TABLE detection_days ADD COLUMN pool_saturation_threshold INTEGER;
                            ALTER TABLE detection_days ADD COLUMN serialized_min_calls INTEGER;
                            """;
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     The finding's envelope as it stood when its action entered done, for the share card. NULL
    ///     while the action is not done and for an action done before the column existed.
    /// </summary>
    private static async Task EnsureActionFrozenFindingColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(
                connection, transaction, "finding_actions", "frozen_finding_json", cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = "ALTER TABLE finding_actions ADD COLUMN frozen_finding_json TEXT;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     The name the SSO gave an action's owner, kept beside the owner it was read with. NULL for
    ///     a hand-typed owner and for an action filed before the column existed.
    /// </summary>
    private static async Task EnsureActionOwnerDisplayColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, transaction, "finding_actions", "owner_display", cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = """
                            ALTER TABLE finding_actions ADD COLUMN owner_display TEXT;
                            ALTER TABLE finding_action_events ADD COLUMN owner_display TEXT;
                            """;
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     The creator's immutable subject beside the name an on-demand report shows. NULL for a
    ///     scheduled report and for one generated before the column existed, which then has no owner.
    /// </summary>
    private static async Task EnsureRecapCreatorSubjectColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, transaction, "recap_reports", "created_by_subject", cancellationToken))
            return;

        await using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = "ALTER TABLE recap_reports ADD COLUMN created_by_subject TEXT;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     The version 9 derivation below, run after the Hub answers rather than in
    ///     InitializeAsync: it scans every copy, which on a large database outlasted
    ///     the startup probe and got the boot killed and started over. A read-only
    ///     check runs first, and the write gate is taken only when there is
    ///     something to write. Until it lands, a copy an older build wrote matches no
    ///     attribute scope. True when it wrote.
    /// </summary>
    public async Task<bool> CatchUpAttributesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = AttributesBackfillNeeded;
            if ((long)(await check.ExecuteScalarAsync(cancellationToken))! == 0)
                return false;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = connection.BeginTransaction(false);
            await BackfillAttributesAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    ///     What version 9 derives from rows an older build may have written. Run
    ///     on every start rather than once, because an image rolled back below it
    ///     and forward again writes copies and incidents without it in between.
    ///     A copy's service and attributes come from its own envelope, so a row
    ///     without one keeps none until its source observes it again, and an
    ///     envelope that is not valid JSON is skipped. Only a copy whose newest
    ///     observation left no attribute row is read, so a purged attribute is
    ///     never read back. Every write of this build keeps an incident copy's
    ///     stamp equal to its last_seen_ms, so only a copy with no row, or with a
    ///     stamp an older build's write left behind, is read again, and the rows of
    ///     a copy an older build purged go too.
    /// </summary>
    private static async Task BackfillAttributesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = AttributesBackfill;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var probe = connection.CreateCommand();
        probe.Transaction = transaction;
        probe.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name = $column;";
        probe.Parameters.AddWithValue("$table", table);
        probe.Parameters.AddWithValue("$column", column);
        return await probe.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
