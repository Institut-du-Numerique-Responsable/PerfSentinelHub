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
    public async Task A_service_label_is_set_read_and_replaced()
    {
        Assert.True(await Database.SetServiceLabelAsync("named-svc", "Payment", "alice", Token));
        Assert.Equal("Payment", (await Database.ReadServiceLabelsAsync(["named-svc", "other-svc"], Token))["named-svc"]);
        Assert.True(await Database.SetServiceLabelAsync("named-svc", "Checkout", "bob", Token));

        var labels = await Database.ReadServiceLabelsAsync(["named-svc", "other-svc"], Token);

        Assert.Equal("Checkout", Assert.Single(labels).Value);
        Assert.False(labels.ContainsKey("other-svc"));
    }

    [Fact]
    public async Task An_empty_service_label_deletes_it()
    {
        await Database.SetServiceLabelAsync("unnamed-svc", "Shipping", "alice", Token);

        Assert.True(await Database.SetServiceLabelAsync("unnamed-svc", null, "alice", Token));

        Assert.Empty(await Database.ReadServiceLabelsAsync(["unnamed-svc"], Token));
        Assert.Empty(await Database.ReadServiceLabelsAsync([], Token));
    }

    [Fact]
    public async Task Reading_the_copies_of_a_label_stops_once_one_decides()
    {
        await Api.LabelsAccessTests.SeedAsync(Database, "copies-1", "copies-svc", "GET /a");
        await Api.LabelsAccessTests.SeedAsync(Database, "copies-2", "copies-svc", "GET /b");

        Assert.Equal(2, (await Database.ReadLabelCopiesAsync("copies-svc", null, _ => false, Token)).Count);
        Assert.Single(await Database.ReadLabelCopiesAsync("copies-svc", null, _ => true, Token));
        Assert.Single(await Database.ReadLabelCopiesAsync("copies-svc", "GET /a", _ => false, Token));
    }

    [Fact]
    public async Task No_key_reads_no_label()
    {
        Assert.Empty(await Database.ReadEndpointLabelsAsync([], Token));
    }
}
