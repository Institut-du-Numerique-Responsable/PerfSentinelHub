using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace PerfSentinelHub.Tests;

/// <summary>
///     An OAuth2 provider whose code is the access token, and whose userinfo is
///     whatever JSON the test mapped to that code. Signing in with a code the map
///     lacks answers a userinfo naming nobody.
/// </summary>
internal sealed class FakeProvider : IAsyncDisposable
{
    private readonly FakeDaemon _server;

    private FakeProvider(FakeDaemon server)
    {
        _server = server;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    public static async Task<FakeProvider> StartAsync(IReadOnlyDictionary<string, string> users)
    {
        return new FakeProvider(await FakeDaemon.StartAsync(async context =>
        {
            switch (context.Request.Path.Value)
            {
                case "/token":
                    var form = await context.Request.ReadFormAsync();
                    await context.Response.WriteAsJsonAsync(
                        new Dictionary<string, string>
                        { ["access_token"] = form["code"].ToString(), ["token_type"] = "Bearer" });
                    break;
                case "/userinfo":
                    var code = context.Request.Headers.Authorization.ToString()["Bearer ".Length..];
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(users.TryGetValue(code, out var user) ? user : "{}");
                    break;
                default:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    break;
            }
        }, TestContext.Current.CancellationToken));
    }

    public void Configure(IWebHostBuilder builder)
    {
        builder.UseSetting("Hub:Auth:Enabled", "true");
        builder.UseSetting("Hub:Auth:AuthorizationEndpoint", "https://idp.example.internal/authorize");
        builder.UseSetting("Hub:Auth:TokenEndpoint", new Uri(_server.BaseUrl, "token").ToString());
        builder.UseSetting("Hub:Auth:UserInformationEndpoint", new Uri(_server.BaseUrl, "userinfo").ToString());
        builder.UseSetting("Hub:Auth:ClientId", "hub");
        builder.UseSetting("Hub:Auth:ClientSecret", "secret"); // gitleaks:allow -- synthetic test credential
    }

    // https: the session and correlation cookies are Secure.
    public static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
    }

    public static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string code)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var challenge = await client.GetAsync("/", cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"];
        return await client.GetAsync(
            $"/auth/callback?code={code}&state={Uri.EscapeDataString(state!)}", cancellationToken);
    }

    // The session cookie a sign-in set, to replay by hand against another Hub or clock.
    public static string SessionCookie(HttpResponseMessage callback)
    {
        return callback.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith("hub_session=", StringComparison.Ordinal)).Split(';')[0];
    }
}
