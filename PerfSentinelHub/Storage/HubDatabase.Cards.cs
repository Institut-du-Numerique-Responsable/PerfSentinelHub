using Microsoft.Data.Sqlite;
using PerfSentinelHub.Cards;

namespace PerfSentinelHub.Storage;

// The reads a share card stands on. Every list is bound as one JSON array.
public sealed partial class HubDatabase
{
    /// <summary>
    ///     The C1 watch list of one source: the fixed findings of a threshold type this source
    ///     detected at least once and has not yet confirmed fixed, newest done first. A day
    ///     confirms once sealed after the done day with passes below the threshold and no
    ///     detection of the signature at this source, whatever its ledger.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadWatchAsync(
        string sourceId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT fa.signature
                              FROM finding_actions AS fa
                              WHERE fa.status = 'done'
                                AND substr(fa.signature, 1, instr(fa.signature, ':') - 1)
                                    IN (SELECT value FROM json_each($types))
                                AND EXISTS (SELECT 1 FROM detection_counts AS seen
                                            WHERE seen.signature = fa.signature AND seen.source_id = $source_id
                                              AND seen.detections > 0)
                                AND NOT EXISTS (
                                  SELECT 1
                                  FROM detection_counts AS dc
                                  JOIN detection_days AS dd
                                    ON dd.source_id = dc.source_id AND dd.ledger_id = dc.ledger_id AND dd.day = dc.day
                                  WHERE dc.signature = fa.signature
                                    AND dc.source_id = $source_id
                                    AND dc.day > fa.done_at_ms / 86400000
                                    AND dd.sealed = 1
                                    AND dc.detections = 0
                                    AND dc.below_threshold_passes > 0
                                    AND NOT EXISTS (SELECT 1 FROM detection_counts AS d2
                                                    WHERE d2.signature = dc.signature AND d2.source_id = dc.source_id
                                                      AND d2.day = dc.day AND d2.detections > 0))
                              ORDER BY fa.done_at_ms DESC, fa.signature
                              LIMIT $limit;
                              """;
        command.Parameters.AddWithValue("$types", JsonArray([.. CardTypes.ThresholdTypes]));
        command.Parameters.AddWithValue("$source_id", sourceId);
        command.Parameters.AddWithValue("$limit", limit);

        var watch = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            watch.Add(reader.GetString(0));
        return watch;
    }

    /// <summary>The done actions that kept their finding, done within the days, oldest first.</summary>
    public async Task<IReadOnlyList<DoneAction>> ReadDoneActionsAsync(
        int fromDay,
        int toDay,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT signature, done_at_ms, frozen_finding_json
                              FROM finding_actions
                              WHERE status = 'done' AND frozen_finding_json IS NOT NULL
                                AND done_at_ms BETWEEN $from_ms AND $to_ms
                              ORDER BY done_at_ms, signature;
                              """;
        command.Parameters.AddWithValue("$from_ms", fromDay * DayMs);
        command.Parameters.AddWithValue("$to_ms", (toDay + 1L) * DayMs - 1);

