namespace PerfSentinelHub.Api;

/// <summary>A count and the moment it was taken, on the Hub's clock.</summary>
public sealed record Counted<T>(T Value, DateTimeOffset CountedAt);

/// <summary>
///     A count served from memory for <see cref="Lifetime" /> after it lands, or nine
///     times as long as it took when that is longer, then taken again, one at a time:
///     a slow count never keeps the database busy more than a tenth of the time, which
///     on a page cache smaller than the database means reads every import competes
///     with. A failed count waits as long before the next try.
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

    // When the last count ended, landed or failed, and how long to wait from there.
    // Measured from the end rather than the start, so a slow count does not start
    // the next at once.
    private long? _settledAt;
    private TimeSpan _rest = Lifetime;

    public async Task<Counted<T>?> GetAsync(CancellationToken cancellationToken)
    {
        Counted<T>? last;
        Task<Counted<T>?> counting;
        lock (_gate)
        {
            last = _last;
            if (_settledAt is { } settledAt && timeProvider.GetElapsedTime(settledAt) < _rest)
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
        // The clock is read before leaving the caller, then the count runs off its
        // lock and on no caller's token: a caller that stops waiting leaves it running.
        var at = timeProvider.GetUtcNow();
        var started = timeProvider.GetTimestamp();
        await Task.Yield();
        try
        {
            // A thread of its own: SQLite runs its Async methods synchronously, so on
            // the pool a long count would hold a pool thread the probes queue behind.
            var value = await Task.Factory.StartNew(
                () => count(CancellationToken.None),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            var counted = new Counted<T>(value, at);
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
                _rest = TimeSpan.FromTicks(Math.Max(Lifetime.Ticks, timeProvider.GetElapsedTime(started).Ticks * 9));
                _settledAt = timeProvider.GetTimestamp();
                _counting = null;
            }
        }
    }

    [LoggerMessage(2301, LogLevel.Warning, "A cached count failed; the previous one is still served.")]
    private static partial void LogCountFailed(ILogger logger, Exception exception);
}
