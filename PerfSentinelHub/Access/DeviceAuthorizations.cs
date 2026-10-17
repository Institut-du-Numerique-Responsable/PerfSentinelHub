using System.Buffers.Text;
using System.Security.Cryptography;

namespace PerfSentinelHub.Access;

/// <summary>
///     The device codes in flight, in memory: the chart runs one replica, and a restart only
///     cancels the logins in flight. Anonymous callers never write to SQLite. Expired entries are
///     removed when read and swept on every start; there is no timer.
/// </summary>
public sealed class DeviceAuthorizations(TimeProvider timeProvider)
{
    private readonly Dictionary<string, Entry> _byDevice = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _byUser = new(StringComparer.Ordinal);

    // shortcut: keyed by signed-in subjects only, so it grows with the user count; prune it if
    // that ever matters.
    private readonly Dictionary<string, (long StartMs, int Count)> _failures = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _byDevice.Count;
            }
        }
    }

    private long NowMs => timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    // Null over a cap: 200 live codes in all, 10 per normalized client address.
    public DeviceStart? Start(string clientAddress)
    {
        var now = NowMs;
        lock (_lock)
        {
            foreach (var expired in _byDevice.Values.Where(entry => entry.ExpiresAtMs <= now).ToList())
                Remove(expired);
            if (_byDevice.Count >= TerminalAccess.MaxPending ||
                _byDevice.Values.Count(entry => entry.ClientAddress == clientAddress) >=
                TerminalAccess.MaxPendingPerAddress)
                return null;

            string userCode;
            do
            {
                userCode = new string(RandomNumberGenerator.GetItems<char>(
                    TerminalAccess.UserCodeAlphabet, TerminalAccess.UserCodeLength));
            } while (_byUser.ContainsKey(userCode));

            var entry = new Entry(
                Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)), userCode, clientAddress, now,
                now + TerminalAccess.ExpiresInSeconds * 1000L);
            _byDevice[entry.DeviceCode] = entry;
            _byUser[userCode] = entry;
            return new DeviceStart(entry.DeviceCode, userCode);
        }
    }

    public DevicePoll Poll(string deviceCode)
    {
        var now = NowMs;
        lock (_lock)
        {
            if (!_byDevice.TryGetValue(deviceCode, out var entry)) return new DevicePoll("invalid_grant");
            if (entry.ExpiresAtMs <= now)
            {
                Remove(entry);
                return new DevicePoll("expired_token");
            }

            var tooSoon = entry.LastPollMs is { } last && now - last < entry.IntervalMs;
            entry.LastPollMs = now;
            if (tooSoon)
            {
                entry.IntervalMs += TerminalAccess.IntervalSeconds * 1000L;
                return new DevicePoll("slow_down");
            }

            switch (entry.State)
            {
                case State.Denied:
                    Remove(entry);
                    return new DevicePoll("access_denied");
                case State.Approved:
                    // Claimed while its row is written, so a second poll cannot mint a second token.
                    entry.State = State.Issuing;
                    return new DevicePoll(null, entry.Approval, entry.ClientAddress);
                default:
                    return new DevicePoll("authorization_pending");
            }
        }
    }

    // The token row is stored: the code is spent.
    public void Issued(string deviceCode)
    {
        lock (_lock)
        {
            if (_byDevice.TryGetValue(deviceCode, out var entry))
                Remove(entry);
        }
    }

    // The row could not be written: the next poll tries again.
    public void Unclaim(string deviceCode)
    {
        lock (_lock)
        {
            if (_byDevice.TryGetValue(deviceCode, out var entry) && entry.State == State.Issuing)
                entry.State = State.Approved;
        }
    }

    // A code still waiting for a decision, null otherwise. userCode is normalized.
    public PendingCode? Find(string userCode)
    {
        var now = NowMs;
        lock (_lock)
        {
            return Pending(userCode, now) is { } entry
                ? new PendingCode(entry.UserCode, entry.ClientAddress, entry.RequestedAtMs)
                : null;
        }
    }

    // Approves, or denies with null. A code is decided once: false when it is not pending.
    public bool Decide(string userCode, DeviceApproval? approval)
    {
        var now = NowMs;
        lock (_lock)
        {
            if (Pending(userCode, now) is not { } entry) return false;
            entry.State = approval is null ? State.Denied : State.Approved;
            entry.Approval = approval;
            return true;
        }
    }

    // A malformed, unknown or expired code entered by this subject.
    public void Fail(string subject)
    {
        var now = NowMs;
        lock (_lock)
        {
            _failures[subject] = _failures.TryGetValue(subject, out var window) &&
                                 now - window.StartMs < TerminalAccess.FailureWindowMs
                ? (window.StartMs, window.Count + 1)
                : (now, 1);
        }
    }

    // How long this subject must wait, or null while under the limit.
    public TimeSpan? Throttled(string subject)
    {
        var now = NowMs;
        lock (_lock)
        {
            if (!_failures.TryGetValue(subject, out var window)) return null;
            var left = window.StartMs + TerminalAccess.FailureWindowMs - now;
            if (left <= 0) _failures.Remove(subject);
            return left > 0 && window.Count >= TerminalAccess.MaxFailures ? TimeSpan.FromMilliseconds(left) : null;
        }
    }

    // Uppercased, without '-' and whitespace; null unless eight letters of the alphabet remain.
    public static string? NormalizeUserCode(string input)
    {
        var code =
            new string([.. input.Where(letter => letter != '-' && !char.IsWhiteSpace(letter))]).ToUpperInvariant();
        return code.Length == TerminalAccess.UserCodeLength && code.All(TerminalAccess.UserCodeAlphabet.Contains)
            ? code
            : null;
    }

    public static string Display(string userCode)
    {
        return userCode[..4] + "-" + userCode[4..];
    }

    // Under the lock. An expired entry is removed as it is read.
    private Entry? Pending(string userCode, long now)
    {
        if (!_byUser.TryGetValue(userCode, out var entry)) return null;
        if (entry.ExpiresAtMs > now) return entry.State == State.Pending ? entry : null;
        Remove(entry);
        return null;
    }

    private void Remove(Entry entry)
    {
        _byDevice.Remove(entry.DeviceCode);
        _byUser.Remove(entry.UserCode);
    }

    private enum State
    {
        Pending,
        Approved,
        Issuing,
        Denied
    }

    private sealed class Entry(
        string deviceCode,
        string userCode,
        string clientAddress,
        long requestedAtMs,
        long expiresAtMs)
    {
        public string DeviceCode { get; } = deviceCode;
        public string UserCode { get; } = userCode;
        public string ClientAddress { get; } = clientAddress;
        public long RequestedAtMs { get; } = requestedAtMs;
        public long ExpiresAtMs { get; } = expiresAtMs;
        public long IntervalMs { get; set; } = TerminalAccess.IntervalSeconds * 1000L;
        public long? LastPollMs { get; set; }
        public State State { get; set; }
        public DeviceApproval? Approval { get; set; }
    }
}
