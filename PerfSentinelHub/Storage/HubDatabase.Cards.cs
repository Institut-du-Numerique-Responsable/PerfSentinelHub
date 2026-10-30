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
