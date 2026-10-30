using Microsoft.Data.Sqlite;
using PerfSentinelHub.Access;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Storage;

public sealed partial class HubDatabase
{
    private const string LedgerIdParameter = "$ledger_id";

    // Every merge is monotonic over absolute values, so a poll, a push, a replay, an
    // overlap or a page out of order all converge on the same row. SQLite's scalar MAX
    // returns NULL when either side is NULL: an I/O count once unknown (green off) stays
    // unknown. started_ms is the ledger's identity and never changes.
    private const string DetectionLedgerUpsert = """
                                                 INSERT INTO detection_ledgers(
                                                   source_id, ledger_id, started_ms, first_read_ms, last_read_ms,
                                                   polled, pushed)
                                                 VALUES ($source_id, $ledger_id, $started_ms, $observed_at,
                                                         $observed_at, $polled, $pushed)
                                                 ON CONFLICT(source_id, ledger_id) DO UPDATE SET
                                                   first_read_ms = MIN(detection_ledgers.first_read_ms, excluded.first_read_ms),
                                                   last_read_ms = MAX(detection_ledgers.last_read_ms, excluded.last_read_ms),
                                                   polled = MAX(detection_ledgers.polled, excluded.polled),
                                                   pushed = MAX(detection_ledgers.pushed, excluded.pushed);
                                                 """;

    // A day keeps the lowest sampling rate it was ever read at, and a sealed day stays sealed.
    // The four carbon columns merge by a null-tolerant maximum: NULL there means "not yet
    // accumulated", unlike overflow_extra_io_ops where NULL means green off and stays sticky.
    private const string DetectionDayUpsert = """
                                              INSERT INTO detection_days(
                                                source_id, ledger_id, day, sealed, gap, overflow_detections,
                                                overflow_extra_io_ops, shed_traces, ingest_rejected, sampling_rate,
                                                last_read_ms, avoidable_gco2, avoidable_io_ops, avoidable_gco2_low,
                                                total_io_ops)
                                              VALUES ($source_id, $ledger_id, $day, $sealed, $gap, $overflow_detections,
                                                      $overflow_extra_io_ops, $shed_traces, $ingest_rejected,
                                                      $sampling_rate, $observed_at, $avoidable_gco2,
                                                      $avoidable_io_ops, $avoidable_gco2_low, $total_io_ops)
                                              ON CONFLICT(source_id, ledger_id, day) DO UPDATE SET
                                                sealed = MAX(detection_days.sealed, excluded.sealed),
                                                gap = MAX(detection_days.gap, excluded.gap),
                                                overflow_detections = MAX(detection_days.overflow_detections,
                                                                          excluded.overflow_detections),
                                                overflow_extra_io_ops = MAX(detection_days.overflow_extra_io_ops,
                                                                            excluded.overflow_extra_io_ops),
                                                shed_traces = MAX(detection_days.shed_traces, excluded.shed_traces),
                                                ingest_rejected = MAX(detection_days.ingest_rejected,
                                                                      excluded.ingest_rejected),
                                                sampling_rate = MIN(detection_days.sampling_rate, excluded.sampling_rate),
                                                last_read_ms = MAX(detection_days.last_read_ms, excluded.last_read_ms),
                                                avoidable_gco2 = CASE WHEN detection_days.avoidable_gco2 IS NULL THEN excluded.avoidable_gco2
                                                                 WHEN excluded.avoidable_gco2 IS NULL THEN detection_days.avoidable_gco2
                                                                 ELSE MAX(detection_days.avoidable_gco2, excluded.avoidable_gco2) END,
                                                avoidable_io_ops = CASE WHEN detection_days.avoidable_io_ops IS NULL THEN excluded.avoidable_io_ops
                                                                   WHEN excluded.avoidable_io_ops IS NULL THEN detection_days.avoidable_io_ops
                                                                   ELSE MAX(detection_days.avoidable_io_ops, excluded.avoidable_io_ops) END,
                                                avoidable_gco2_low = CASE WHEN detection_days.avoidable_gco2_low IS NULL THEN excluded.avoidable_gco2_low
                                                                     WHEN excluded.avoidable_gco2_low IS NULL THEN detection_days.avoidable_gco2_low
                                                                     ELSE MAX(detection_days.avoidable_gco2_low, excluded.avoidable_gco2_low) END,
                                                total_io_ops = CASE WHEN detection_days.total_io_ops IS NULL THEN excluded.total_io_ops
                                                               WHEN excluded.total_io_ops IS NULL THEN detection_days.total_io_ops
                                                               ELSE MAX(detection_days.total_io_ops, excluded.total_io_ops) END;
                                              """;

