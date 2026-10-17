namespace PerfSentinelHub.Access;

/// <summary>
///     The local sign-in throttle, in memory and cleared by a restart. A backoff per client
///     address and name, never a hard lock: an attacker slows only their own address, so the
///     break-glass account stays usable from the operator's. And a budget per address, across
///     every name: without it each new name would buy five free hashes, and one address could
///     keep the single hashing slot busy. An attempt is reserved before it is verified, so
///     concurrent ones cannot all pass the check; a refused one is not counted.
/// </summary>
public sealed class LocalSignInThrottle(TimeProvider timeProvider)
{
    public const int MaxKeys = 10_000;
    private const int FreeFailures = 5;
    private const int BudgetPerWindow = 5;
    private const long WindowMs = 60_000;
    private const long FirstDelayMs = 30_000;
    private const long MaxDelayMs = 15 * 60_000;
    private const long ForgetMs = 24 * 3_600_000;

    private readonly RecentTable<Backoff> _backoffs = new();
    private readonly RecentTable<Budget> _budgets = new();
    private readonly Lock _lock = new();

    /// <summary>
    ///     Null when the attempt is reserved: counted against the address's budget and as a
    ///     failure of the key before it is verified. Otherwise how long to wait.
    /// </summary>
    public TimeSpan? TryReserve(string address, string key)
    {
        lock (_lock)
        {
            var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var backoff = _backoffs.Use(key);
            if (backoff is not null && now - backoff.LastMs >= ForgetMs)
            {
                _backoffs.Remove(key);
                backoff = null;
            }

            if (backoff is { Failures: >= FreeFailures } &&
                backoff.LastMs + Delay(backoff.Failures) - now is > 0 and var wait)
                return TimeSpan.FromMilliseconds(wait);

            var budget = _budgets.Use(address);
            if (budget is not null && now - budget.WindowStartMs >= WindowMs)
                (budget.WindowStartMs, budget.Spent) = (now, 0);
            if (budget is { Spent: >= BudgetPerWindow })
                return TimeSpan.FromMilliseconds(budget.WindowStartMs + WindowMs - now);

            budget ??= _budgets.Add(address, new Budget { WindowStartMs = now });
            budget.Spent++;
            backoff ??= _backoffs.Add(key, new Backoff());
            (backoff.PreviousLastMs, backoff.LastMs) = (backoff.LastMs, now);
            backoff.Failures++;
            return null;
        }
    }

    // The attempt never ran: the hashing slot stayed busy.
    public void GiveBack(string address, string key)
    {
        lock (_lock)
        {
            if (_budgets.Get(address) is { Spent: > 0 } budget)
                budget.Spent--;
            if (_backoffs.Get(key) is not { } backoff) return;
            backoff.Failures--;
            backoff.LastMs = backoff.PreviousLastMs;
            if (backoff.Failures <= 0) _backoffs.Remove(key);
        }
    }

    public void Succeeded(string key)
    {
        lock (_lock)
        {
            _backoffs.Remove(key);
        }
    }

    // Consecutive failures counted for the key, the current attempt's included.
    public int Failures(string key)
    {
        lock (_lock)
        {
            return _backoffs.Get(key)?.Failures ?? 0;
        }
    }

    // After failure n >= 5 the next attempt waits 30 s × 2^(n−5), at most 15 minutes.
    private static long Delay(int failures)
    {
        return Math.Min(FirstDelayMs << Math.Min(failures - FreeFailures, 5), MaxDelayMs);
    }

    // Least recently used first, so at the cap the key evicted is the idlest one, in O(1).
    private sealed class RecentTable<T> where T : class
    {
        private readonly Dictionary<string, LinkedListNode<(string Key, T Value)>> _nodes = new(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, T Value)> _order = new();

        public T? Get(string key)
        {
            return _nodes.TryGetValue(key, out var node) ? node.Value.Value : null;
        }

        public T? Use(string key)
        {
            if (!_nodes.TryGetValue(key, out var node)) return null;
            _order.Remove(node);
            _order.AddLast(node);
            return node.Value.Value;
        }

        public T Add(string key, T entry)
        {
            if (_nodes.Count >= MaxKeys) Remove(_order.First!.Value.Key);
            _nodes[key] = _order.AddLast((key, entry));
            return entry;
        }

        public void Remove(string key)
        {
            if (_nodes.Remove(key, out var node)) _order.Remove(node);
        }
    }

    private sealed class Backoff
    {
        public int Failures { get; set; }
        public long LastMs { get; set; }
        public long PreviousLastMs { get; set; }
    }

    private sealed class Budget
    {
        public long WindowStartMs { get; set; }
        public int Spent { get; set; }
    }
}
