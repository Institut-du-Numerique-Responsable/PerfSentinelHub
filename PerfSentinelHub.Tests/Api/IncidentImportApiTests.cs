using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Tests.Api;

public sealed class IncidentImportApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private const string ApiKey = "0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential
    private readonly HttpClient _client = factory.CreateClient();

    private static JsonObject FixtureIncident(string id)
    {
        var array = JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "daemon-incidents-0.20.0.json")))!.AsArray();
        var incident = array[0]!.AsObject();
        incident["id"] = id;
        return incident;
    }

    private static HttpRequestMessage Request(string? apiKey, IEnumerable<JsonNode> incidents,
        string query = "source_id=test")
    {
        var body = new JsonObject
        {
            ["producer_version"] = "0.29.0",
            ["incidents"] = new JsonArray(incidents.Select(node => node.DeepClone()).ToArray())
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/import/incidents?" + query)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (apiKey is not null)
            request.Headers.Add("X-API-Key", apiKey);
        return request;
    }

    private static string Id(int index)
    {
        return "b" + index.ToString("x31", CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key-wrong-key-wrong-key-wrong")]
    public async Task The_route_answers_to_the_source_import_key_alone(string? apiKey)
    {
        using var request = Request(apiKey, [FixtureIncident(Id(1))]);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_source_is_refused_as_a_wrong_key_is()
    {
        using var request = Request(ApiKey, [FixtureIncident(Id(2))], "source_id=nobody");
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_pushed_incident_is_stored_and_acknowledged_after_the_commit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var request = Request(ApiKey, [FixtureIncident(Id(3))]);
        using var response = await _client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(1, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("rejected").GetInt32());
        Assert.NotNull(await factory.Database.FindIncidentAsync(Id(3), null, cancellationToken));
    }

    [Fact]
    public async Task An_invalid_incident_is_refused_alone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var request = Request(ApiKey, [FixtureIncident(Id(4)), FixtureIncident("NOT-A-HEX-ID")]);
        using var response = await _client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(1, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("rejected").GetInt32());
    }

    [Fact]
    public async Task A_batch_of_only_invalid_incidents_is_a_bad_request()
    {
        using var request = Request(ApiKey, [FixtureIncident("NOT-A-HEX-ID")]);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task More_than_ten_incidents_are_too_large()
    {
        using var request = Request(ApiKey, Enumerable.Range(10, 11).Select(index => FixtureIncident(Id(index))));
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_body_over_four_mebibytes_is_too_large()
    {
        var incident = FixtureIncident(Id(30));
        incident["detail"] = new string('x', 4 * 1024 * 1024);
        using var request = Request(ApiKey, [incident]);
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
            using var request = Request(ApiKey, [FixtureIncident(Id(31))]);
            using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
        }
        finally
        {
            for (var slot = 0; slot < entered; slot++)
                gate.Exit();
        }
    }

    [Fact]
    public async Task A_second_query_parameter_is_a_bad_request()
    {
        using var request = Request(ApiKey, [FixtureIncident(Id(32))], "source_id=test&extra=1");
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
