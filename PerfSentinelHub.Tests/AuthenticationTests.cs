using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PerfSentinelHub.Tests;

/// <summary>
///     The Hub signing browser users in against an OAuth2 provider of its own,
///     played here by a Kestrel that answers the token and userinfo calls.
/// </summary>
public sealed class AuthenticationTests : IAsyncLifetime
{
    private const string RelayedSignature = "slow_sql:order-svc:0123456789abcdef0123456789abcdef";
    private const string RelayBody = $$"""{"signature":"{{RelayedSignature}}","reason":"known"}""";

    // The userinfo a code stands for, for the users Hub:Auth:RequiredGroups judges.
    private static readonly Dictionary<string, string> GroupedUsers = new(StringComparer.Ordinal)
    {
        ["Bearer member"] = """{"email":"bob@example.internal","groups":[7,"/other","/hub-users"]}""",
        ["Bearer single"] = """{"email":"carol@example.internal","groups":"/hub-users"}""",
        ["Bearer outsider"] = """{"email":"dave@example.internal","groups":["/other"]}""",
        ["Bearer shouting"] = """{"email":"erin@example.internal","groups":["/HUB-USERS"]}""",
        ["Bearer nested"] = """{"email":"frank@example.internal","groups":[["/hub-users"],null]}""",
        ["Bearer malformed"] = """{"email":"grace@example.internal","groups":42}""",
        ["Bearer near"] =
            """{"email":"heidi@example.internal","groups":["/parent/hub-users","/hub-users/readers","hub-users","/hub-users "]}""",
        ["Bearer renamed"] = """{"email":"ivan@example.internal","hub_groups":["/hub-users"],"groups":["/other"]}"""
    };

    private readonly HubApplicationFactory _hub = new();
    private WebApplicationFactory<Program> _factory = null!;
    private FakeDaemon _provider = null!;

