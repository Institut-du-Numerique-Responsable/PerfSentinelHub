using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Storage;

public sealed class LabelStorageTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private HubDatabase Database => factory.Database;

    [Fact]
    public async Task A_label_is_set_read_and_replaced()
    {
        var key = ("labelled-svc", "GET /api/cart/{id}");
        var other = ("labelled-svc", "GET /api/other");

        Assert.True(await Database.SetEndpointLabelAsync(key.Item1, key.Item2, "Showing a cart", "alice", Token));
        Assert.Equal("Showing a cart", (await Database.ReadEndpointLabelsAsync([key, other], Token))[key]);
        Assert.True(await Database.SetEndpointLabelAsync(key.Item1, key.Item2, "Viewing a basket", "bob", Token));

        var labels = await Database.ReadEndpointLabelsAsync([key, other], Token);

        Assert.Equal("Viewing a basket", Assert.Single(labels).Value);
        Assert.False(labels.ContainsKey(other));
    }

    [Fact]
    public async Task An_empty_label_deletes_it()
    {
        var key = ("deleted-svc", "POST /orders");
        await Database.SetEndpointLabelAsync(key.Item1, key.Item2, "Placing an order", "alice", Token);

        Assert.True(await Database.SetEndpointLabelAsync(key.Item1, key.Item2, null, "alice", Token));

        Assert.Empty(await Database.ReadEndpointLabelsAsync([key], Token));
    }

    [Fact]
    public async Task A_label_belongs_to_one_service_and_endpoint()
    {
        await Database.SetEndpointLabelAsync("pair-a", "GET /x", "Alpha", "alice", Token);

        var labels = await Database.ReadEndpointLabelsAsync([("pair-b", "GET /x"), ("pair-a", "GET /y")], Token);

        Assert.Empty(labels);
    }

    [Fact]
    public async Task No_key_reads_no_label()
    {
        Assert.Empty(await Database.ReadEndpointLabelsAsync([], Token));
    }
}
