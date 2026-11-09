using PerfSentinelHub.Access;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;
using PerfSentinelHub.Tests.Api;

namespace PerfSentinelHub.Tests.Storage;

/// <summary>
///     The service and grouping filters of a findings read take lists, as the recap asks them. Four
///     findings of one source cross two services with two namespaces, under names no other test uses.
/// </summary>
public sealed class RecapFilterStorageTests(HubApplicationFactory factory)
    : IClassFixture<HubApplicationFactory>, IAsyncLifetime
{
    private static readonly string[] FourFindings = ["rf-AX", "rf-AY", "rf-BX", "rf-CY"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await SeedAsync("rf-AX", "rf-a", "rf-ns-x");
        await SeedAsync("rf-AY", "rf-a", "rf-ns-y");
        await SeedAsync("rf-BX", "rf-b", "rf-ns-x");
        await SeedAsync("rf-CY", "rf-c", "rf-ns-y");
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Several_services_read_as_their_union()
    {
        Assert.Equal(["rf-AX", "rf-AY", "rf-BX"], await ReadAsync(Query() with { Services = ["rf-a", "rf-b"] }));
        Assert.Equal(FourFindings, await ReadAsync(Query() with { Services = ["rf-c", "rf-b", "rf-a"] }));
    }

    [Fact]
    public async Task Several_namespaces_read_as_their_union()
    {
        Assert.Equal(FourFindings, await ReadAsync(Query() with { Groupings = ["rf-ns-x", "rf-ns-y"] }));
    }

    // /api/findings sends one value; the recap sends a list of one. Both must read the same.
    [Fact]
    public async Task One_value_in_a_list_reads_as_the_single_value_did()
    {
        Assert.Equal(["rf-AX", "rf-AY"], await ReadAsync(Query() with { Service = "rf-a" }));
        Assert.Equal(["rf-AX", "rf-AY"], await ReadAsync(Query() with { Services = ["rf-a"] }));
        Assert.Equal(["rf-AX", "rf-BX"], await ReadAsync(Query() with { Grouping = "rf-ns-x" }));
        Assert.Equal(["rf-AX", "rf-BX"], await ReadAsync(Query() with { Groupings = ["rf-ns-x"] }));
    }

    [Fact]
    public async Task A_service_and_a_namespace_combine_with_and()
    {
        Assert.Equal(["rf-AX", "rf-BX"],
            await ReadAsync(Query() with { Services = ["rf-a", "rf-b"], Groupings = ["rf-ns-x"] }));
        Assert.Equal(["rf-AX", "rf-AY"],
            await ReadAsync(Query() with { Services = ["rf-a"], Groupings = ["rf-ns-x", "rf-ns-y"] }));
        Assert.Equal(["rf-AX"], await ReadAsync(Query() with { Services = ["rf-a"], Groupings = ["rf-ns-x"] }));
        Assert.Empty(await ReadAsync(Query() with { Services = ["rf-b"], Groupings = ["rf-ns-y"] }));
    }

    [Fact]
    public async Task Excluded_services_are_left_out_after_the_chosen_ones()
    {
        Assert.Equal(["rf-CY"], await ReadAsync(Query() with { ExcludedServices = ["rf-a", "rf-b"] }));
        Assert.Equal(["rf-BX"],
            await ReadAsync(Query() with { Services = ["rf-a", "rf-b"], ExcludedServices = ["rf-a"] }));
        Assert.Equal(FourFindings, await ReadAsync(Query() with { ExcludedServices = [] }));
    }

    [Fact]
    public async Task An_empty_list_filters_nothing()
    {
        Assert.Equal(4, (await ReadAsync(Query() with { Services = [], Groupings = [] })).Count);
    }

    // A list never widens what the caller reads: the scope holds on both lists.
    [Fact]
    public async Task A_narrowed_access_scope_holds_on_the_list_path()
    {
        var onlyA = AccessScope.Of([AccessPattern.Compile(new PatternOptions { Service = "rf-a" })]);

        Assert.Equal(["rf-AX", "rf-AY"],
            await ReadAsync(Query() with { Access = onlyA, Services = ["rf-a", "rf-b", "rf-c"] }));
        Assert.Equal(["rf-AX", "rf-AY"],
            await ReadAsync(Query() with { Access = onlyA, Groupings = ["rf-ns-x", "rf-ns-y"] }));
        Assert.Equal(["rf-AX"],
            await ReadAsync(Query() with { Access = onlyA, Services = ["rf-a", "rf-b"], Groupings = ["rf-ns-x"] }));
        Assert.Empty(await ReadAsync(Query() with { Access = onlyA, Services = ["rf-b", "rf-c"] }));
    }

    // Every test shares one database, so a read keeps only the findings seeded here.
    private static FindingQuery Query()
    {
        return new FindingQuery(null, null, null, 1000);
    }

    private async Task<IReadOnlyList<string>> ReadAsync(FindingQuery query)
    {
        var rows = await factory.Database.QueryFindingsAsync(query, Token);
        return [.. rows.Select(row => row.Signature).Where(sig => sig.StartsWith("rf-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    }

    private Task SeedAsync(string signature, string service, string grouping)
    {
        return factory.Database.UpsertBatchAsync(
            new SourceSnapshot("test", "Test", "test", "0.26.2"),
            FindingJson.Batch(FindingJson.Envelope(
                signature, service: service,
                grouping: $$"""[{"key":"k8s.namespace.name","value":"{{grouping}}"}]""")),
            5_000, Token);
    }
}
