using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Recaps;

/// <summary>Runs the scheduled recaps every 15 seconds while a subscription is declared.</summary>
public sealed partial class RecapLoop(
    RecapGenerator generator,
    IOptions<HubOptions> options,
    TimeProvider timeProvider,
    ILogger<RecapLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Recaps.Subscriptions.Count == 0)
            return;
        using var timer = new PeriodicTimer(Tick, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                try
                {
                    await generator.RunOnceAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogPassFailed(logger, exception);
                }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: the next start generates what is still missing.
        }
    }

    [LoggerMessage(2505, LogLevel.Warning, "A recap pass failed; the next one retries.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
