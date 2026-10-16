namespace PerfSentinelHub.Api;

/// <summary>A count and the moment it was taken, on the Hub's clock.</summary>
public sealed record Counted<T>(T Value, DateTimeOffset CountedAt);

/// <summary>
///     A count served from memory for <see cref="Lifetime" />, then taken again, one at
///     a time. A caller waits for a new count at most <see cref="Budget" />: past it, it
///     gets the previous one and the count goes on, to land for a later caller. A count
///     that grows with the data can then never hold a request, a Prometheus scrape
///     included, past its timeout.
/// </summary>
public sealed partial class CachedCount<T>(
    Func<CancellationToken, Task<T>> count,
    TimeProvider timeProvider,
    ILogger logger)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    // Half of Prometheus' default 10 s scrape_timeout.
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private Task<Counted<T>?>? _counting;
    private Counted<T>? _last;
    private long _lastTimestamp;

    public async Task<Counted<T>?> GetAsync(CancellationToken cancellationToken)
    {
        Counted<T>? last;
        Task<Counted<T>?> counting;
        lock (_gate)
        {
            last = _last;
            if (last is not null && timeProvider.GetElapsedTime(_lastTimestamp) < Lifetime)
                return last;
            counting = _counting ??= CountAsync();
        }

        var waited = Task.Delay(Budget, timeProvider, cancellationToken);
        if (await Task.WhenAny(counting, waited) == counting)
            return await counting ?? last;
        cancellationToken.ThrowIfCancellationRequested();
        return last;
    }

    private async Task<Counted<T>?> CountAsync()
    {
        // Off the caller's lock, and on no caller's token: a caller that stops
        // waiting leaves the count running.
        await Task.Yield();
        var timestamp = timeProvider.GetTimestamp();
        var at = timeProvider.GetUtcNow();
        try
        {
            var counted = new Counted<T>(await count(CancellationToken.None), at);
            lock (_gate)
            {
                _last = counted;
                _lastTimestamp = timestamp;
            }

            return counted;
        }
        catch (Exception exception)
        {
            LogCountFailed(logger, exception);
            return null;
        }
        finally
        {
            lock (_gate)
            {
                _counting = null;
            }
        }
    }

    [LoggerMessage(2301, LogLevel.Warning, "A cached count failed; the previous one is still served.")]
    private static partial void LogCountFailed(ILogger logger, Exception exception);
}
