using System.Net;
using System.Text.Json;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     GET /api/recap with several services and namespaces, on a database of its own: the findings seeded
///     here would otherwise reach the pilotage test of RecapApiTests, which counts the endpoints of day 0.
/// </summary>
public sealed class RecapFilterApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_service_still_filters_and_is_echoed_as_a_string()
    {
        await SeedFilteredAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/recap?period=day&service=rcp-a", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(["rcp-a-x", "rcp-a-y"], Signatures(document).Order());
        var filters = document.RootElement.GetProperty("filters");
        Assert.Equal("rcp-a", filters.GetProperty("service").GetString());
        // Absent, not null: the filters of a one-value recap are what they were before the lists.
        Assert.False(filters.TryGetProperty("services", out _));
        Assert.False(filters.TryGetProperty("groupings", out _));
    }

    [Fact]
    public async Task A_repeated_key_filters_on_every_value_in_a_canonical_order()
    {
        await SeedFilteredAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/recap?period=day&service=rcp-b&service=rcp-a&service=rcp-a&service=&grouping=rcp-ns-x&grouping=rcp-ns-z",
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(["rcp-a-x", "rcp-b-x"], Signatures(document).Order());
        var filters = document.RootElement.GetProperty("filters");
        Assert.Equal(JsonValueKind.Null, filters.GetProperty("service").ValueKind);
        Assert.Equal(["rcp-a", "rcp-b"], filters.GetProperty("services").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(["rcp-ns-x", "rcp-ns-z"],
            filters.GetProperty("groupings").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(JsonValueKind.Null, filters.GetProperty("grouping").ValueKind);
    }

    [Fact]
    public async Task A_filter_list_holds_fifty_values_of_256_characters_and_no_more()
    {
        using var client = factory.CreateClient();
        var longest = new string('a', 256);

        using var many = await client.GetAsync($"/api/recap?period=day&{Repeated("service", 50, "s")}", Token);
        using var wide = await client.GetAsync($"/api/recap?period=day&service={longest}&grouping={longest}", Token);

        Assert.Equal(HttpStatusCode.OK, many.StatusCode);
        Assert.Equal(HttpStatusCode.OK, wide.StatusCode);
    }

    [Theory]
    [InlineData("service", 51, "s")]
    [InlineData("grouping", 51, "g")]
    public async Task More_than_fifty_values_are_a_400(string name, int count, string stem)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/recap?period=day&{Repeated(name, count, stem)}", Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("invalid_recap_query", document.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("service")]
    [InlineData("grouping")]
    public async Task A_value_over_256_characters_or_with_a_control_character_is_a_400(string name)
    {
        using var client = factory.CreateClient();

        using var tooLong = await client.GetAsync($"/api/recap?period=day&{name}={new string('a', 257)}", Token);
        using var amongOthers = await client.GetAsync(
            $"/api/recap?period=day&{name}=ok&{name}={new string('a', 257)}", Token);
        using var control = await client.GetAsync($"/api/recap?period=day&{name}=a%1Fb", Token);

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, amongOthers.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, control.StatusCode);
    }

    // Only the two filters take a repeated key; any other keeps one value.
    [Fact]
    public async Task A_key_other_than_the_two_filters_still_takes_one_value()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/recap?period=day&environment=a&environment=b", Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string Repeated(string name, int count, string stem)
    {
        return string.Join('&', Enumerable.Range(0, count).Select(index => $"{name}={stem}{index}"));
    }

    private static IEnumerable<string> Signatures(JsonDocument recap)
    {
        return recap.RootElement.GetProperty("priorities").EnumerateArray()
            .Select(priority => priority.GetProperty("signature").GetString()!);
    }

    // Two services across two namespaces, plus a third service, all seen on day 0.
    private async Task SeedFilteredAsync()
    {
        foreach (var (signature, service, grouping) in new[]
                 {
                     ("rcp-a-x", "rcp-a", "rcp-ns-x"), ("rcp-a-y", "rcp-a", "rcp-ns-y"),
                     ("rcp-b-x", "rcp-b", "rcp-ns-x"), ("rcp-c-x", "rcp-c", "rcp-ns-x")
                 })
            await factory.Database.UpsertBatchAsync(
                new SourceSnapshot("test", "Test", "test", "0.26.2"),
                FindingJson.Batch(FindingJson.Envelope(
                    signature, service: service,
                    grouping: $$"""[{"key":"k8s.namespace.name","value":"{{grouping}}"}]""")),
                5_000, Token);
    }
}
