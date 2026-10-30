using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Maintenance;

public sealed partial class RetentionWorker(
    HubDatabase database,
    IOptions<HubOptions> options,
    TimeProvider timeProvider,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Once per start, after the Hub answers: see HubDatabase.CatchUpAttributesAsync.
        try
        {
            if (await database.CatchUpAttributesAsync(stoppingToken))
                LogAttributesCaughtUp(logger);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            LogCatchUpFailure(logger, exception);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            var cutoff = (now - options.Value.Retention).ToUnixTimeMilliseconds();
            // Finished runs age out on their own clock. Reusing Retention would
            // keep six months of run history for a table nothing reads back.
            var runCutoff = (now - options.Value.Analysis.RunRetention).ToUnixTimeMilliseconds();
            var detectionCutoff = (now - options.Value.DetectionRetention).ToUnixTimeMilliseconds();
            try
            {
                await database.PurgeAsync(
                    cutoff, detectionCutoff, runCutoff, now.ToUnixTimeMilliseconds(), stoppingToken);
                await database.PurgeRecapReportsAsync(
                    (now - options.Value.Recaps.Retention).ToUnixTimeMilliseconds(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A failed purge must skip one pass, not stop the host and lose the read endpoint.
                LogPurgeFailure(logger, exception);
            }

            await Task.Delay(TimeSpan.FromDays(1), timeProvider, stoppingToken);
        }
    }

    [LoggerMessage(1201, LogLevel.Error, "Retention purge failed; retrying at the next pass.")]
    private static partial void LogPurgeFailure(ILogger logger, Exception exception);

    [LoggerMessage(1202, LogLevel.Information, "Derived the attributes copies an older build stored without them.")]
    private static partial void LogAttributesCaughtUp(ILogger logger);

    [LoggerMessage(1203, LogLevel.Error, "Deriving the attributes older copies lack failed; the next start retries.")]
    private static partial void LogCatchUpFailure(ILogger logger, Exception exception);
}
