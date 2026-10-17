using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PerfSentinelHub.Tests.Api;

public sealed class CorrelationsApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task SeedAsync(params JsonObject[] episodes)
    {
        Assert.True(await factory.Database.TryUpsertEpisodesAsync(
            "test", EpisodeJson.Parsed(episodes), 10_000, TestContext.Current.CancellationToken));
    }

    private async Task<JsonElement> GetAsync(string query)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await _client.GetAsync("/api/correlations?" + query, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    private static string Ids(JsonElement rows)
    {
        return string.Join(',', rows.EnumerateArray().Select(row => row.GetProperty("id").GetString()![^2..]));
    }

    [Fact]
    public async Task An_episode_is_served_in_snake_case_with_both_sides()
    {
        var pair = EpisodeJson.Id(0xb1);
        var grouped = EpisodeJson.Episode(EpisodeJson.Id(1), pairKey: pair);
        grouped["target"]!["grouping_key"] = "k8s.namespace.name";
        grouped["target"]!["grouping_value"] = "payments";
        await SeedAsync(grouped);

        var episode = Assert.Single((await GetAsync($"pair_key={pair}")).EnumerateArray());

        Assert.Equal(EpisodeJson.Id(1), episode.GetProperty("id").GetString());
        Assert.Equal("test", episode.GetProperty("source_id").GetString());
        Assert.Equal("Test", episode.GetProperty("source_name").GetString());
        Assert.Equal("n_plus_one_sql", episode.GetProperty("source").GetProperty("finding_type").GetString());
        Assert.Equal(JsonValueKind.Null, episode.GetProperty("source").GetProperty("grouping_value").ValueKind);
        Assert.Equal("payments", episode.GetProperty("target").GetProperty("grouping_value").GetString());
        Assert.Equal("payment-svc", episode.GetProperty("target").GetProperty("service").GetString());
        Assert.Equal(1_800_000_000_000, episode.GetProperty("started_at_ms").GetInt64());
        Assert.Equal(JsonValueKind.Null, episode.GetProperty("ended_at_ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, episode.GetProperty("ended_by").ValueKind);
        Assert.Equal(0.92, episode.GetProperty("peak_confidence").GetDouble());
        Assert.Equal(1200.0, episode.GetProperty("median_lag_ms").GetDouble());
        Assert.Equal("00f067aa0ba902b74bf92f3577b34da6", episode.GetProperty("source_sample_trace_id").GetString());
        Assert.Equal(600_000, episode.GetProperty("gap_ms").GetInt64());
    }

    [Fact]
    public async Task Service_active_and_the_span_narrow_the_listing()
    {
        await SeedAsync(
            EpisodeJson.Episode(EpisodeJson.Id(0x11), 1_800_000_000_000, 1_800_000_060_000, 1_800_000_060_000,
                EpisodeJson.Id(0xc1), "api-a", "api-b"),
            EpisodeJson.Episode(EpisodeJson.Id(0x12), 1_800_000_500_000, 1_800_000_560_000, null,
                EpisodeJson.Id(0xc2), "api-c", "api-a"));

        Assert.Equal("12,11", Ids(await GetAsync("service=api-a")));
        Assert.Equal("12", Ids(await GetAsync("service=api-a&active=true")));
        Assert.Equal("11", Ids(await GetAsync("service=api-a&from=1800000000000&to=1800000100000")));
        Assert.Equal("12", Ids(await GetAsync("service=api-a&from=1800000400000")));
        Assert.Equal("11", Ids(await GetAsync("service=api-a&limit=1&offset=1")));
        Assert.Equal("12,11", Ids(await GetAsync("service=api-a&source_id=test&environment=test")));
    }

    [Fact]
    public async Task A_blank_filter_means_no_filter()
    {
        var pair = EpisodeJson.Id(0xb2);
        await SeedAsync(EpisodeJson.Episode(EpisodeJson.Id(0x21), pairKey: pair));

        // What Grafana sends for a variable set to All.
        Assert.Equal("21", Ids(await GetAsync(
            $"pair_key={pair}&service=&source_id=%20&environment=&from=&to=&active=")));
    }

    [Fact]
    public async Task The_listing_stops_at_200_unless_asked_for_up_to_1000()
    {
        var pair = EpisodeJson.Id(0xd1);
        for (var batch = 0; batch < 3; batch++)
            await SeedAsync([
                .. Enumerable.Range(0x1000 + batch * 70, 70)
                    .Select(index => EpisodeJson.Episode(EpisodeJson.Id(index), pairKey: pair))
            ]);

        Assert.Equal(200, (await GetAsync($"pair_key={pair}")).GetArrayLength());
        Assert.Equal(210, (await GetAsync($"pair_key={pair}&limit=1000")).GetArrayLength());
    }

    [Theory]
    [InlineData("source_id=nobody")]
    [InlineData("environment=nowhere")]
    [InlineData("from=2&to=1")]
    [InlineData("from=yesterday")]
    [InlineData("limit=0")]
    [InlineData("limit=1001")]
    [InlineData("offset=-1")]
    [InlineData("active=yes")]
    [InlineData("service=a&service=b")]
    public async Task A_query_the_route_cannot_read_is_a_bad_request(string query)
    {
        using var response = await _client.GetAsync("/api/correlations?" + query,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
