using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Tests;

public sealed class CorrelationImportApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private const string ApiKey = "0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential
    private readonly HttpClient _client = factory.CreateClient();

    private static HttpRequestMessage Request(string? apiKey, string body, string query = "source_id=test")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/import/correlations?" + query)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (apiKey is not null)
            request.Headers.Add("X-API-Key", apiKey);
        return request;
    }

    private static string Body(params JsonObject[] episodes)
    {
        return EpisodeJson.Body(episodes);
    }

    private async Task<bool> StoredAsync(string id)
    {
        var rows = await factory.Database.ListEpisodesAsync(
            new CorrelationQuery(null, null, ["test"], null, null, false, 10_000, 0, 1000),
            TestContext.Current.CancellationToken);
        return rows.Any(row => row.Id == id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key-wrong-key-wrong-key-wrong")]
    public async Task The_route_answers_to_the_source_import_key_alone(string? apiKey)
    {
        using var request = Request(apiKey, Body(EpisodeJson.Episode(EpisodeJson.Id(1))));
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_source_is_refused_as_a_wrong_key_is()
    {
        using var request = Request(ApiKey, Body(EpisodeJson.Episode(EpisodeJson.Id(2))), "source_id=nobody");
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_pushed_episode_is_stored_and_acknowledged_after_the_commit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var request = Request(ApiKey, Body(EpisodeJson.Episode(EpisodeJson.Id(3))));
        using var response = await _client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(1, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("rejected").GetInt32());
        Assert.True(await StoredAsync(EpisodeJson.Id(3)));
    }

    [Fact]
    public async Task A_push_records_the_import_and_leaves_reachability_alone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var request = Request(ApiKey, Body(EpisodeJson.Episode(EpisodeJson.Id(4))));
        using var response = await _client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var connection = await factory.Database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT (SELECT last_import_ms FROM source_imports WHERE source_id = 'test'),
                                     (SELECT COUNT(*) FROM source_state WHERE source_id = 'test');
                              """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(10_000, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
    }

    [Fact]
    public async Task An_invalid_episode_is_refused_alone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var invalid = EpisodeJson.Episode("NOT-A-HEX-ID");
        using var request = Request(ApiKey, Body(EpisodeJson.Episode(EpisodeJson.Id(5)), invalid));
        using var response = await _client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(1, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("rejected").GetInt32());
    }

    [Fact]
    public async Task A_batch_of_only_invalid_episodes_is_a_bad_request()
    {
        using var request = Request(ApiKey, Body(EpisodeJson.Episode("NOT-A-HEX-ID")));
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"producer_version\":\"0.30.0\",\"episodes\":[]}")]
    [InlineData("{\"producer_version\":\"0.30.0\",\"producer_version\":\"0.29.0\",\"episodes\":[]}")]
    [InlineData("not json")]
    public async Task An_envelope_the_route_cannot_read_is_a_bad_request(string body)
    {
        using var request = Request(ApiKey, body);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task More_than_a_hundred_episodes_are_too_large()
    {
        var episodes = Enumerable.Range(100, 101).Select(index => EpisodeJson.Episode(EpisodeJson.Id(index))).ToArray();
        using var request = Request(ApiKey, Body(episodes));
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_body_over_one_mebibyte_is_too_large()
    {
        var episode = EpisodeJson.Episode(EpisodeJson.Id(6));
        episode["source"]!["template"] = new string('x', 1024 * 1024);
        using var request = Request(ApiKey, Body(episode));
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_full_import_gate_is_retryable_without_reading_the_body()
    {
        var gate = factory.Services.GetRequiredService<ImportGate>();
        var entered = Enumerable.Range(0, ImportGate.MaxImports).Count(_ => gate.TryEnter());
        try
        {
            using var request = Request(ApiKey, Body(EpisodeJson.Episode(EpisodeJson.Id(7))));
            using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
        }
        finally
        {
            for (var slot = 0; slot < entered; slot++) gate.Exit();
        }
    }

    [Fact]
    public async Task A_second_query_parameter_is_a_bad_request()
    {
        using var request = Request(ApiKey, Body(EpisodeJson.Episode(EpisodeJson.Id(8))), "source_id=test&extra=1");
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_refusal_counts_on_the_correlation_series()
    {
        var metrics = factory.Services.GetRequiredService<CorrelationImportMetrics>();
        var before = metrics.Snapshot().Single(entry => entry.Reason == "unauthorized").Count;

        using var request = Request(null, Body(EpisodeJson.Episode(EpisodeJson.Id(9))));
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before + 1, metrics.Snapshot().Single(entry => entry.Reason == "unauthorized").Count);
    }
}
