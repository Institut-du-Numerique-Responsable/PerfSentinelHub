using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Collection;

/// <summary>
///     How many signatures each daemon first saw in the last hour. A healthy fleet
///     settles on a stable set, so a source that keeps minting new ones points at
///     a producer putting something unique per trace into a signature, and the
///     Hub stores one row per signature it gets. Past
///     `Hub:NewSignaturesPerHourWarning` the source is flagged on /api/sources and
///     logged once, until it falls back under.
/// </summary>
public sealed partial class SignatureRate(
    HubDatabase database,
    IOptions<HubOptions> options,
    TimeProvider timeProvider,
    ILogger<SignatureRate> logger)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly HashSet<string> _flagged = new(StringComparer.Ordinal);

    // Replaced whole on each evaluation, read on request threads.
    private IReadOnlyDictionary<string, int> _counts = new Dictionary<string, int>();

    /// <summary>Null while the source is not judged: never evaluated, or still in its first hour.</summary>
    public int? NewSignaturesLastHour(string sourceId)
    {
        return Volatile.Read(ref _counts).TryGetValue(sourceId, out var count) ? count : null;
    }

    public bool IsAbnormal(string sourceId)
    {
        var threshold = options.Value.NewSignaturesPerHourWarning;
        return threshold > 0 && NewSignaturesLastHour(sourceId) > threshold;
    }

    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var sinceMs = timeProvider.GetUtcNow().Subtract(Window).ToUnixTimeMilliseconds();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var source in options.Value.Sources.Where(source => source.Kind == SourceKinds.Daemon))
            if (await database.CountNewSignaturesAsync(source.Id, sinceMs, cancellationToken) is { } count)
                counts[source.Id] = count;
        Volatile.Write(ref _counts, counts);

        // One evaluation at a time: the worker is its only caller.
        foreach (var source in options.Value.Sources)
        {
            if (!IsAbnormal(source.Id))
            {
                _flagged.Remove(source.Id);
                continue;
            }

            if (_flagged.Add(source.Id))
                LogAbnormalRate(logger, source.Id, counts[source.Id], options.Value.NewSignaturesPerHourWarning);
        }
    }

    [LoggerMessage(
        1112,
        LogLevel.Warning,
        "Source {SourceId} first saw {Count} signatures in the last hour, over the {Threshold} of "
        + "Hub:NewSignaturesPerHourWarning. A producer that puts something unique per trace into a "
        + "signature fills the Hub's volume one row per trace.")]
    private static partial void LogAbnormalRate(ILogger logger, string sourceId, int count, int threshold);
}

/// <summary>Runs <see cref="SignatureRate" /> at start and every five minutes.</summary>
public sealed partial class SignatureRateWorker(
    SignatureRate rate,
    TimeProvider timeProvider,
    ILogger<SignatureRateWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                await rate.EvaluateAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                // A full disk is the case this check exists for; it must not stop the Hub.
                LogEvaluationFailed(logger, exception);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(1113, LogLevel.Warning, "The signature rate check failed; it runs again in five minutes.")]
    private static partial void LogEvaluationFailed(ILogger logger, Exception exception);
}
