namespace PerfSentinelHub.Api;

/// <summary>
///     Bounds how many requests of one kind run at once. Refusal is TryEnter
///     answering false, which callers turn into a 503 with Retry-After: the work
///     behind each gate buffers real memory, so waiting in line would only move
///     the pressure inside.
/// </summary>
public abstract class RequestGate(int maxConcurrent) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(maxConcurrent, maxConcurrent);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public bool TryEnter()
    {
        return _gate.Wait(0);
    }

    public void Exit()
    {
        _gate.Release();
    }

    // The hook exists because the class is inheritable, not because a subclass
    // needs it today: none of the gates below holds a field of its own. Without
    // it, one that did could only hide this Dispose, and its own field would leak.
    // ReSharper disable once VirtualMemberNeverOverridden.Global
    protected virtual void Dispose(bool disposing)
    {
        if (disposing) _gate.Dispose();
    }
}

// Bounds how many import bodies may be buffered at once (MaxImports * 2 MiB). It is not a write
// lock -- HubDatabase serializes writes -- so one slow uploader must not stall the whole fleet.
public sealed class ImportGate() : RequestGate(MaxImports)
{
    public const int MaxImports = 4;
}

/// <summary>
///     Each relay holds a daemon connection for up to three client timeouts, and
///     every one of them spends that daemon's write key, so a burst is refused
///     rather than queued.
/// </summary>
public sealed class AckRelayGate() : RequestGate(MaxRelays)
{
    // Public because AckRelayApiTests pins it, as IncidentRefreshGate.MaxReads is.
    public const int MaxRelays = 2;
}

/// <summary>
///     Each daemon read buffers a report snapshot, so a burst of folds on the
///     Sources screen is real memory rather than a few cheap requests.
/// </summary>
public sealed class DaemonViewGate() : RequestGate(MaxReads)
{
    // Public because DaemonViewApiTests pins it, the same reason
    // ImportGate.MaxImports is: the cap is a contract, not a detail.
    public const int MaxReads = 2;
}

/// <summary>
///     Each refresh reads the whole daemon fleet, and every page it pulls is
///     buffered under the client's 4 MiB cap, so a burst of screen opens is real
///     memory and real load on the daemons rather than a few cheap requests.
/// </summary>
public sealed class IncidentRefreshGate() : RequestGate(MaxReads)
{
    // Public because IncidentRefreshApiTests pins it, the same reason
    // DaemonViewGate.MaxReads is: the cap is a contract, not a detail.
    public const int MaxReads = 2;
}

/// <summary>One publication at a time: each runs the engine, and two would race on the same uuid.</summary>
public sealed class DisclosureImportGate() : RequestGate(1);

/// <summary>Anonymous share downloads at once: each holds a file of up to the PDF's size in memory.</summary>
public sealed class ShareGate() : RequestGate(4);
