using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     The card signing key routes. Access is off except where a test turns it on, so the caller is
///     the proxy header; the key files sit beside a database inside this class's own directory.
/// </summary>
public sealed class CardKeysApiTests : IDisposable
{
    private const string KeysPath = "/api/cards/keys";
    private const string RotatePath = "/api/cards/keys/rotate";
    private const string RevokePath = "/api/cards/keys/revoke";

    private static readonly string Fingerprint = "ps1:" + new string('A', 22);

    private static readonly Dictionary<string, string> Users = new(StringComparer.Ordinal)
    {
        ["member"] = """{"sub":"u-member","email":"member@example.internal","groups":["/members"]}""",
        ["admin"] = """{"sub":"u-admin","email":"admin@example.internal","groups":[]}"""
    };

    private readonly List<WebApplicationFactory<Program>> _hubs = [];

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-kapi-{Guid.NewGuid():N}");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_workspace, "data", "hub.db");

    public void Dispose()
    {
        foreach (var hub in _hubs)
            hub.Dispose();
        TestPool.ClearFor(DatabasePath);
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, true);
    }

    [Theory]
    [InlineData("GET", KeysPath)]
    [InlineData("POST", RotatePath)]
    [InlineData("POST", RevokePath)]
    public async Task Keys_need_access_admin(string method, string path)
    {
        await using var provider = await FakeProvider.StartAsync(Users);

        using var response = await SignedInSendAsync(provider, "member", method, path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You may not manage the card signing key.", await DetailAsync(response));
        Assert.False(File.Exists(Path.Combine(_workspace, "reports", "card-keygen-args.txt")));
    }

    [Fact]
    public async Task An_access_admin_reads_the_keys()
    {
        await using var provider = await FakeProvider.StartAsync(Users);

        using var response = await SignedInSendAsync(provider, "admin", "GET", KeysPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", KeysPath)]
    [InlineData("POST", RotatePath)]
    [InlineData("POST", RevokePath)]
    public async Task Keys_answer_404_while_cards_are_off(string method, string path)
    {
        using var client = Hub(organisation: null).CreateClient();

        using var response = await client.SendAsync(Send(method, path, "{}"), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rotate_needs_an_identity()
    {
        using var client = Hub().CreateClient();
        using var request = Send("POST", RotatePath, "{}");
        request.Headers.Remove("X-Forwarded-User");

        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The Hub does not know who is asking, and a card key is managed in somebody's name.",
            await DetailAsync(response));
    }

    [Fact]
    public async Task A_cross_site_rotate_is_refused()
    {
        using var client = Hub().CreateClient();

        using var response = await client.SendAsync(Send("POST", RotatePath, "{}", "cross-site"), Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("A request from another site is refused.", await DetailAsync(response));
    }

    [Fact]
    public async Task Rotate_needs_an_engine_with_card()
    {
        using var client = Hub(new FakeEngine()).CreateClient();

        using var response = await client.SendAsync(Send("POST", RotatePath, "{}"), Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("This Hub's engine cannot make cards yet.", await DetailAsync(response));
    }

    [Fact]
    public async Task An_engine_line_out_of_contract_is_a_502()
    {
        using var client = Hub(new FakeEngine { Card = true, CardKeygenOutput = "{}" }).CreateClient();

        using var response = await client.SendAsync(Send("POST", RotatePath, "{}"), Token);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Rotate_makes_a_key_and_answers_the_lines_to_publish()
    {
        using var client = Hub().CreateClient();

        using var response = await client.SendAsync(Send("POST", RotatePath, "{}"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("_perf-sentinel.example.com", body.RootElement.GetProperty("record_name").GetString());
        var key = Assert.Single(body.RootElement.GetProperty("keys").EnumerateArray());
        Assert.Equal(Fingerprint, key.GetProperty("fingerprint").GetString());
        Assert.Equal(new string('A', 43), key.GetProperty("public_key").GetString());
        Assert.Equal("1970-01-01", key.GetProperty("from").GetString());
        Assert.Equal(JsonValueKind.Null, key.GetProperty("to").ValueKind);
        Assert.Equal(JsonValueKind.Null, key.GetProperty("revoked").ValueKind);
        Assert.True(key.GetProperty("current").GetBoolean());
        Assert.Equal($"v=ps1; k={Fingerprint}; from=1970-01-01", key.GetProperty("dns").GetString());
        Assert.Contains($"\"fingerprint\": \"{Fingerprint}\"", body.RootElement.GetProperty("well_known").GetString(),
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_workspace, "data", "card-keys", new string('A', 22) + ".pk8")));
    }

    [Fact]
    public async Task Revoke_with_a_bad_date_is_a_400()
    {
        using var client = Hub().CreateClient();
        using var rotated = await client.SendAsync(Send("POST", RotatePath, "{}"), Token);

        using var response = await client.SendAsync(
            Send("POST", RevokePath, $$"""{"fingerprint":"{{Fingerprint}}","revoked":"2026-11-01"}"""), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The revocation date must lie between the key's creation and today.",
            await DetailAsync(response));
    }

    [Theory]
    [InlineData("""{"fingerprint":"ps1:AAAAAAAAAAAAAAAAAAAAAA"}""")]
    [InlineData("""{"fingerprint":"ps1:AAAAAAAAAAAAAAAAAAAAAA","revoked":null}""")]
    [InlineData("""{"fingerprint":"ps1:AAAAAAAAAAAAAAAAAAAAAA","revoked":"yesterday"}""")]
    public async Task Revoke_without_a_date_is_a_400(string body)
    {
        using var client = Hub().CreateClient();
        using var rotated = await client.SendAsync(Send("POST", RotatePath, "{}"), Token);

        using var response = await client.SendAsync(Send("POST", RevokePath, body), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Give the first day the key may have leaked.", await DetailAsync(response));
    }

    [Fact]
    public async Task Revoking_answers_the_key_with_its_date_and_an_unknown_key_is_a_404()
    {
        using var client = Hub().CreateClient();
        using var rotated = await client.SendAsync(Send("POST", RotatePath, "{}"), Token);

        using var unknown = await client.SendAsync(
            Send("POST", RevokePath, """{"fingerprint":"ps1:ZZZZZZZZZZZZZZZZZZZZZZ","revoked":"1970-01-01"}"""),
            Token);
        using var revoked = await client.SendAsync(
            Send("POST", RevokePath, $$"""{"fingerprint":"{{Fingerprint}}","revoked":"1970-01-01"}"""), Token);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        using var body = JsonDocument.Parse(await revoked.Content.ReadAsStringAsync(Token));
        var key = Assert.Single(body.RootElement.GetProperty("keys").EnumerateArray());
        Assert.Equal("1970-01-01", key.GetProperty("revoked").GetString());
        Assert.False(key.GetProperty("current").GetBoolean());
    }

    [Fact]
    public async Task Reading_the_keys_never_makes_one()
    {
        using var client = Hub().CreateClient();

        using var response = await client.SendAsync(Send("GET", KeysPath), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Empty(body.RootElement.GetProperty("keys").EnumerateArray());
        Assert.Equal("{\n  \"keys\": []\n}", body.RootElement.GetProperty("well_known").GetString());
        Assert.False(File.Exists(Path.Combine(_workspace, "reports", "card-keygen-args.txt")));
    }

    private WebApplicationFactory<Program> Hub(
        FakeEngine? engine = null,
        string? organisation = "Acme",
        Action<IWebHostBuilder>? configure = null)
    {
        var enginePath = ((engine ?? new FakeEngine { Card = true }) with { Version = "perf-sentinel 0.38.0" })
            .WriteTo(Path.Combine(_workspace, $"engine-{Guid.NewGuid():N}"));
        var hub = new HubApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
            {
                options.DatabasePath = DatabasePath;
                options.AckRelay = new AckRelayOptions { TrustIdentityHeader = true };
                options.Analysis = new AnalysisOptions
                {
                    EngineBinaryPath = enginePath,
                    ReportDirectory = Path.Combine(_workspace, "reports")
                };
                options.Cards = new CardsOptions
                {
                    Organisation = organisation,
                    Domain = organisation is null ? null : "example.com"
                };
            }));
            configure?.Invoke(builder);
        });
        _hubs.Add(hub);
        return hub;
    }

    // Access on, so the caller is the signed-in session and never the proxy header.
    private async Task<HttpResponseMessage> SignedInSendAsync(
        FakeProvider provider,
        string user,
        string method,
        string path)
    {
        var hub = Hub(configure: builder =>
        {
            provider.Configure(builder);
            builder.UseSetting("Hub:Access:Enabled", "true");
            builder.UseSetting("Hub:MetricsPort", "9464");
            builder.ConfigureServices(services =>
            {
                // The factory's clock sits in 1970, where every cookie the handlers write has already expired.
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(TimeProvider.System);
                services.PostConfigure<HubOptions>(options =>
                {
                    // Access on names every caller itself, so the proxy header is off.
                    options.AckRelay = new AckRelayOptions();
                    options.Access = new AccessOptions
                    {
                        Enabled = true,
                        Admins = ["u-admin"],
                        Grants = [new GrantOptions { Group = "/members", Project = "*", Role = "member" }]
                    };
                });
            });
        });
        using var client = FakeProvider.Client(hub);
        using var callback = await FakeProvider.SignInAsync(client, user);
        using var request = Send(method, path, "{}", "same-origin");
        request.Headers.Remove("X-Forwarded-User");
        request.Headers.Add("Cookie", FakeProvider.SessionCookie(callback));
        return await client.SendAsync(request, Token);
    }

    private static HttpRequestMessage Send(string method, string path, string? body = null, string? site = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("X-Forwarded-User", "alice");
        if (site is not null)
            request.Headers.Add("Sec-Fetch-Site", site);
        return request;
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return document.RootElement.GetProperty("detail").GetString();
    }
}