    public async ValueTask InitializeAsync()
    {
        // The code becomes the access token, so a test picks the userinfo it gets
        // back by the code it hands the callback: "anon" names nobody.
        _provider = await FakeDaemon.StartAsync(async context =>
        {
            switch (context.Request.Path.Value)
            {
                case "/token":
                    var form = await context.Request.ReadFormAsync();
                    await context.Response.WriteAsJsonAsync(
                        new Dictionary<string, string>
                        { ["access_token"] = form["code"].ToString(), ["token_type"] = "Bearer" });
                    break;
                case "/userinfo" when GroupedUsers.TryGetValue(
                    context.Request.Headers.Authorization.ToString(), out var user):
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(user);
                    break;
                case "/userinfo" when context.Request.Headers.Authorization == "Bearer anon":
                    await context.Response.WriteAsJsonAsync(new Dictionary<string, string> { ["sub"] = "2" });
                    break;
                case "/userinfo":
                    await context.Response.WriteAsJsonAsync(
                        new Dictionary<string, string> { ["sub"] = "1", ["email"] = "alice@example.internal" });
                    break;
                default:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    break;
            }
        }, TestContext.Current.CancellationToken);

        _factory = _hub.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Hub:Auth:Enabled", "true");
            builder.UseSetting("Hub:Auth:AuthorizationEndpoint", "https://idp.example.internal/authorize");
            builder.UseSetting("Hub:Auth:TokenEndpoint", new Uri(_provider.BaseUrl, "token").ToString());
            builder.UseSetting("Hub:Auth:UserInformationEndpoint", new Uri(_provider.BaseUrl, "userinfo").ToString());
            builder.UseSetting("Hub:Auth:ClientId", "hub");
            builder.UseSetting("Hub:Auth:ClientSecret", "secret"); // gitleaks:allow -- synthetic test credential
            // The factory's clock sits in 1970, where every cookie the handlers
            // write has already expired before the browser can send it back.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(TimeProvider.System);
            });
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _hub.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task A_page_sends_the_browser_to_the_provider_with_pkce()
    {
        using var client = Client();

        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith("https://idp.example.internal/authorize?", location.ToString(), StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("hub", query["client_id"]);
        Assert.Equal("https://localhost/auth/callback", query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.True(query.ContainsKey("state"));
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/sources")]
    [InlineData("/api/analyses")]
    [InlineData("/app.js")]
    public async Task What_the_launcher_reads_answers_401_or_redirects_without_a_session(string path)
    {
        using var client = Client();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // A fetch cannot follow a redirect to another origin, so the API says
        // 401 and the launcher reloads itself into the sign-in.
        Assert.Equal(
            path == "/app.js" ? HttpStatusCode.Redirect : HttpStatusCode.Unauthorized,
            response.StatusCode);
    }

    [Theory]
    [InlineData("iframe", HttpStatusCode.Unauthorized)]
    [InlineData("document", HttpStatusCode.Redirect)]
    [InlineData(null, HttpStatusCode.Redirect)]
    public async Task A_report_answers_401_in_the_launcher_frame_and_signs_in_as_a_page(
        string? destination, HttpStatusCode expected)
    {
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/reports/0123456789abcdef.html");
        if (destination is not null) request.Headers.Add("Sec-Fetch-Dest", destination);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // A shared report link opened on its own is a page: it has to reach the
        // sign-in, not stop at a bare 401.
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/findings")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/metrics")]
    public async Task Machine_routes_stay_open(string path)
    {
        using var client = Client();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_import_still_answers_to_its_key_alone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = Client();

        using var refused = await client.PostAsync("/api/import/findings?source_id=test",
            new StringContent("[]", Encoding.UTF8, "application/json"), cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Null(refused.Headers.Location);
    }

    [Fact]
    public async Task A_signed_in_user_is_the_identity_and_the_proxy_header_is_ignored()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = Client();

        using var challenge = await client.GetAsync("/", cancellationToken);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"];
        using var callback = await client.GetAsync(
            $"/auth/callback?code=abc&state={Uri.EscapeDataString(state!)}", cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add("X-Forwarded-User", "mallory@example.internal");
        using var status = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var payload = await status.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("alice@example.internal", payload.GetProperty("identity").GetString());
    }

    [Theory]
    [InlineData("error=access_denied")]
    [InlineData("code=anon")]
    public async Task A_refused_or_nameless_sign_in_answers_403_rather_than_failing(string outcome)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = Client();

        using var challenge = await client.GetAsync("/", cancellationToken);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"];
        using var callback = await client.GetAsync(
            $"/auth/callback?{outcome}&state={Uri.EscapeDataString(state!)}", cancellationToken);

        // Cancelling on the provider's consent screen, or a userinfo without the
        // configured field, is not a Hub fault. No redirect either: it would send
        // the user straight back to the screen they just cancelled.
        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.Null(callback.Headers.Location);
        Assert.Contains("Sign-in refused", await callback.Content.ReadAsStringAsync(cancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replayed_callback_points_at_the_hub_rather_than_at_a_reload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = Client();

        using var challenge = await client.GetAsync("/", cancellationToken);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"];
        var callbackPath = $"/auth/callback?error=access_denied&state={Uri.EscapeDataString(state!)}";
        using var first = await client.GetAsync(callbackPath, cancellationToken);
        using var replay = await client.GetAsync(callbackPath, cancellationToken);

        // The correlation cookie is spent by the first answer, so reloading the
        // callback can never succeed: the line has to send the user home.
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        var line = await replay.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("home page", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Reload", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_ack_relay_needs_a_session_and_acks_in_the_name_it_carries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = new ConcurrentQueue<AckRelayApiTests.DaemonCall>();
        await using var daemon = await AckRelayApiTests.DaemonAsync(calls, StatusCodes.Status201Created);
        // The header is trusted here, and the session still outranks it.
        await using var relaying = AckRelayApiTests.Scoped(
            _factory, true, TimeSpan.FromSeconds(10), AckRelayApiTests.Relayed("signed", daemon));
        await AckRelayApiTests.SeedAsync(_hub.Database, "signed", RelayedSignature);
        using var client = Client(relaying);

        using var refused = await client.SendAsync(
            AckRelayApiTests.Relay("signed", RelayBody, "mallory@example.internal"), cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Empty(calls);

        using var challenge = await client.GetAsync("/", cancellationToken);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"];
        using var callback = await client.GetAsync(
            $"/auth/callback?code=abc&state={Uri.EscapeDataString(state!)}", cancellationToken);
        using var relayed = await client.SendAsync(
            AckRelayApiTests.Relay("signed", RelayBody, "mallory@example.internal"), cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, relayed.StatusCode);
        using var sent = JsonDocument.Parse(Assert.Single(calls, call => call.Method != "GET").Body);
        Assert.Equal("alice@example.internal", sent.RootElement.GetProperty("by").GetString());
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.NoContent)]
    public async Task Without_sign_in_the_proxy_header_names_the_caller_only_once_the_relay_opts_in(
        bool trustIdentityHeader,
        HttpStatusCode expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceId = $"header-{trustIdentityHeader}";
        var calls = new ConcurrentQueue<AckRelayApiTests.DaemonCall>();
        await using var daemon = await AckRelayApiTests.DaemonAsync(calls, StatusCodes.Status201Created);
        // Off the plain factory: under Hub:Auth a request with no session never gets this far.
        await using var relaying = AckRelayApiTests.Scoped(
            _hub, trustIdentityHeader, TimeSpan.FromSeconds(10), AckRelayApiTests.Relayed(sourceId, daemon));
        await AckRelayApiTests.SeedAsync(_hub.Database, sourceId, RelayedSignature);
        using var client = relaying.CreateClient();

        using var response = await client.SendAsync(
            AckRelayApiTests.Relay(sourceId, RelayBody, "alice@example.internal"), cancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(trustIdentityHeader ? 1 : 0, calls.Count(call => call.Method != "GET"));
    }

    [Theory]
    [InlineData("member", "bob@example.internal")]
    [InlineData("single", "carol@example.internal")]
    public async Task A_member_of_a_required_group_is_let_in(string code, string identity)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var gated = Gated(new ListLogger<Program>());
        using var client = Client(gated);

        using var callback = await SignInAsync(client, code);
        using var status = await client.GetAsync("/api/status", cancellationToken);

        // A non-string item is skipped rather than failing the whole list, and
        // a provider may send a lone group as a bare string.
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var payload = await status.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(identity, payload.GetProperty("identity").GetString());
    }

    [Theory]
    [InlineData("outsider", "dave@example.internal is in none of Hub:Auth:RequiredGroups")]
    [InlineData("shouting", "erin@example.internal is in none of Hub:Auth:RequiredGroups")]
    [InlineData("nested", "frank@example.internal is in none of Hub:Auth:RequiredGroups")]
    [InlineData("malformed", "grace@example.internal is in none of Hub:Auth:RequiredGroups")]
    [InlineData("near", "heidi@example.internal is in none of Hub:Auth:RequiredGroups")]
    [InlineData("abc", "userinfo for alice@example.internal lacks the Hub:Auth:GroupsClaim field")]
    public async Task A_user_outside_the_required_groups_is_refused(string code, string reason)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logger = new ListLogger<Program>();
        await using var gated = Gated(logger);
        using var client = Client(gated);

        using var callback = await SignInAsync(client, code);
        using var status = await client.GetAsync("/api/status", cancellationToken);

        // The same line for every refusal: who is outside a group is for the
        // operator's log, not for whoever holds the callback URL.
        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.Equal(
            "Sign-in refused. Open the Hub's home page to try again, or ask the Hub's administrator for access.",
            await callback.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        var refusal = Assert.Single(logger.Messages, message =>
            message.StartsWith("Sign-in refused", StringComparison.Ordinal));
        Assert.Contains(reason, refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("/other", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_required_groups_the_groups_field_is_never_read_and_the_session_slides()
    {
        using var client = Client();

        using var callback = await SignInAsync(client, "malformed");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var session = CookieOptions(_factory);
        Assert.True(session.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(8), session.ExpireTimeSpan);
    }

    [Fact]
    public async Task With_required_groups_the_session_ends_eight_hours_after_sign_in()
    {
        await using var gated = Gated(new ListLogger<Program>());

        // Membership is read at sign-in only, so a session that slid would keep
        // a member removed at the provider in for as long as they kept using it.
        var session = CookieOptions(gated);
        Assert.False(session.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(8), session.ExpireTimeSpan);
    }

    [Theory]
    [InlineData("renamed", HttpStatusCode.Redirect)]
    [InlineData("member", HttpStatusCode.Forbidden)]
    public async Task The_groups_are_read_from_the_field_GroupsClaim_names(string code, HttpStatusCode expected)
    {
        await using var gated = Gated(new ListLogger<Program>(), groupsClaim: "hub_groups");
        using var client = Client(gated);

        using var callback = await SignInAsync(client, code);

        // "renamed" lists /hub-users under hub_groups only, "member" under groups only.
        Assert.Equal(expected, callback.StatusCode);
    }

    [Theory]
    [InlineData(null, "outsider", "/hub-admins,/hub-users", HttpStatusCode.Unauthorized)]
    [InlineData("/hub-admins,/hub-users", "member", "/hub-users,/hub-admins", HttpStatusCode.OK)]
    [InlineData("/hub-admins,/hub-users", "member", "/hub-admins", HttpStatusCode.Unauthorized)]
    public async Task A_session_is_refused_once_its_group_is_no_longer_required(
        string? before, string code, string after, HttpStatusCode expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var issuing = before is null ? null : Gated(new ListLogger<Program>(), before);
        using var signIn = Client(issuing);
        using var callback = await SignInAsync(signIn, code);
        var session = callback.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith("hub_session=", StringComparison.Ordinal)).Split(';')[0];

        await using var restarted = Gated(new ListLogger<Program>(), after);
        using var client = Client(restarted);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add("Cookie", session);
        using var status = await client.SendAsync(request, cancellationToken);

        // The keys outlive a restart: without the check, a session opened before
        // the gate, or before its group was dropped, would last up to 8 hours more.
        Assert.Equal(expected, status.StatusCode);
    }

    private WebApplicationFactory<Program> Gated(
        ListLogger<Program> logger, string groups = "/hub-admins,/hub-users", string? groupsClaim = null)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            foreach (var (group, index) in groups.Split(',').Select((group, index) => (group, index)))
                builder.UseSetting($"Hub:Auth:RequiredGroups:{index}", group);
            if (groupsClaim is not null) builder.UseSetting("Hub:Auth:GroupsClaim", groupsClaim);
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(new Capture(logger)));
        });
    }

    private static CookieAuthenticationOptions CookieOptions(WebApplicationFactory<Program> factory)
    {
        return factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string code)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var challenge = await client.GetAsync("/", cancellationToken);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"];
        return await client.GetAsync(
            $"/auth/callback?code={code}&state={Uri.EscapeDataString(state!)}", cancellationToken);
    }

    private HttpClient Client(WebApplicationFactory<Program>? factory = null)
    {
        // https: the session and correlation cookies are Secure, and the cookie
        // container would not send them back over http.
        return (factory ?? _factory).CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
    }

    private sealed class Capture(ListLogger<Program> logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return logger;
        }

        public void Dispose()
        {
        }
    }
}
