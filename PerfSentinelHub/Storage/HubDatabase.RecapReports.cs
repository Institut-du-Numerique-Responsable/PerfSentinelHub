using Microsoft.Data.Sqlite;

namespace PerfSentinelHub.Storage;

// Frozen recap reports and the deliveries that carry them out. A report row is written once,
// together with its delivery rows, and only the delivery rows change afterwards.
public sealed partial class HubDatabase
{
    private const string SelectRecapSummary = """
                                              SELECT id, subscription, period_kind, period_from, period_to, views,
                                                     complete, nothing_moved, created_by, generated_at_ms
                                              FROM recap_reports
                                              """;

    private const string SelectRecapDeliveries = """
                                                 SELECT id, report_id, channel, target, state, attempts,
                                                        last_attempt_ms, next_attempt_ms, last_status
                                                 FROM recap_deliveries
                                                 """;

    /// <summary>
    ///     Stores a report and its pending deliveries, due at once, in one transaction. False when
    ///     the subscription already holds a report for that cadence and period: nothing is written.
    /// </summary>
    public Task<bool> InsertRecapReportAsync(
        RecapReportRecord report,
        IReadOnlyList<(string Channel, string Target)> deliveries,
        CancellationToken cancellationToken)
    {
        return WriteTransactionAsync(async (connection, transaction) =>
        {
            if (!await InsertRecapReportRowAsync(connection, transaction, report, cancellationToken))
                return false;
            foreach (var (channel, target) in deliveries)
                await InsertRecapDeliveryRowAsync(
                    connection, transaction, report.Summary, channel, target, cancellationToken);
            return true;
        }, cancellationToken);
    }

