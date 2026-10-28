using System.Runtime.CompilerServices;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Collection;

/// <summary>
///     One daemon's detection ledger, read page by page and merged. A pass starts from the
///     ledger's cursor, or its oldest day the first time, and seals only once it read every
///     page: then the days up to the sealed_through its first page carried, so a day the
///     daemon sealed while the pass ran is read whole again by the next one.
/// </summary>
/// <remarks>
///     Its failures are its own, as for IncidentReader, and a storage failure too, unlike
///     there: the findings this poll read are already committed, and a ledger that cannot
///     be stored must not mark a daemon that answered as failed. Each outcome is counted in
///     DetectionWatch and nothing reaches MarkSourceFailureAsync.
/// </remarks>
public sealed partial class DetectionReader(
    DaemonClient client,
    HubDatabase database,
    DetectionWatch watch,
    ILogger<DetectionReader> logger)
{
    // 64 pages of 5000 rows, 320 000 signature-days per pass. A pass that reaches it
    // seals nothing, so the next one starts from the same cursor.
    public const int PagesCap = 64;

    /// <summary>Reads one pass of the ledger the daemon's status named, or does nothing without one.</summary>
    public async Task ReadAsync(
        SourceOptions source,
        DetectionLedgerStatus? ledger,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        if (ledger is null)
            return;

        // Counted as they merge, so a pass that stops halfway still reports the rows it stored.
        var rows = new StrongBox<long>();
        DetectionReadOutcome outcome;
        try
        {
            outcome = await ReadPassAsync(source, ledger, observedAtMs, rows, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DetectionsUnauthorizedException)
        {
            outcome = DetectionReadOutcome.Unauthorized;
            LogDetectionsUnauthorized(logger, source.Id);
        }
        catch (Exception exception)
        {
            outcome = DetectionReadOutcome.Failed;
            // The shared switch names the findings leg for a malformed body, and this leg
            // has its own parser, whose bounds also refuse an oversized page.
            var errorCode = exception is InvalidDataException or ImportBatchTooLargeException
                ? "invalid_detections"
                : SourcePoller.ErrorCode(exception);
            LogDetectionsFailed(logger, source.Id, errorCode);
        }

        watch.Record(source.Id, outcome, rows.Value);
    }

    private async Task<DetectionReadOutcome> ReadPassAsync(
        SourceOptions source,
        DetectionLedgerStatus ledger,
        long observedAtMs,
        StrongBox<long> rows,
        CancellationToken cancellationToken)
    {
        var sinceDay = await database.ReadDetectionCursorAsync(source.Id, ledger.LedgerId, cancellationToken) ??
                       ledger.OldestDay;
        int? sealedThrough = null;
        string? after = null;
        for (var pages = 0; pages < PagesCap; pages++)
        {
            // A daemon that turned its ledger off or rolled back since its status answered.
            if (await client.FetchDetectionsPageAsync(source, sinceDay, after, cancellationToken) is not { } payload)
                return DetectionReadOutcome.Aborted;

            var page = DetectionLedgerParser.ParsePage(payload, observedAtMs);
            // Checked against the status, not only the first page: the cursor read above
            // is that ledger's, and a restart can land between the status and any page.
            if (page.LedgerId != ledger.LedgerId)
            {
                LogLedgerChanged(logger, source.Id);
                return DetectionReadOutcome.Aborted;
            }

            if (pages == 0)
                sealedThrough = page.SealedThrough;
            await database.MergeDetectionPageAsync(source.Id, page, false, observedAtMs, cancellationToken);
            rows.Value += page.Counts.Count;
            if (page.NextAfter is null)
            {
                await database.SealDetectionPassAsync(source.Id, ledger.LedgerId, sealedThrough, cancellationToken);
                return DetectionReadOutcome.Ok;
            }

            after = page.NextAfter;
        }

        LogDetectionsCapped(logger, source.Id, PagesCap);
        return DetectionReadOutcome.Capped;
    }

    [LoggerMessage(
        1114,
        LogLevel.Warning,
        "Source {SourceId} refused the Hub's key on /api/detections; its findings were still collected.")]
    private static partial void LogDetectionsUnauthorized(ILogger logger, string sourceId);

    [LoggerMessage(
        1115,
        LogLevel.Warning,
        "Source {SourceId} detection ledger read failed: {ErrorCode}; its findings were still collected.")]
    private static partial void LogDetectionsFailed(ILogger logger, string sourceId, string errorCode);

    [LoggerMessage(
        1116,
        LogLevel.Information,
        "Source {SourceId} started a new detection ledger mid-pass; the pass stopped unsealed and the next one reads the new ledger.")]
    private static partial void LogLedgerChanged(ILogger logger, string sourceId);

    [LoggerMessage(
        1117,
        LogLevel.Warning,
        "Source {SourceId} detection ledger runs past {Cap} pages; the pass stopped unsealed and starts over next poll.")]
    private static partial void LogDetectionsCapped(ILogger logger, string sourceId, int cap);
}