    private const string DetectionCountUpsert = """
                                                INSERT INTO detection_counts(
                                                  signature, source_id, ledger_id, day, detections, extra_io_ops)
                                                VALUES ($signature, $source_id, $ledger_id, $day, $detections,
                                                        $extra_io_ops)
                                                ON CONFLICT(signature, source_id, ledger_id, day) DO UPDATE SET
                                                  detections = MAX(detection_counts.detections, excluded.detections),
                                                  extra_io_ops = MAX(detection_counts.extra_io_ops, excluded.extra_io_ops);
                                                """;

    // A windowed page's volume inputs in one round trip: the page's counts in the window,
    // then the days and the ledgers of every source in scope.
    // shortcut: days and ledgers are read for every source in scope, not only the page's,
    // key them by the page's sources if a fleet grows to hundreds of daemons.
    private static readonly string DetectionVolumeRead = $"""
                                                          SELECT dc.signature, dc.source_id, dc.day, dc.detections,
                                                                 dc.extra_io_ops, ss.unreachable_since_ms, dc.ledger_id
                                                          FROM detection_counts AS dc
                                                          LEFT JOIN source_state AS ss ON ss.source_id = dc.source_id
                                                          WHERE dc.signature IN (SELECT value FROM json_each($signatures))
                                                            AND dc.day BETWEEN $vol_from_day AND $vol_to_day
                                                            AND {Copies(false, "dc")};
                                                          SELECT dd.source_id, dd.day, dd.sealed, dd.gap,
                                                                 dd.overflow_detections, dd.shed_traces,
                                                                 dd.ingest_rejected, dd.sampling_rate, dd.ledger_id
                                                          FROM detection_days AS dd
                                                          WHERE dd.day BETWEEN $vol_from_day AND $vol_to_day
                                                            AND {Copies(false, "dd")};
                                                          SELECT dl.source_id, dl.started_ms, dl.last_read_ms, dl.polled
                                                          FROM detection_ledgers AS dl
                                                          WHERE {Copies(false, "dl")};
                                                          """;

