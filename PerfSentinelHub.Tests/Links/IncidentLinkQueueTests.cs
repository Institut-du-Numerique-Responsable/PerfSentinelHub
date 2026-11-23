using PerfSentinelHub.Links;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Links;

public sealed class IncidentLinkQueueTests
{
    private static readonly IncidentKey Key = new("incident-1", "test");

    private static async Task<List<IncidentKey>> TakeAsync(IncidentLinkQueue queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var keys = new List<IncidentKey>();
        try
        {
            await foreach (var key in queue.ReadAllAsync(timeout.Token))
                keys.Add(key);
        }
        catch (OperationCanceledException)
        {
        }

        return keys;
    }

    [Fact]
    public async Task A_key_already_waiting_is_not_queued_twice()
    {
        var queue = new IncidentLinkQueue();
        queue.Enqueue(Key);
        queue.Enqueue(Key);

        Assert.Equal([Key], await TakeAsync(queue));
    }

    [Fact]
    public async Task A_key_queued_again_after_it_was_taken_is_delivered_again()
    {
        var queue = new IncidentLinkQueue();
        queue.Enqueue(Key);
        Assert.Equal([Key], await TakeAsync(queue));

        queue.Enqueue(Key);

        Assert.Equal([Key], await TakeAsync(queue));
    }
}