    private static async Task<bool> InsertRecapReportRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecapReportRecord report,
        CancellationToken cancellationToken)
    {
        var summary = report.Summary;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO recap_reports(
                                id, subscription, period_kind, period_from, period_to, views, complete,
                                nothing_moved, created_by, created_by_subject, generated_at_ms, snapshot_json)
                              VALUES ($id, $subscription, $kind, $from, $to, $views, $complete,
                                      $nothing_moved, $created_by, $created_by_subject, $generated_at_ms, $snapshot)
                              ON CONFLICT DO NOTHING;
                              """;
        command.Parameters.AddWithValue("$id", summary.Id);
        command.Parameters.AddWithValue("$subscription", Nullable(summary.Subscription));
        command.Parameters.AddWithValue("$kind", summary.PeriodKind);
        command.Parameters.AddWithValue("$from", summary.PeriodFrom);
        command.Parameters.AddWithValue("$to", summary.PeriodTo);
        command.Parameters.AddWithValue("$views", string.Join(',', summary.Views));
        command.Parameters.AddWithValue("$complete", summary.Complete);
        command.Parameters.AddWithValue("$nothing_moved", summary.NothingMoved);
        command.Parameters.AddWithValue("$created_by", Nullable(summary.CreatedBy));
        command.Parameters.AddWithValue("$created_by_subject", Nullable(report.CreatorSubject));
        command.Parameters.AddWithValue("$generated_at_ms", summary.GeneratedAtMs);
        command.Parameters.AddWithValue("$snapshot", report.SnapshotJson);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task InsertRecapDeliveryRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecapReportSummary report,
        string channel,
        string target,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO recap_deliveries(
                                id, report_id, channel, target, state, created_at_ms, next_attempt_ms)
                              VALUES ($id, $report_id, $channel, $target, $state, $at_ms, $at_ms);
                              """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$report_id", report.Id);
        command.Parameters.AddWithValue("$channel", channel);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$state", DeliveryStates.Pending);
        command.Parameters.AddWithValue("$at_ms", report.GeneratedAtMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RecapReportExistsAsync(
        string subscription,
        string periodKind,
        string periodFrom,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT EXISTS(
                                SELECT 1 FROM recap_reports
                                WHERE subscription = $subscription AND period_kind = $kind AND period_from = $from);
                              """;
        command.Parameters.AddWithValue("$subscription", subscription);
        command.Parameters.AddWithValue("$kind", periodKind);
        command.Parameters.AddWithValue("$from", periodFrom);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))! == 1;
    }

    /// <summary>
    ///     Newest first, optionally the ones one account generated, by its subject. Never reads a snapshot.
    /// </summary>
    public async Task<IReadOnlyList<RecapReportSummary>> ListRecapReportsAsync(
        string? creatorSubject,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectRecapSummary + """

                                                   WHERE ($by IS NULL OR created_by_subject = $by)
                                                   ORDER BY generated_at_ms DESC, id
                                                   LIMIT $limit;
                                                   """;
        command.Parameters.AddWithValue("$by", Nullable(creatorSubject));
        command.Parameters.AddWithValue("$limit", limit);
        var reports = new List<RecapReportSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            reports.Add(ReadRecapSummary(reader));
        return reports;
    }

    public async Task<RecapReportRecord?> GetRecapReportAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, subscription, period_kind, period_from, period_to, views,
                                     complete, nothing_moved, created_by, generated_at_ms, snapshot_json,
                                     created_by_subject
                              FROM recap_reports WHERE id = $id;
                              """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RecapReportRecord(ReadRecapSummary(reader), reader.GetString(10), NullableString(reader, 11))
            : null;
    }

    private static RecapReportSummary ReadRecapSummary(SqliteDataReader reader)
    {
        return new RecapReportSummary(
            reader.GetString(0), NullableString(reader, 1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5).Split(','), reader.GetBoolean(6), reader.GetBoolean(7),
            NullableString(reader, 8), reader.GetInt64(9));
    }

    public Task<IReadOnlyList<RecapDeliveryRecord>> ListRecapDeliveriesAsync(
        IReadOnlyList<string> reportIds,
        CancellationToken cancellationToken)
    {
        return ReadRecapDeliveriesAsync(
            SelectRecapDeliveries + " WHERE report_id IN (SELECT value FROM json_each($key)) ORDER BY created_at_ms, rowid;",
            JsonArray(reportIds), cancellationToken);
    }

    /// <summary>Pending deliveries whose next attempt has come, oldest first. Survives a restart.</summary>
    public Task<IReadOnlyList<RecapDeliveryRecord>> DueRecapDeliveriesAsync(
        long nowMs,
        CancellationToken cancellationToken)
    {
        return ReadRecapDeliveriesAsync(
            SelectRecapDeliveries + " WHERE state = 'pending' AND next_attempt_ms <= $key ORDER BY next_attempt_ms, id;",
            nowMs, cancellationToken);
    }

    private async Task<IReadOnlyList<RecapDeliveryRecord>> ReadRecapDeliveriesAsync(
        string query,
        object key,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.Parameters.AddWithValue("$key", key);
        var deliveries = new List<RecapDeliveryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            deliveries.Add(new RecapDeliveryRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), NullableInt64(reader, 6), NullableInt64(reader, 7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8)));
        return deliveries;
    }

    /// <summary>One attempt's outcome: the status the receiver answered, or none when nothing did.</summary>
    public Task RecordRecapDeliveryAttemptAsync(
        string id,
        int? status,
        string state,
        long attemptMs,
        long? nextAttemptMs,
        CancellationToken cancellationToken)
    {
        return WriteAsync(async command =>
        {
            command.CommandText = """
                                  UPDATE recap_deliveries
                                  SET attempts = attempts + 1, last_attempt_ms = $attempt, last_status = $status,
                                      state = $state, next_attempt_ms = $next
                                  WHERE id = $id;
                                  """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$attempt", attemptMs);
            command.Parameters.AddWithValue("$status", Nullable(status));
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$next", Nullable(nextAttemptMs));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Drops the reports generated before the cutoff, then the deliveries whose report is gone.</summary>
    public Task PurgeRecapReportsAsync(long cutoffMs, CancellationToken cancellationToken)
    {
        return WriteAsync(async command =>
        {
            command.CommandText = """
                                  DELETE FROM recap_reports WHERE generated_at_ms < $cutoff;
                                  DELETE FROM recap_deliveries WHERE report_id NOT IN (SELECT id FROM recap_reports);
                                  """;
            command.Parameters.AddWithValue("$cutoff", cutoffMs);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }
}