        var actions = new List<DoneAction>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            actions.Add(new DoneAction(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        return actions;
    }

    /// <summary>The sources that detected each signature at least once.</summary>
    public async Task<IReadOnlyList<(string Signature, string SourceId)>> ReadCountedSourcesAsync(
        IReadOnlyList<string> signatures,
        IReadOnlyList<string> sourceIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT DISTINCT signature, source_id FROM detection_counts
                              WHERE signature IN (SELECT value FROM json_each($signatures))
                                AND source_id IN (SELECT value FROM json_each($source_ids))
                                AND detections > 0;
                              """;
        command.Parameters.AddWithValue("$signatures", JsonArray(signatures));
        command.Parameters.AddWithValue(SourceIdsParameter, JsonArray(sourceIds));

        var pairs = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            pairs.Add((reader.GetString(0), reader.GetString(1)));
        return pairs;
    }

    /// <summary>Every ledger day of the sources in the range, with the activity counts a curve sums.</summary>
    public async Task<IReadOnlyList<DayTotals>> ReadDayTotalsAsync(
        IReadOnlyList<string> sourceIds,
        int fromDay,
        int toDay,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT source_id, day, avoidable_io_ops, total_io_ops, avoidable_gco2_low
                              FROM detection_days
                              WHERE source_id IN (SELECT value FROM json_each($source_ids))
                                AND day BETWEEN $from_day AND $to_day;
                              """;
        command.Parameters.AddWithValue(SourceIdsParameter, JsonArray(sourceIds));
        command.Parameters.AddWithValue("$from_day", fromDay);
        command.Parameters.AddWithValue("$to_day", toDay);

        var totals = new List<DayTotals>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            totals.Add(new DayTotals(
                reader.GetString(0), reader.GetInt32(1),
                NullableInt64(reader, 2), NullableInt64(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        return totals;
    }

    /// <summary>
    ///     The C1 signal days after <paramref name="afterDay" />: sealed, no detection of the signature
    ///     at that source in any ledger, passes below the threshold, with the thresholds applied.
    /// </summary>
    public async Task<IReadOnlyList<SignalDay>> ReadSignalDaysAsync(
        IReadOnlyList<string> signatures,
        IReadOnlyList<string> sourceIds,
        int afterDay,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT dc.signature, dc.source_id, dc.day, dd.slow_query_threshold_ms,
                                     dd.slow_http_threshold_ms, dd.max_fanout, dd.chatty_threshold,
                                     dd.pool_saturation_threshold, dd.serialized_min_calls
                              FROM detection_counts AS dc
                              JOIN detection_days AS dd
                                ON dd.source_id = dc.source_id AND dd.ledger_id = dc.ledger_id AND dd.day = dc.day
                              WHERE dc.signature IN (SELECT value FROM json_each($signatures))
                                AND dc.source_id IN (SELECT value FROM json_each($source_ids))
                                AND dc.day > $after_day
                                AND dd.sealed = 1
                                AND dc.detections = 0
                                AND dc.below_threshold_passes > 0
                                AND NOT EXISTS (SELECT 1 FROM detection_counts AS d2
                                                WHERE d2.signature = dc.signature AND d2.source_id = dc.source_id
                                                  AND d2.day = dc.day AND d2.detections > 0);
                              """;
        command.Parameters.AddWithValue("$signatures", JsonArray(signatures));
        command.Parameters.AddWithValue(SourceIdsParameter, JsonArray(sourceIds));
        command.Parameters.AddWithValue("$after_day", afterDay);

        var days = new List<SignalDay>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            days.Add(new SignalDay(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                new DetectionThresholds(
                    NullableInt64(reader, 3), NullableInt64(reader, 4), NullableInt64(reader, 5),
                    NullableInt64(reader, 6), NullableInt64(reader, 7), NullableInt64(reader, 8))));
        return days;
    }

    /// <summary>
    ///     Whether an action done within the days kept no finding, done before the Hub froze them, for a
    ///     signature a scope source detected: a count of that period's fixes would read short.
    /// </summary>
    public async Task<bool> HasUnkeptDoneActionAsync(
        IReadOnlyList<string> sourceIds,
        int fromDay,
        int toDay,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT EXISTS (
                                SELECT 1 FROM finding_actions AS fa
                                WHERE fa.status = 'done' AND fa.frozen_finding_json IS NULL
                                  AND fa.done_at_ms BETWEEN $from_ms AND $to_ms
                                  AND EXISTS (SELECT 1 FROM detection_counts AS dc
                                              WHERE dc.signature = fa.signature AND dc.detections > 0
                                                AND dc.source_id IN (SELECT value FROM json_each($source_ids))));
                              """;
        command.Parameters.AddWithValue(SourceIdsParameter, JsonArray(sourceIds));
        command.Parameters.AddWithValue("$from_ms", fromDay * DayMs);
        command.Parameters.AddWithValue("$to_ms", (toDay + 1L) * DayMs - 1);
        return await command.ExecuteScalarAsync(cancellationToken) is 1L;
    }

    /// <summary>Every card signing key, oldest first, retired and revoked ones included.</summary>
    public async Task<IReadOnlyList<StoredCardKey>> ReadCardKeysAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT fingerprint, public_key, created_at_ms, retired_at_ms, revoked_on
                              FROM card_keys
                              ORDER BY created_at_ms, fingerprint;
                              """;
        var keys = new List<StoredCardKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            keys.Add(new StoredCardKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        return keys;
    }

    public Task InsertCardKeyAsync(StoredCardKey key, CancellationToken cancellationToken)
    {
        return WriteCardKeyAsync(
            """
            INSERT INTO card_keys(fingerprint, public_key, created_at_ms, retired_at_ms, revoked_on)
            VALUES ($fingerprint, $public_key, $at_ms, $retired_at_ms, $revoked_on);
            """,
            key.Fingerprint, key.CreatedAtMs,
            parameters =>
            {
                parameters.AddWithValue("$public_key", key.PublicKey);
                parameters.AddWithValue("$retired_at_ms", (object?)key.RetiredAtMs ?? DBNull.Value);
                parameters.AddWithValue("$revoked_on", (object?)key.RevokedOn ?? DBNull.Value);
            },
            cancellationToken);
    }

    /// <summary>Retires a key, leaving a key retired earlier as it was.</summary>
    public Task RetireCardKeyAsync(string fingerprint, long atMs, CancellationToken cancellationToken)
    {
        return WriteCardKeyAsync(
            "UPDATE card_keys SET retired_at_ms = $at_ms WHERE fingerprint = $fingerprint AND retired_at_ms IS NULL;",
            fingerprint, atMs, null, cancellationToken);
    }

    /// <summary>Revokes a key, current or retired, keeping a retired key's end date. False when unknown.</summary>
    public async Task<bool> RevokeCardKeyAsync(
        string fingerprint,
        string revokedOn,
        long atMs,
        CancellationToken cancellationToken)
    {
        return await WriteCardKeyAsync(
            """
            UPDATE card_keys SET revoked_on = $revoked_on, retired_at_ms = COALESCE(retired_at_ms, $at_ms)
            WHERE fingerprint = $fingerprint;
            """,
            fingerprint, atMs,
            parameters => parameters.AddWithValue("$revoked_on", revokedOn),
            cancellationToken) > 0;
    }

    private async Task<int> WriteCardKeyAsync(
        string sql,
        string fingerprint,
        long atMs,
        Action<SqliteParameterCollection>? bind,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$at_ms", atMs);
            bind?.Invoke(command.Parameters);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
