namespace PerfSentinelHub.Api;

/// <summary>A count and the moment it was taken, on the Hub's clock.</summary>
public sealed record Counted<T>(T Value, DateTimeOffset CountedAt);

/// <summary>
///     A count served from memory for <see cref="Lifetime" /> after it lands, then
///     taken again, one at a time. A failed count waits as long before the next try.
///     A caller waits for a new count at most <see cref="Budget" />: past it, it gets
///     the previous one and the count goes on, to land for a later caller. A count
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

    // When the last count ended, landed or failed. Measured from the end rather than
    // the start, so a count slower than the lifetime does not start the next at once.
    private long? _settledAt;

    public async Task<Counted<T>?> GetAsync(CancellationToken cancellationToken)
    {
        Counted<T>? last;
        Task<Counted<T>?> counting;
        lock (_gate)
        {
            last = _last;
            if (_settledAt is { } settledAt && timeProvider.GetElapsedTime(settledAt) < Lifetime)
                return last;
            counting = _counting ??= CountAsync();
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waited = Task.Delay(Budget, timeProvider, budget.Token);
        var first = await Task.WhenAny(counting, waited);
        await budget.CancelAsync();
        if (first == counting)
            return await counting ?? last;
        cancellationToken.ThrowIfCancellationRequested();
        return last;
    }

    private async Task<Counted<T>?> CountAsync()
    {
        // Off the caller's lock, and on no caller's token: a caller that stops
        // waiting leaves the count running.
        await Task.Yield();
        var at = timeProvider.GetUtcNow();
        try
        {
            var counted = new Counted<T>(await count(CancellationToken.None), at);
            lock (_gate)
            {
                _last = counted;
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
                _settledAt = timeProvider.GetTimestamp();
                _counting = null;
            }
        }
    }

    [LoggerMessage(2301, LogLevel.Warning, "A cached count failed; the previous one is still served.")]
    private static partial void LogCountFailed(ILogger logger, Exception exception);
}
