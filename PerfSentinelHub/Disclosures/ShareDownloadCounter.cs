using System.Collections.Concurrent;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Disclosures;

/// <summary>
///     Share-link downloads, counted in memory so an anonymous request never writes to
///     SQLite, and added to the links' rows once a minute and at shutdown. A restart loses
///     at most a minute of counts, which only inform.
/// </summary>
public sealed class ShareDownloadCounter(HubDatabase database)
{
    private ConcurrentDictionary<string, ShareDownloads> _pending = new(StringComparer.Ordinal);

    public void Add(string linkId, long nowMs, string? client)
    {
        _pending.AddOrUpdate(
            linkId,
            _ => new ShareDownloads(1, nowMs, client),
            (_, counted) => new ShareDownloads(counted.Count + 1, Math.Max(counted.LastUsedAtMs, nowMs),
                client ?? counted.LastClient));
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var flushed = Interlocked.Exchange(ref _pending,
            new ConcurrentDictionary<string, ShareDownloads>(StringComparer.Ordinal));
        await database.AddShareDownloadsAsync(flushed, cancellationToken);
    }
}

/// <summary>Flushes the share-link counts every minute, and once more when the Hub stops.</summary>
public sealed partial class ShareDownloadFlusher(
    ShareDownloadCounter counter,
    TimeProvider timeProvider,
    ILogger<ShareDownloadFlusher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushQuietlyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: StopAsync flushes what is left.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushQuietlyAsync(cancellationToken);
    }

    private async Task FlushQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await counter.FlushAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFlushFailed(logger, exception);
        }
    }

    [LoggerMessage(2402, LogLevel.Warning, "Share-link download counts could not be written; they are lost.")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception);
}
