using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     Naming an endpoint in business words. Access is off here, so every caller is an admin and
///     the identity is the proxy header; <see cref="LabelsAccessTests" /> owns what each role may do.
/// </summary>
public sealed class LabelsApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>, IAsyncLifetime
{
    private const string Service = "order-svc";
    private const string Endpoint = "GET /orders";
    internal const string ServicesPath = "/api/labels/services";
    private HttpClient _client = null!;

    private WebApplicationFactory<Program> _scoped = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _scoped = AckRelayApiTests.Scoped(factory, true, TimeSpan.FromSeconds(10));
        _client = _scoped.CreateClient();
        await LabelsAccessTests.SeedAsync(factory.Database, "access-off", Service, Endpoint);
        // The Hub is shared by the class, so each test starts with the endpoint unnamed.
        await factory.Database.SetEndpointLabelAsync(Service, Endpoint, null, "alice", Token);
        await factory.Database.SetServiceLabelAsync(Service, null, "alice", Token);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _scoped.DisposeAsync();
    }

    [Fact]
    public async Task A_label_needs_an_identity()
    {
        using var response = await SendAsync(Body(Service, Endpoint, "Listing orders"), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The Hub does not know who is asking, and a label is set in somebody's name.",
            await DetailAsync(response));
    }

    [Fact]
    public async Task A_label_is_set_replaced_and_removed()
    {
        using var set = await SendAsync(Body(Service, Endpoint, "  Listing orders  "));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal("Listing orders", await LabelAsync(Service, Endpoint));

        using var replaced = await SendAsync(Body(Service, Endpoint, "Showing the order list"));
        Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);
        Assert.Equal("Showing the order list", await LabelAsync(Service, Endpoint));

        using var removed = await SendAsync(Body(Service, Endpoint, null));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Null(await LabelAsync(Service, Endpoint));
    }

    [Fact]
    public async Task A_blank_label_removes_it()
    {
        await factory.Database.SetEndpointLabelAsync(Service, Endpoint, "Listing orders", "alice", Token);

        using var response = await SendAsync(Body(Service, Endpoint, "   "));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await LabelAsync(Service, Endpoint));
    }

    [Fact]
    public async Task An_endpoint_the_hub_holds_no_finding_at_is_a_404()
    {
        using var response = await SendAsync(Body(Service, "GET /nowhere", "Nowhere"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("The Hub holds no finding at this endpoint.", await DetailAsync(response));
        Assert.Null(await LabelAsync(Service, "GET /nowhere"));
    }

    [Theory]
    [InlineData("""{"service":"order-svc","endpoint":"GET /orders","label":"Listing orders"}""", 204)]
    [InlineData("""{"service":"order-svc","endpoint":"GET /orders","label":"EIGHTY"}""", 204)]
    [InlineData("""{"service":"order-svc","endpoint":"GET /orders","label":"EIGHTYONE"}""", 400)]
    [InlineData("""{"service":"order-svc","endpoint":"GET /orders","label":"Listing\u0007orders"}""", 400)]
    // A right-to-left override, which would turn the name around where it is shown.
    [InlineData("""{"service":"order-svc","endpoint":"GET /orders","label":"Listing\u202Eorders"}""", 400)]
    [InlineData("""{"service":"order-svc","endpoint":"GET /orders","label":7}""", 400)]
    [InlineData("""{"service":"order-svc","endpoint":"GET /orders"}""", 400)]
    [InlineData("""{"endpoint":"GET /orders","label":"Listing orders"}""", 400)]
    [InlineData("""{"service":"","endpoint":"GET /orders","label":"Listing orders"}""", 400)]
    [InlineData("""{"service":"order-svc","label":"Listing orders"}""", 400)]
    [InlineData("""{"service":"order-svc","endpoint":"GET /\norders","label":"Listing orders"}""", 400)]
    [InlineData("""{"service":"order-svc","endpoint":7,"label":"Listing orders"}""", 400)]
    [InlineData("not json", 400)]
    [InlineData("[]", 400)]
    // Not a string .NET can hold, which the reader throws on.
    [InlineData("""{"service":"\ud800","endpoint":"GET /orders","label":"Listing orders"}""", 400)]
    public async Task A_label_body_is_judged(string body, int expected)
    {
        body = body.Replace("EIGHTYONE", new string('x', 81), StringComparison.Ordinal)
            .Replace("EIGHTY", new string('x', 80), StringComparison.Ordinal);

        using var response = await SendAsync(body);

        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Fact]
    public async Task A_cross_site_label_write_is_refused()
    {
        using var response = await SendAsync(Body(Service, Endpoint, "Listing orders"), "alice", "cross-site");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await LabelAsync(Service, Endpoint));
    }

    [Fact]
    public async Task A_body_over_the_cap_is_a_413()
    {
        using var response = await SendAsync(Body(Service, new string('e', 9 * 1024), "Listing orders"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_service_label_needs_an_identity()
    {
        using var response = await SendAsync(ServiceBody(Service, "Ordering"), null, path: ServicesPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The Hub does not know who is asking, and a label is set in somebody's name.",
            await DetailAsync(response));
    }

    [Fact]
    public async Task A_service_label_is_set_and_removed()
    {
        using var set = await SendAsync(ServiceBody(Service, "  Ordering  "), path: ServicesPath);
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal("Ordering", await ServiceLabelAsync(Service));

        using var removed = await SendAsync(ServiceBody(Service, null), path: ServicesPath);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Null(await ServiceLabelAsync(Service));
    }

    [Fact]
    public async Task A_service_the_hub_holds_no_finding_of_is_a_404()
    {
        using var response = await SendAsync(ServiceBody("nowhere-svc", "Nowhere"), path: ServicesPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("The Hub holds no finding of this service.", await DetailAsync(response));
        Assert.Null(await ServiceLabelAsync("nowhere-svc"));
    }

    [Theory]
    [InlineData("""{"service":"order-svc","label":"EIGHTYONE"}""")]
    [InlineData("""{"service":"order-svc","label":"Order\u0007ing"}""")]
    [InlineData("""{"service":"order-svc","label":"Order\u202Eing"}""")]
    [InlineData("""{"label":"Ordering"}""")]
    [InlineData("""{"service":"order-svc"}""")]
    public async Task A_bad_service_label_is_a_400(string body)
    {
        body = body.Replace("EIGHTYONE", new string('x', 81), StringComparison.Ordinal);

        using var response = await SendAsync(body, path: ServicesPath);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await ServiceLabelAsync(Service));
    }

    [Fact]
    public async Task A_cross_site_service_label_write_is_refused()
    {
        using var response = await SendAsync(ServiceBody(Service, "Ordering"), "alice", "cross-site", ServicesPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await ServiceLabelAsync(Service));
    }

    private Task<HttpResponseMessage> SendAsync(
        string body,
        string? identity = "alice",
        string? site = null,
        string path = "/api/labels")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (identity is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-User", identity);
        if (site is not null)
            request.Headers.Add("Sec-Fetch-Site", site);
        return _client.SendAsync(request, Token);
    }

    internal static string Body(string service, string endpoint, string? label)
    {
        return new JsonObject { ["service"] = service, ["endpoint"] = endpoint, ["label"] = label }.ToJsonString();
    }

    internal static string ServiceBody(string service, string? label)
    {
        return new JsonObject { ["service"] = service, ["label"] = label }.ToJsonString();
    }

    private async Task<string?> ServiceLabelAsync(string service)
    {
        return (await factory.Database.ReadServiceLabelsAsync([service], Token)).GetValueOrDefault(service);
    }

    private async Task<string?> LabelAsync(string service, string endpoint)
    {
        var labels = await factory.Database.ReadEndpointLabelsAsync([(service, endpoint)], Token);
        return labels.GetValueOrDefault((service, endpoint));
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response)
    {
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["detail"]!.GetValue<string>();
    }
}

/// <summary>
///     Hub:Access on: a label is written by a caller holding <c>labels.write</c> on a source that holds
///     a finding at the endpoint, within what it reads. The built-in <c>reader</c> lacks it, <c>member</c> has it.
/// </summary>
public sealed class LabelsAccessTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private const string Service = "cart-svc";
    private const string Endpoint = "GET /api/cart/{id}";

    private static readonly Dictionary<string, string> Users = new(StringComparer.Ordinal)
    {
        ["reader"] = """{"sub":"u-reader","email":"reader@example.internal","groups":["/readers"]}""",
        ["member"] = """{"sub":"u-member","email":"member@example.internal","groups":["/members"]}""",
        ["scoped"] = """{"sub":"u-scoped","email":"scoped@example.internal","groups":["/scoped"]}"""
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static Task SeedAsync(HubDatabase database, string name, string service, string endpoint)
    {
        return database.UpsertBatchAsync(
            new SourceSnapshot("test", "Test", "test", "0.26.2"),
            FindingJson.Batch(FindingJson.Envelope($"n_plus_one_sql:{service}:{name}", service: service,
                endpoint: endpoint)),
            10_000,
            Token);
    }

    [Theory]
    [InlineData("reader", HttpStatusCode.Forbidden)]
    [InlineData("member", HttpStatusCode.NoContent)]
    public async Task A_label_needs_labels_write_on_a_source_holding_the_endpoint(string user, HttpStatusCode expected)
    {
        // One endpoint per case: the Hub is shared by the class and a label outlives its test.
        var endpoint = $"{Endpoint}/{user}";
        await SeedAsync(factory.Database, $"access-{user}", Service, endpoint);
        await using var provider = await FakeProvider.StartAsync(Users);
        await using var hub = Hub(provider);
        using var client = FakeProvider.Client(hub);
        using var callback = await FakeProvider.SignInAsync(client, user);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/labels");
        request.Headers.Add("Cookie", FakeProvider.SessionCookie(callback));
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Content = new StringContent(LabelsApiTests.Body(Service, endpoint, "Showing a cart"), Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, Token);

        Assert.Equal(expected, response.StatusCode);
        var labels = await factory.Database.ReadEndpointLabelsAsync([(Service, endpoint)], Token);
        Assert.Equal(expected == HttpStatusCode.NoContent ? 1 : 0, labels.Count);
        if (expected == HttpStatusCode.Forbidden)
            Assert.Equal("You may not name endpoints.",
                JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["detail"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("reader", HttpStatusCode.Forbidden)]
    [InlineData("member", HttpStatusCode.NoContent)]
    public async Task A_service_label_needs_labels_write_on_a_source_holding_the_service(
        string user,
        HttpStatusCode expected)
    {
        // One service per case: the Hub is shared by the class and a name outlives its test.
        var service = $"{Service}-{user}";
        await SeedAsync(factory.Database, $"service-{user}", service, Endpoint);
        await using var provider = await FakeProvider.StartAsync(Users);
        await using var hub = Hub(provider);
        using var client = FakeProvider.Client(hub);
        using var callback = await FakeProvider.SignInAsync(client, user);
        using var request = new HttpRequestMessage(HttpMethod.Put, LabelsApiTests.ServicesPath);
        request.Headers.Add("Cookie", FakeProvider.SessionCookie(callback));
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Content = new StringContent(LabelsApiTests.ServiceBody(service, "Basket"), Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, Token);

        Assert.Equal(expected, response.StatusCode);
        var labels = await factory.Database.ReadServiceLabelsAsync([service], Token);
        Assert.Equal(expected == HttpStatusCode.NoContent ? 1 : 0, labels.Count);
        if (expected == HttpStatusCode.Forbidden)
            Assert.Equal("You may not name services.",
                JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_endpoint_the_caller_cannot_read_answers_as_an_unknown_one()
    {
        await SeedAsync(factory.Database, "access-scoped", Service, Endpoint);
        await using var provider = await FakeProvider.StartAsync(Users);
        await using var hub = Hub(provider);
        using var client = FakeProvider.Client(hub);
        using var callback = await FakeProvider.SignInAsync(client, "scoped");

        using var hidden = await Send(client, callback, Service, Endpoint);
        using var unknown = await Send(client, callback, Service, "GET /unknown");

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsByteArrayAsync(Token),
            await hidden.Content.ReadAsByteArrayAsync(Token));
    }

    private static Task<HttpResponseMessage> Send(
        HttpClient client,
        HttpResponseMessage callback,
        string service,
        string endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/labels");
        request.Headers.Add("Cookie", FakeProvider.SessionCookie(callback));
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Content = new StringContent(LabelsApiTests.Body(service, endpoint, "Showing a cart"), Encoding.UTF8,
            "application/json");
        return client.SendAsync(request, Token);
    }

    private WebApplicationFactory<Program> Hub(FakeProvider provider)
    {
        // The factory's clock sits in 1970, where every cookie the handlers write has already expired.
        return factory.WithWebHostBuilder(builder =>
        {
            provider.Configure(builder);
            builder.UseSetting("Hub:Access:Enabled", "true");
            builder.UseSetting("Hub:MetricsPort", "9464");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(TimeProvider.System);
                services.PostConfigure<HubOptions>(options => options.Access = new AccessOptions
                {
                    Enabled = true,
                    Admins = ["u-admin"],
                    Projects =
                    [
                        new ProjectOptions
                            { Name = "elsewhere", Patterns = [new PatternOptions { Service = "billing-svc" }] }
                    ],
                    Grants =
                    [
                        new GrantOptions { Group = "/readers", Project = "*", Role = "reader" },
                        new GrantOptions { Group = "/members", Project = "*", Role = "member" },
                        new GrantOptions { Group = "/scoped", Project = "elsewhere", Role = "member" }
                    ]
                });
            });
        });
    }
}
