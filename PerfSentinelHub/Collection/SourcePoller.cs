using System.Text.Json;
using Microsoft.Data.Sqlite;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Collection;

public sealed record PollResult(
    int ImportedCount,
    int RejectedCount,
    string ProducerVersion,
    bool IsPossiblyTruncated,
    // Null when the daemon publishes no incidents, or refused or failed the read.
    int? IncidentCount,
    // Null when RingWatch cannot judge this poll: see Observe.
    long? MissedFindings = null);

public sealed class SourcePollException(string errorCode, Exception innerException)
    : Exception($"Source poll failed: {errorCode}", innerException)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed partial class SourcePoller(
    DaemonClient client,
    HubDatabase database,
    IncidentReader incidents,
    AckReader acks,
    RingWatch rings,
    TimeProvider timeProvider,
    ILogger<SourcePoller> logger)
{
    public async Task<PollResult> PollAsync(
        SourceOptions source,
        CancellationToken cancellationToken)
    {
        var observedAtMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await database.MarkSourceAttemptAsync(source.Id, observedAtMs, cancellationToken);

        try
        {
            var status = await client.FetchStatusAsync(source, cancellationToken);
            var missedCandidate = rings.Measure(
                source.Id,
                status,
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            var payload = await client.FetchFindingsAsync(source, cancellationToken);
            var batch = FindingParser.Parse(payload);
            await database.UpsertBatchAsync(
                new SourceSnapshot(source.Id, source.Name, source.Environment, status.Version),
                batch,
                observedAtMs,
                cancellationToken);
            if (batch.RejectedCount > 0)
                LogRejectedFindings(
                    logger,
                    source.Id,
                    batch.RejectedCount,
                    batch.RejectedCount + batch.Findings.Count);
            var isPossiblyTruncated = batch.Findings.Count + batch.RejectedCount == DaemonClient.FindingsLimit;
            if (isPossiblyTruncated)
                LogPossiblyTruncated(logger, source.Id);
            // After the findings: a config read only for a candidate, and never
            // between the status and the findings it measured.
            var missed = await RecordMissedAsync(source, missedCandidate, cancellationToken);
            // Its own failures, filed in incident_reads and never in
            // source_state: see IncidentReader.
            var incidentCount = await incidents.ReadAsync(source, observedAtMs, cancellationToken);
            // The same, in ack_reads. The version decides whether the daemon
            // is asked at all: see AckReader.
            await acks.ReadAsync(source, status.Version, observedAtMs, cancellationToken);
            return new PollResult(
                batch.Findings.Count,
                batch.RejectedCount,
                status.Version,
                isPossiblyTruncated,
                incidentCount,
                missed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var errorCode = ErrorCode(exception);
            await database.MarkSourceFailureAsync(source.Id, observedAtMs, errorCode, cancellationToken);
            LogPollFailure(logger, source.Id, errorCode);
            throw new SourcePollException(errorCode, exception);
        }
    }

    private async Task<long?> RecordMissedAsync(
        SourceOptions source,
        long? candidate,
        CancellationToken cancellationToken)
    {
        if (candidate is not > 0)
            return candidate;
        // A daemon that pushes delivered them to the Hub. An unread config counts
        // as pushing: a missed count the Hub cannot back would send the operator
        // after a setting that may be fine.
        if (await ExportsToHubAsync(source, cancellationToken) is not false)
            return 0;
        rings.Record(source.Id, candidate.Value);
        LogMissedFindings(logger, source.Id, candidate.Value);
        return candidate;
    }

    private async Task<bool?> ExportsToHubAsync(SourceOptions source, CancellationToken cancellationToken)
    {
        try
        {
            if (await client.FetchConfigAsync(source, cancellationToken) is not { } body)
                return null;
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("hub_export_enabled", out var enabled) &&
                   enabled.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? enabled.GetBoolean()
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
                                              or DaemonTimeoutException or ResponseTooLargeException)
        {
            return null;
        }
    }

    // Shared with the daemon view: one classification for both readers.
    public static string ErrorCode(Exception exception)
    {
        return exception switch
        {
            DaemonTimeoutException => "timeout",
            ResponseTooLargeException => "response_too_large",
            InvalidStatusException => "invalid_status",
            InvalidDataException => "invalid_findings",
            // SQLITE_FULL: the Hub's own volume, not the daemon, which answered.
            SqliteException { SqliteErrorCode: 13 } => "database_full",
            SqliteException => "storage_error",
            HttpRequestException httpException => httpException.StatusCode is null ? "network_error" : "http_error",
            _ => "network_error"
        };
    }

    [LoggerMessage(
        1101,
        LogLevel.Warning,
        "Source {SourceId} rejected {RejectedCount} of {ReceivedCount} findings.")]
    private static partial void LogRejectedFindings(
        ILogger logger,
        string sourceId,
        int rejectedCount,
        int receivedCount);

    [LoggerMessage(
        1102,
        LogLevel.Warning,
        "Source {SourceId} poll returned the daemon cap and is possibly truncated; Hub push export is required for complete high-volume coverage.")]
    private static partial void LogPossiblyTruncated(ILogger logger, string sourceId);

    [LoggerMessage(
        1104,
        LogLevel.Warning,
        "Source {SourceId}: {Missed} findings left the daemon's ring between two polls before the Hub read them; "
        + "raise [daemon] max_retained_findings, shorten Hub:PollInterval, or turn on [daemon.hub_export].")]
    private static partial void LogMissedFindings(ILogger logger, string sourceId, long missed);

    [LoggerMessage(1103, LogLevel.Warning, "Source {SourceId} poll failed: {ErrorCode}")]
    private static partial void LogPollFailure(ILogger logger, string sourceId, string errorCode);
}
