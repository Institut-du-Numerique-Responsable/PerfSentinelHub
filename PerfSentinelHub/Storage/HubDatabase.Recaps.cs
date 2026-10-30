using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Storage;

// The three reads a review recap adds to the findings read: how well the ledgers cover a
// period, the carbon inputs behind a finding's extra I/O, and the days a fixed finding was
// or was not detected. Every list is bound as one JSON array.
public sealed partial class HubDatabase
{
    /// <summary>
    ///     One entry per source holding a live ledger: the days of those ledgers in the range. A
    ///     ledger last read before the range starts is retired and not judged, so a dead
    ///     source does not mark every period incomplete until the retention purges it.
    /// </summary>
    public async Task<IReadOnlyList<SourceCoverage>> ReadRecapCoverageAsync(
        IReadOnlyList<string> sourceIds,
        int fromDay,
        int toDay,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT dl.source_id, dl.ledger_id, dl.started_ms, dl.last_read_ms, dl.polled
                              FROM detection_ledgers AS dl
                              WHERE dl.source_id IN (SELECT value FROM json_each($source_ids))
                                AND dl.last_read_ms >= $from_ms;
                              SELECT dd.source_id, dd.ledger_id, dd.day, dd.sealed, dd.gap, dd.overflow_detections,
                                     dd.shed_traces, dd.ingest_rejected, dd.sampling_rate
                              FROM detection_days AS dd
                              JOIN detection_ledgers AS dl
                                ON dl.source_id = dd.source_id AND dl.ledger_id = dd.ledger_id
                              WHERE dd.source_id IN (SELECT value FROM json_each($source_ids))
                                AND dl.last_read_ms >= $from_ms
                                AND dd.day BETWEEN $from_day AND $to_day;
                              """;
        command.Parameters.AddWithValue("$source_ids", JsonArray(sourceIds));
        command.Parameters.AddWithValue("$from_ms", fromDay * DayMs);
        command.Parameters.AddWithValue("$from_day", fromDay);
        command.Parameters.AddWithValue("$to_day", toDay);

        var ledgers = new Dictionary<string, List<LedgerLife>>(StringComparer.Ordinal);
        var days = new Dictionary<string, List<DayCoverage>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            Entry(ledgers, reader.GetString(0)).Add(
                new LedgerLife(reader.GetInt64(2), reader.GetInt64(3), reader.GetBoolean(4)));
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            Entry(days, reader.GetString(0)).Add(new DayCoverage(
                reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3), reader.GetBoolean(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetDouble(8)));

        return
        [
            .. ledgers.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new SourceCoverage(entry.Key, days.GetValueOrDefault(entry.Key) ?? [], entry.Value))
        ];
    }

    /// <summary>
    ///     The extra I/O each signature counted per source, ledger and day, with that day's
    ///     carbon inputs. A count of unknown I/O (green off) is left out.
    /// </summary>
    public async Task<IReadOnlyList<(string Signature, CarbonDay Day)>> ReadCarbonDaysAsync(
        IReadOnlyList<string> signatures,
        IReadOnlyList<string> sourceIds,
        int fromDay,
        int toDay,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT dc.signature, dc.source_id, dc.ledger_id, dc.day, dc.extra_io_ops,
                                     dd.avoidable_gco2, dd.avoidable_io_ops
                              FROM detection_counts AS dc
                              LEFT JOIN detection_days AS dd
                                ON dd.source_id = dc.source_id AND dd.ledger_id = dc.ledger_id AND dd.day = dc.day
                              WHERE dc.signature IN (SELECT value FROM json_each($signatures))
                                AND dc.source_id IN (SELECT value FROM json_each($source_ids))
                                AND dc.day BETWEEN $from_day AND $to_day
                                AND dc.detections > 0
                                AND dc.extra_io_ops IS NOT NULL;
                              """;
        command.Parameters.AddWithValue("$signatures", JsonArray(signatures));
        command.Parameters.AddWithValue("$source_ids", JsonArray(sourceIds));
        command.Parameters.AddWithValue("$from_day", fromDay);
        command.Parameters.AddWithValue("$to_day", toDay);

        var rows = new List<(string, CarbonDay)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.GetString(0), new CarbonDay(
                reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetInt64(6))));
        return rows;
    }

    /// <summary>
    ///     The days after <paramref name="afterDay" /> a signature was detected or observed at a
    ///     source, and the days a reachable source sealed. A source whose last poll failed
    ///     proves nothing, so it contributes no sealed day.
    /// </summary>
    public async Task<(IReadOnlyList<(string Signature, string SourceId, int Day)> Detected,
            IReadOnlyList<(string SourceId, int Day)> SealedReachable)>
        ReadActionEvidenceAsync(
            IReadOnlyList<string> signatures,
            IReadOnlyList<string> sourceIds,
            int afterDay,
            CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT signature, source_id, day FROM detection_counts
                              WHERE signature IN (SELECT value FROM json_each($signatures))
                                AND source_id IN (SELECT value FROM json_each($source_ids))
                                AND detections > 0 AND day > $after_day
                              UNION
                              SELECT signature, source_id, day FROM finding_observations
                              WHERE signature IN (SELECT value FROM json_each($signatures))
                                AND source_id IN (SELECT value FROM json_each($source_ids))
                                AND day > $after_day;
                              SELECT DISTINCT dd.source_id, dd.day
                              FROM detection_days AS dd
                              LEFT JOIN source_state AS ss ON ss.source_id = dd.source_id
                              WHERE dd.sealed = 1 AND dd.day > $after_day
                                AND dd.source_id IN (SELECT value FROM json_each($source_ids))
                                AND ss.unreachable_since_ms IS NULL;
                              """;
        command.Parameters.AddWithValue("$signatures", JsonArray(signatures));
        command.Parameters.AddWithValue("$source_ids", JsonArray(sourceIds));
        command.Parameters.AddWithValue("$after_day", afterDay);

        var detected = new List<(string, string, int)>();
        var sealedReachable = new List<(string, int)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            detected.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            sealedReachable.Add((reader.GetString(0), reader.GetInt32(1)));
        return (detected, sealedReachable);
    }

    private static List<T> Entry<T>(Dictionary<string, List<T>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        return list;
    }
}
