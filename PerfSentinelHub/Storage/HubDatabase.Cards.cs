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
}
