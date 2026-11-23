using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Links;

/// <summary>
///     Assesses the findings frozen on each queued incident, off the import path: an import only
///     queues a key, so a large incident never holds the write gate of a findings push.
/// </summary>
public sealed partial class IncidentLinkWorker(
    IncidentLinkQueue queue,
    HubDatabase database,
    IOptions<HubOptions> options,
    TimeProvider timeProvider,
    ILogger<IncidentLinkWorker> logger) : BackgroundService
{
    // A failed assessment leaves an incident without current links; the catch-up runs again to retry it.
    public static readonly TimeSpan CatchUpInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CatchUpAsync(stoppingToken);
            await Task.WhenAll(ConsumeAsync(stoppingToken), RetryCatchUpAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CatchUpAsync(CancellationToken stoppingToken)
    {
        try
        {
            var missing = await database.IncidentsNeedingLinksAsync(
                IncidentLinkRules.RulesVersion, [.. options.Value.Sources.Select(s => s.Id)], stoppingToken);
            foreach (var key in missing)
                queue.Enqueue(key);
            if (missing.Count > 0)
                LogCatchUp(logger, missing.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Not fatal: imports keep queueing, and the next pass retries the catch-up.
            LogCatchUpFailed(logger, exception);
        }
    }

    private async Task RetryCatchUpAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CatchUpInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await CatchUpAsync(stoppingToken);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var key in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(key, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogIncidentFailed(logger, key.Id, exception);
            }
        }
    }

    /// <summary>Writes the links of one incident. False when the incident or its source is gone.</summary>
    public async Task<bool> ProcessAsync(IncidentKey key, CancellationToken cancellationToken)
    {
        var sources = options.Value.Sources;
        var environment = sources.FirstOrDefault(s => string.Equals(s.Id, key.SourceId, StringComparison.Ordinal))
            ?.Environment;
        if (environment is null)
            return false;
        var sourceIds = sources.Where(s => string.Equals(s.Environment, environment, StringComparison.Ordinal))
            .Select(s => s.Id).ToArray();
        if (await database.LoadLinkContextAsync(key, sourceIds, cancellationToken) is not { } context)
            return false;

        var windowMs = context.WindowToMs - context.WindowFromMs;
        var links = new List<StoredLink>(context.Signatures.Count);
        foreach (var signature in context.Signatures)
        {
            var result = IncidentLinkRules.Assess(new LinkInput(
                context.Kind,
                signature.FindingType,
                context.Saturation,
                windowMs,
                signature.SeenCount,
                signature.FirstSeenMs >= context.AtMs && !context.SeenInBaselineDays.Contains(signature.Signature),
                context.Baselines.GetValueOrDefault(signature.Signature, new Baseline(0, 0, 0)),
                context.Recurrences.GetValueOrDefault(signature.Signature, new Recurrence(0, 0)),
                context.SeenInBaselineDays.Contains(signature.Signature)));
            links.Add(new StoredLink(signature.Signature, result.Level, result.Reasons, IncidentLinkRules.RulesVersion));
        }

        await database.ReplaceLinksAsync(key, links, context.FindingsJson, cancellationToken);
        return true;
    }

    [LoggerMessage(2601, LogLevel.Information, "Queued {Count} incidents whose finding links are missing or outdated.")]
    private static partial void LogCatchUp(ILogger logger, int count);

    [LoggerMessage(2602, LogLevel.Error, "Assessing the finding links of incident {IncidentId} failed.")]
    private static partial void LogIncidentFailed(ILogger logger, string incidentId, Exception exception);

    [LoggerMessage(2603, LogLevel.Error, "The catch-up of finding links failed; incidents imported from now on are still assessed.")]
    private static partial void LogCatchUpFailed(ILogger logger, Exception exception);
}