    /// <summary>
    ///     Merges one ledger page in its own transaction, waiting for the write lock as long
    ///     as it takes. A polled page never seals a day (SealDetectionPassAsync does, once a
    ///     whole pass is read); a pushed one seals what the daemon sent sealed.
    /// </summary>
    public async Task MergeDetectionPageAsync(
        string sourceId,
        DetectionLedgerPage page,
        bool fromPush,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await WriteDetectionPageAsync(sourceId, page, fromPush, observedAtMs, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    ///     The push path: waits for the write lock as long as the other imports do, then
    ///     gives up so the daemon retries on a 503. Files the push in source_imports and
    ///     never touches source_state nor source_finding_imports.
    /// </summary>
    public async Task<bool> TryMergeDetectionPageAsync(
        string sourceId,
        DetectionLedgerPage page,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        if (!await _writeGate.WaitAsync(WriteGateWait, cancellationToken))
            return false;
        try
        {
            await WriteDetectionPageAsync(sourceId, page, true, observedAtMs, cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteDetectionPageAsync(
        string sourceId,
        DetectionLedgerPage page,
        bool fromPush,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(false);
        // One command per table, rebound per row: a page holds up to 5000 count rows.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = command.Parameters;

        command.CommandText = DetectionLedgerUpsert;
        BindLedger(parameters, sourceId, page.LedgerId, observedAtMs);
        parameters.AddWithValue("$started_ms", page.StartedMs);
        parameters.AddWithValue("$polled", fromPush ? 0 : 1);
        parameters.AddWithValue("$pushed", fromPush ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.CommandText = DetectionDayUpsert;
        foreach (var day in page.Days)
        {
            BindLedger(parameters, sourceId, page.LedgerId, observedAtMs);
            parameters.AddWithValue("$day", day.Day);
            parameters.AddWithValue("$sealed", fromPush && day.Sealed ? 1 : 0);
            parameters.AddWithValue("$gap", day.Gap ? 1 : 0);
            parameters.AddWithValue("$overflow_detections", day.OverflowDetections);
            parameters.AddWithValue("$overflow_extra_io_ops", (object?)day.OverflowExtraIoOps ?? DBNull.Value);
            parameters.AddWithValue("$shed_traces", day.ShedTraces);
            parameters.AddWithValue("$ingest_rejected", day.IngestRejected);
            parameters.AddWithValue("$sampling_rate", day.SamplingRate);
            parameters.AddWithValue("$avoidable_gco2", (object?)day.AvoidableGco2 ?? DBNull.Value);
            parameters.AddWithValue("$avoidable_io_ops", (object?)day.AvoidableIoOps ?? DBNull.Value);
            parameters.AddWithValue("$avoidable_gco2_low", (object?)day.AvoidableGco2Low ?? DBNull.Value);
            parameters.AddWithValue("$total_io_ops", (object?)day.TotalIoOps ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        command.CommandText = DetectionCountUpsert;
        foreach (var count in page.Counts)
        {
            BindLedger(parameters, sourceId, page.LedgerId, observedAtMs);
            parameters.AddWithValue("$day", count.Day);
            parameters.AddWithValue("$signature", count.Signature);
            parameters.AddWithValue("$detections", count.Detections);
            parameters.AddWithValue("$extra_io_ops", (object?)count.ExtraIoOps ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (fromPush)
            await RecordImportAsync(connection, transaction, sourceId, observedAtMs, cancellationToken,
                "source_imports");
        await transaction.CommitAsync(cancellationToken);
    }

    // Clears the previous row's values; SQLite ignores a bound name the statement does not use.
    private static void BindLedger(
        SqliteParameterCollection parameters, string sourceId, string ledgerId, long observedAtMs)
    {
        parameters.Clear();
        parameters.AddWithValue(SourceIdParameter, sourceId);
        parameters.AddWithValue(LedgerIdParameter, ledgerId);
        parameters.AddWithValue(ObservedAtParameter, observedAtMs);
    }

    /// <summary>
    ///     Closes a whole poll pass in one transaction: seals the ledger's days the pass read,
    ///     from the later of its since_day and its first page's oldest_day up to the
    ///     sealed_through that page carried, and moves the cursor to the day after, so the
    ///     next pass rereads whole any day sealed while this one ran. A day below the read
    ///     was pruned by the daemon, perhaps read partially, and stays open. A first page
    ///     with no sealed day leaves both as they are.
    /// </summary>
    public async Task SealDetectionPassAsync(
        string sourceId,
        string ledgerId,
        int sinceDay,
        int oldestDay,
        int? sealedThrough,
        CancellationToken cancellationToken)
    {
        if (sealedThrough is null)
            return;

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                                  UPDATE detection_days SET sealed = 1
                                  WHERE source_id = $source_id AND ledger_id = $ledger_id
                                    AND day BETWEEN MAX($since_day, $oldest_day) AND $sealed_through;
                                  UPDATE detection_ledgers SET read_from_day = $sealed_through + 1
                                  WHERE source_id = $source_id AND ledger_id = $ledger_id;
                                  """;
            command.Parameters.AddWithValue(SourceIdParameter, sourceId);
            command.Parameters.AddWithValue(LedgerIdParameter, ledgerId);
            command.Parameters.AddWithValue("$since_day", sinceDay);
            command.Parameters.AddWithValue("$oldest_day", oldestDay);
            command.Parameters.AddWithValue("$sealed_through", sealedThrough.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>The day the next poll pass of this ledger starts from, null for a ledger never sealed or never read.</summary>
    public async Task<int?> ReadDetectionCursorAsync(
        string sourceId,
        string ledgerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT read_from_day FROM detection_ledgers
                              WHERE source_id = $source_id AND ledger_id = $ledger_id;
                              """;
        command.Parameters.AddWithValue(SourceIdParameter, sourceId);
        command.Parameters.AddWithValue(LedgerIdParameter, ledgerId);
        return await command.ExecuteScalarAsync(cancellationToken) is long day ? (int)day : null;
    }

    // The days a windowed read counts: from its from day, never before the purge's cutoff
    // day, to its to day, never after the Hub's own. A window wholly ahead starts the day after.
    private (VolumeWindow Window, int Today) VolumeWindowOf(FindingQuery query)
    {
        var now = timeProvider.GetUtcNow();
        var today = now.ToUnixTimeMilliseconds() / DayMs;
        var cutoffDay = (now - _retention).ToUnixTimeMilliseconds() / DayMs;
        var from = Math.Min(Math.Max((query.FromMs ?? 0) / DayMs, cutoffDay), today + 1);
        var to = Math.Min((query.ToMs ?? long.MaxValue) / DayMs, today);
        return (new VolumeWindow((int)from, (int)to), (int)today);
    }

    /// <summary>
    ///     Folds the detection volume into a windowed page, per source and per finding. A
    ///     source that counted a finding without holding a copy joins its sources, named by the
    ///     configuration, and with access patterns only where the caller holds the whole source.
    ///     An unwindowed read or an empty page has nothing to fold.
    /// </summary>
    private async Task AttachVolumesAsync(
        SqliteConnection connection,
        List<StoredFinding> rows,
        FindingQuery query,
        VolumeWindow? window,
        int today,
        CancellationToken cancellationToken)
    {
        if (window is null || rows.Count == 0)
            return;

        await using var command = connection.CreateCommand();
        command.CommandText = DetectionVolumeRead;
        command.Parameters.AddWithValue("$signatures", JsonArray([.. rows.Select(row => row.Signature)]));
        command.Parameters.AddWithValue("$vol_from_day", window.FromDay);
        command.Parameters.AddWithValue("$vol_to_day", window.ToDay);
        command.Parameters.AddWithValue(
            "$source_ids", query.SourceIds is null ? DBNull.Value : JsonArray(query.SourceIds));

        var counts = new List<(string SourceId, long? UnreachableSinceMs, string LedgerId, DetectionCountRow Row)>();
        var days = new List<(string SourceId, DayCoverage Day)>();
        var ledgers = new List<(string SourceId, LedgerLife Ledger)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                counts.Add((reader.GetString(1), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetString(6),
                    new DetectionCountRow(reader.GetInt32(2), reader.GetString(0), reader.GetInt64(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4))));
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                days.Add((reader.GetString(0), new DayCoverage(reader.GetString(8), reader.GetInt32(1),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                    reader.GetDouble(7))));
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ledgers.Add((reader.GetString(0),
                    new LedgerLife(reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3))));
        }

        var daysBySource = days.ToLookup(item => item.SourceId, item => item.Day, StringComparer.Ordinal);
        var multiInstance = ledgers
            .GroupBy(item => item.SourceId, item => item.Ledger, StringComparer.Ordinal)
            .Where(group => DetectionVolumeFold.MultiInstance([.. group], window))
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var countsBySignature = counts.ToLookup(item => item.Row.Signature, StringComparer.Ordinal);
        foreach (var finding in rows)
        {
            var findingCounts = countsBySignature[finding.Signature].ToList();
            AddCountOnlySources(finding, findingCounts, query.Access);
            foreach (var source in finding.Sources)
                source.Volume = DetectionVolumeFold.ForSource(
                    [.. daysBySource[source.SourceId]],
                    [
                        .. findingCounts.Where(item => item.SourceId == source.SourceId)
                            .Select(item => (item.LedgerId, item.Row))
                    ],
                    window, today, multiInstance.Contains(source.SourceId));
            finding.Window = window;
            finding.Volume = DetectionVolumeFold.ForFinding([.. finding.Sources.Select(source => source.Volume!)]);
        }
    }

    // A source no longer configured has no name to serve, and is left out.
    private void AddCountOnlySources(
        StoredFinding finding,
        List<(string SourceId, long? UnreachableSinceMs, string LedgerId, DetectionCountRow Row)> counts,
        AccessScope? access)
    {
        foreach (var (sourceId, unreachableSinceMs, _, _) in counts.DistinctBy(item => item.SourceId))
        {
            if (finding.Sources.Exists(source => source.SourceId == sourceId) ||
                (access is not null && !access.HoldsAtSource(sourceId)) ||
                _sources.FirstOrDefault(source => source.Id == sourceId) is not { } configured)
                continue;
            finding.Sources.Add(new FindingSourceObservation(
                sourceId, configured.Name, configured.Environment, null, null, unreachableSinceMs));
        }
    }
}
