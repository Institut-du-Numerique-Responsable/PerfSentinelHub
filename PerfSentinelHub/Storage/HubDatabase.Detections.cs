using Microsoft.Data.Sqlite;

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
    private const string DetectionDayUpsert = """
                                              INSERT INTO detection_days(
                                                source_id, ledger_id, day, sealed, gap, overflow_detections,
                                                overflow_extra_io_ops, shed_traces, ingest_rejected, sampling_rate,
                                                last_read_ms)
                                              VALUES ($source_id, $ledger_id, $day, $sealed, $gap, $overflow_detections,
                                                      $overflow_extra_io_ops, $shed_traces, $ingest_rejected,
                                                      $sampling_rate, $observed_at)
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
                                                last_read_ms = MAX(detection_days.last_read_ms, excluded.last_read_ms);
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
                table: "source_imports");
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
    ///     Closes a whole poll pass in one transaction: seals the ledger's days up to the
    ///     sealed_through its first page carried and moves the cursor to the day after, so
    ///     the next pass rereads whole any day sealed while this one ran. A first page with
    ///     no sealed day leaves both as they are.
    /// </summary>
    public async Task SealDetectionPassAsync(
        string sourceId,
        string ledgerId,
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
                                  WHERE source_id = $source_id AND ledger_id = $ledger_id AND day <= $sealed_through;
                                  UPDATE detection_ledgers SET read_from_day = $sealed_through + 1
                                  WHERE source_id = $source_id AND ledger_id = $ledger_id;
                                  """;
            command.Parameters.AddWithValue(SourceIdParameter, sourceId);
            command.Parameters.AddWithValue(LedgerIdParameter, ledgerId);
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
}
