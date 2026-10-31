using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     Every problem names its refusal with a stable snake_case code beside the unchanged English
///     detail, so the launcher can translate it. One refused call per area, access off then on.
/// </summary>
public sealed partial class ProblemCodeTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private const string CiToken = "psh_problemcodes_0123456789abcdef0123456789"; // gitleaks:allow -- synthetic test credential

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("POST", "/api/analyses", 503, "No analysis engine is configured on this Hub.",
        "analysis_engine_missing")]
    [InlineData("GET", "/api/recap?period=year", 400, "The recap query is invalid.", "invalid_recap_query")]
    [InlineData("GET", "/api/sources/test/acks", 404, "This source relays no acknowledgments.", "acks_not_relayed")]
    [InlineData("POST", "/api/actions", 403,
        "The Hub does not know who is asking, and an action is taken in somebody's name.", "unknown_caller")]
    [InlineData("PUT", "/api/labels", 403,
        "The Hub does not know who is asking, and a label is set in somebody's name.", "unknown_caller")]
    [InlineData("POST", "/api/recap/reports", 403,
        "The Hub does not know who is asking, and a report is generated in somebody's name.", "unknown_caller")]
    [InlineData("GET", "/api/cards/options", 403,
        "The Hub does not know who is asking, and a card is made in somebody's name.", "unknown_caller")]
    [InlineData("POST", "/api/cards/keys/rotate", 403,
        "The Hub does not know who is asking, and a card key is managed in somebody's name.", "unknown_caller")]
    public async Task Every_problem_carries_a_code(string method, string path, int status, string detail,
        string code)
    {
        await using var hub = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Hub:Cards:Organisation", "Acme"));
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(Request(method, path), Token);

        await AssertProblemAsync(response, status, detail, code);
    }

    [Fact]
    public async Task Every_access_problem_carries_a_code()
    {
        await using var provider = await FakeProvider.StartAsync(new Dictionary<string, string>());
        await using var hub = factory.WithWebHostBuilder(builder =>
        {
            provider.Configure(builder);
            builder.UseSetting("Hub:Access:Enabled", "true");
            builder.UseSetting("Hub:MetricsPort", "9464");
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
                options.Access = new AccessOptions
                {
                    Enabled = true,
                    Admins = ["u-admin"],
                    Groups = [new InternalGroupOptions { Name = "ci" }],
                    MachineTokens =
                    [
                        new MachineTokenOptions
                        {
                            Name = "ci", Group = "internal:ci", Token = CiToken,
                            ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(10_000).AddDays(30)
                        }
                    ]
                }));
        });
        using var client = FakeProvider.Client(hub);

        using var write = Request("POST", "/api/incidents/refresh");
        write.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CiToken);
        using var refused = await client.SendAsync(write, Token);
        using var list = Request("GET", "/api/tokens");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CiToken);
        using var tokens = await client.SendAsync(list, Token);

        await AssertProblemAsync(refused, 403, "A machine token only reads.", "machine_token_read_only");
        await AssertProblemAsync(tokens, 401, "Approving a sign-in or managing tokens needs a browser session.",
            "browser_session_required");
    }

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET")
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return request;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string detail,
        string code)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(detail, body.RootElement.GetProperty("detail").GetString());
        var served = body.RootElement.TryGetProperty("code", out var value) ? value.GetString() : null;
        Assert.Equal(code, served);
        Assert.Matches(SnakeCase(), served);
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$")]
    private static partial Regex SnakeCase();
}
