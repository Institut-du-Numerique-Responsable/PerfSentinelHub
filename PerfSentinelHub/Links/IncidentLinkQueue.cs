using System.Threading.Channels;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Links;

/// <summary>
///     Incidents waiting for their links to be assessed. A key already waiting is not queued twice;
///     one taken by the worker can be queued again, so a merge that lands mid-assessment is seen.
/// </summary>
public sealed class IncidentLinkQueue
{
    private readonly Channel<IncidentKey> _channel =
        Channel.CreateUnbounded<IncidentKey>(new UnboundedChannelOptions { SingleReader = true });

    private readonly HashSet<IncidentKey> _pending = [];

    public void Enqueue(IncidentKey key)
    {
        lock (_pending)
        {
            if (_pending.Add(key))
                _channel.Writer.TryWrite(key);
        }
    }

    public async IAsyncEnumerable<IncidentKey> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var key in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            lock (_pending)
            {
                _pending.Remove(key);
            }

            yield return key;
        }
    }
}
