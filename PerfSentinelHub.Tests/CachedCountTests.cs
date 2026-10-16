using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Tests;

public sealed class CachedCountTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch.AddDays(1));
    private readonly ListLogger<CachedCountTests> _logger = new();

    [Fact]
    public async Task A_count_inside_the_budget_is_served_fresh_then_kept_for_its_lifetime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = 0;
        var cache = new CachedCount<int>(_ => Task.FromResult(++calls), _clock, _logger);

        Assert.Equal(1, (await cache.GetAsync(cancellationToken))?.Value);
        _clock.Advance(CachedCount<int>.Lifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await cache.GetAsync(cancellationToken))?.Value);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, (await cache.GetAsync(cancellationToken))?.Value);
    }

    [Fact]
    public async Task A_count_past_the_budget_serves_the_last_value_and_lands_on_a_later_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slow = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new CachedCount<int>(_ => ++calls == 1 ? Task.FromResult(7) : slow.Task, _clock, _logger);
        var first = await cache.GetAsync(cancellationToken);
        _clock.Advance(CachedCount<int>.Lifetime);

        // The caller stops waiting at the budget and gets the previous count, with
        // the time it was taken, while the count goes on.
        var waiting = cache.GetAsync(cancellationToken);
        _clock.Advance(CachedCount<int>.Budget);
        var stale = await waiting;
        Assert.Equal(7, stale?.Value);
        Assert.Equal(first?.CountedAt, stale?.CountedAt);

        // A second caller during that count starts no other one.
        waiting = cache.GetAsync(cancellationToken);
        _clock.Advance(CachedCount<int>.Budget);
        Assert.Equal(7, (await waiting)?.Value);
        Assert.Equal(2, calls);

        slow.SetResult(9);
        Assert.Equal(9, (await cache.GetAsync(cancellationToken))?.Value);
    }

    [Fact]
    public async Task A_count_slower_than_its_lifetime_is_kept_for_a_lifetime_from_when_it_lands()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slow = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new CachedCount<int>(_ => ++calls == 1 ? slow.Task : Task.FromResult(9), _clock, _logger);

        var waiting = cache.GetAsync(cancellationToken);
        _clock.Advance(CachedCount<int>.Budget);
        Assert.Null(await waiting);
        _clock.Advance(CachedCount<int>.Lifetime);
        slow.SetResult(7);
        Assert.Equal(7, (await cache.GetAsync(cancellationToken))?.Value);

        // Landed twenty seconds after it started: still fresh, no second count.
        Assert.Equal(7, (await cache.GetAsync(cancellationToken))?.Value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_slow_count_waits_nine_times_its_duration_before_the_next()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var slow = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new CachedCount<int>(_ => ++calls == 1 ? slow.Task : Task.FromResult(9), _clock, _logger);

        // A count that takes 70 seconds, the time a scrape interval comes back in.
        var waiting = cache.GetAsync(cancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(70));
        Assert.Null(await waiting);
        slow.SetResult(7);
        Assert.Equal(7, (await cache.GetAsync(cancellationToken))?.Value);

        // The base is not kept counting: at most a tenth of the time goes to it.
        _clock.Advance(TimeSpan.FromSeconds(9 * 70) - TimeSpan.FromSeconds(1));
        Assert.Equal(7, (await cache.GetAsync(cancellationToken))?.Value);
        Assert.Equal(1, calls);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(9, (await cache.GetAsync(cancellationToken))?.Value);
    }

    [Fact]
    public async Task Nothing_counted_yet_and_past_the_budget_serves_nothing()
    {
        var never = new TaskCompletionSource<int>();
        var cache = new CachedCount<int>(_ => never.Task, _clock, _logger);

        var waiting = cache.GetAsync(TestContext.Current.CancellationToken);
        _clock.Advance(CachedCount<int>.Budget);
        Assert.Null(await waiting);
    }

    [Fact]
    public async Task A_failed_count_is_logged_and_keeps_the_last_value()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = 0;
        var cache = new CachedCount<int>(
            _ => ++calls == 1 ? Task.FromResult(7) : Task.FromException<int>(new InvalidOperationException("full")),
            _clock,
            _logger);
        await cache.GetAsync(cancellationToken);
        _clock.Advance(CachedCount<int>.Lifetime);

        Assert.Equal(7, (await cache.GetAsync(cancellationToken))?.Value);
        Assert.Contains(_logger.Messages, message => message.Contains("count failed", StringComparison.Ordinal));

        // A failure waits a lifetime before the next try, like a success.
        Assert.Equal(7, (await cache.GetAsync(cancellationToken))?.Value);
        Assert.Equal(2, calls);
        Assert.Single(_logger.Messages);
    }
}
