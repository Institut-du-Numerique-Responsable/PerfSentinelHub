using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

/// <summary>
///     Terminal sign-in: the device flow the CLI drives, the approval page a signed-in
///     browser confirms it on, and the personal tokens it hands out. Every request
///     reaches the Hub from 127.0.0.1, the trusted proxy, with the client's own
///     address in X-Forwarded-For.
/// </summary>
public sealed class TerminalAccessApiTests : IAsyncLifetime
{
    private const string Here = "203.0.113.7";
    private const string Elsewhere = "198.51.100.1";

    private const string
        CiToken = "ci-terminal-0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential

    private const string Peer = "X-Test-Peer";

    private static readonly Dictionary<string, string> Users = new(StringComparer.Ordinal)
    {
        ["member"] = """{"sub":"u-member","email":"member@example.internal","groups":["/teams/shop"]}""",
        ["other"] = """{"sub":"u-other","email":"other@example.internal","groups":["/teams/shop"]}""",
        ["admin"] = """{"sub":"u-admin","email":"admin@example.internal","groups":["/hub-admins","/teams/shop"]}""",
        ["lab"] = """{"sub":"u-lab","email":"lab@example.internal","groups":["/teams/lab"]}""",
        ["forger"] = """{"sub":"u-forger","email":"forger\r\nforged line","groups":["/teams/shop"]}"""
    };

    private static readonly (HttpMethod Method, string Path)[] Routes =
    [
        (HttpMethod.Post, "/auth/device"),
        (HttpMethod.Post, "/auth/device/token"),
        (HttpMethod.Post, "/auth/revoke"),
        (HttpMethod.Get, "/device"),
        (HttpMethod.Post, "/api/device/verify"),
        (HttpMethod.Post, "/api/device/approve"),
        (HttpMethod.Get, "/api/tokens"),
        (HttpMethod.Delete, "/api/tokens/0123456789abcdef0123456789abcdef")
    ];

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly HubApplicationFactory _hub = new();
    private readonly ListLogger<Program> _log = new();
    private FakeProvider _provider = null!;

    private WebApplicationFactory<Program> Factory { get; set; } = null!;

    private HubDatabase Database => Factory.Services.GetRequiredService<HubDatabase>();

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync(Users);
        Factory = _hub.WithWebHostBuilder(builder =>
        {
            _provider.Configure(builder);
            builder.UseSetting("Hub:Access:Enabled", "true");
            builder.UseSetting("Hub:MetricsPort", "9464");
            builder.UseSetting("Hub:Auth:TrustedProxyNetworks:0", "127.0.0.1/32");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
                services.AddSingleton<IStartupFilter, FromTheProxy>();
                services.AddSingleton<ILoggerProvider>(new Capture(_log));
                services.PostConfigure<HubOptions>(options => options.Access = new AccessOptions
                {
                    Enabled = true,
                    AdminGroups = ["/hub-admins"],
                    Groups = [new InternalGroupOptions { Name = "ci" }],
                    Projects =
                    [
                        new ProjectOptions { Name = "lab", Patterns = [new PatternOptions { Source = "test" }] }
                    ],
                    Grants =
                    [
                        new GrantOptions { Group = "/teams/shop", Project = "*", Role = "member" },
                        new GrantOptions { Group = "/teams/lab", Project = "lab", Role = "reader" }
                    ],
                    MachineTokens =
                    [
                        new MachineTokenOptions
                        {
                            Name = "ci", Group = "internal:ci", Token = CiToken,
                            ExpiresAt = _clock.GetUtcNow().AddDays(30)
                        }
                    ]
                });
            });
        });
        await Factory.Services.GetRequiredService<HubDatabase>().InitializeAsync(Cancel);
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _hub.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task With_auth_off_every_new_route_answers_404()
    {
        using var client = _hub.CreateClient();

        foreach (var (method, path) in Routes)
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, Cancel);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task With_auth_on_and_access_off_the_new_routes_answer_as_unmapped_ones_do()
    {
        await using var hub = _hub.WithWebHostBuilder(builder =>
        {
            _provider.Configure(builder);
            builder.UseSetting("Hub:Auth:TrustedProxyNetworks:0", "127.0.0.1/32");
        });
        using var client = FakeProvider.Client(hub);

        using var device = await FormAsync(client, "/auth/device", "client_id=perf-sentinel");
        using var tokens = await client.GetAsync("/api/tokens", Cancel);

        Assert.Equal(HttpStatusCode.Redirect, device.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, tokens.StatusCode);
    }

    [Fact]
    public async Task Without_trusted_proxy_networks_the_device_routes_are_unmapped_while_tokens_and_revoke_answer()
    {
        await using var hub = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<HubOptions>(options =>
                options.Auth = options.Auth with { TrustedProxyNetworks = [] })));
        using var cli = FakeProvider.Client(hub);
        using var browser = FakeProvider.Client(hub);
        using (var callback = await FakeProvider.SignInAsync(browser, "member"))
        {
            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        }

        using var device = await FormAsync(cli, "/auth/device", "client_id=perf-sentinel");
        using var poll = await FormAsync(cli, "/auth/device/token", "client_id=perf-sentinel");
        using var page = await browser.GetAsync("/device", Cancel);
        using var anonymous = await cli.GetAsync("/api/tokens", Cancel);
        using var tokens = await browser.GetAsync("/api/tokens", Cancel);
        using var revoke = await FormAsync(cli, "/auth/revoke", "token=pshub_nothing");

        Assert.Equal(HttpStatusCode.Redirect, device.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, poll.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, tokens.StatusCode);
        Assert.Equal("[]", await tokens.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
    }

    [Fact]
    public async Task The_device_answer_offers_no_complete_uri_and_is_never_stored()
    {
        using var cli = Cli(Here);

        using var response = await FormAsync(cli, "/auth/device", "client_id=perf-sentinel&scope=ignored");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement;
        using var pending = await FormAsync(cli, "/auth/device/token",
            "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code&client_id=perf-sentinel&device_code="
            + Uri.EscapeDataString(body.GetProperty("device_code").GetString()!));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            ["device_code", "user_code", "verification_uri", "expires_in", "interval"],
            body.EnumerateObject().Select(property => property.Name));
        Assert.Matches("^[BCDFGHJKLMNPQRSTVWXZ]{4}-[BCDFGHJKLMNPQRSTVWXZ]{4}$",
            body.GetProperty("user_code").GetString());
        Assert.Equal("https://localhost/device", body.GetProperty("verification_uri").GetString());
        Assert.Equal(600, body.GetProperty("expires_in").GetInt32());
        Assert.Equal(5, body.GetProperty("interval").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, pending.StatusCode);
        Assert.Equal("no-store", pending.Headers.CacheControl?.ToString());
        Assert.Equal("""{"error":"authorization_pending"}""", await pending.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task The_RFC_routes_answer_their_errors_in_the_OAuth_shape()
    {
        using var cli = Cli(Here);
        var start = await StartAsync(cli);
        var device = Uri.EscapeDataString(start.GetProperty("device_code").GetString()!);

        Assert.Equal("invalid_client", await OAuthErrorAsync(cli, "/auth/device", "client_id=other"));
        Assert.Equal("invalid_request", await OAuthErrorAsync(cli, "/auth/device", ""));
        Assert.Equal("invalid_request",
            await OAuthErrorAsync(cli, "/auth/device", "client_id=perf-sentinel&client_id=perf-sentinel"));
        Assert.Equal("invalid_request",
            await OAuthErrorAsync(cli, "/auth/device/token", $"client_id=perf-sentinel&device_code={device}"));
        Assert.Equal("unsupported_grant_type",
            await OAuthErrorAsync(cli, "/auth/device/token",
                $"grant_type=password&client_id=perf-sentinel&device_code={device}"));
        Assert.Equal("invalid_grant", await OAuthErrorAsync(cli, "/auth/device/token", Poll("unknown")));
        Assert.Equal("invalid_request", await OAuthErrorAsync(cli, "/auth/revoke", "token_type_hint=access_token"));
        using (var json = await cli.PostAsync("/auth/device",
                   new StringContent("""{"client_id":"perf-sentinel"}""", Encoding.UTF8, "application/json"), Cancel))
        {
            Assert.Equal("""{"error":"invalid_request"}""", await json.Content.ReadAsStringAsync(Cancel));
        }

        using (var large = await FormAsync(cli, "/auth/device", "client_id=perf-sentinel&pad=" + new string('a', 4096)))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        }
    }

    [Fact]
    public async Task Eleven_starts_from_one_address_hit_the_cap_with_retry_after_a_minute()
    {
        using var cli = Cli(Here);
        for (var index = 0; index < 10; index++)
            await StartAsync(cli);

        using var refused = await FormAsync(cli, "/auth/device", "client_id=perf-sentinel");
        using var elsewhere = await FormAsync(Cli(Elsewhere), "/auth/device", "client_id=perf-sentinel");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("60", refused.Headers.RetryAfter?.ToString());
        Assert.Equal("""{"error":"temporarily_unavailable"}""", await refused.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
    }

    [Fact]
    public async Task The_page_is_served_only_by_GET_device_with_its_own_policy_and_never_stored()
    {
        using var browser = await SignedInAsync("member", Here);

        using var page = await browser.GetAsync("/device?code=BCDFGHJK", Cancel);
        var html = await page.Content.ReadAsStringAsync(Cancel);
        using var file = await browser.GetAsync("/device.html", Cancel);
        using var script = await browser.GetAsync("/device.js", Cancel);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
        Assert.Equal("default-src 'self'; font-src 'self' data:; frame-ancestors 'none'",
            string.Join(';', page.Headers.GetValues("Content-Security-Policy")));
        Assert.Contains("Enter the code your terminal shows.", html, StringComparison.Ordinal);
        Assert.Contains("maxlength=\"9\"", html, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"off\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("BCDFGHJK", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
    }

    [Fact]
    public async Task A_browser_is_refused_on_the_RFC_routes()
    {
        using var cli = Cli(Here);
        cli.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");

        foreach (var path in new[] { "/auth/device", "/auth/device/token", "/auth/revoke" })
        {
            using var response = await FormAsync(cli, path, "client_id=perf-sentinel");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_bearer_alone_approves_nothing_and_manages_no_token()
    {
        using var browser = await SignedInAsync("member", Here);
        var token = await IssueAsync(browser);
        using var cli = Cli(Here);
        var start = await StartAsync(cli);
        using var bearer = Cli(Here);
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var approve = await bearer.PostAsync("/api/device/approve", Json(Decision(start, true)), Cancel);
        using var verify = await bearer.PostAsync("/api/device/verify", Json(Code(start)), Cancel);
        using var list = await bearer.GetAsync("/api/tokens", Cancel);
        using var page = await bearer.GetAsync("/device", Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, approve.StatusCode);
        Assert.Contains("needs a browser session", await approve.Content.ReadAsStringAsync(Cancel),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
    }

    [Fact]
    public async Task A_machine_token_gets_403_on_the_cookie_only_writes_and_401_on_the_token_list()
    {
        using var browser = await SignedInAsync("member", Here);
        await IssueAsync(browser);
        var id = Assert.Single((await TokensAsync(browser)).EnumerateArray()).GetProperty("id").GetString()!;
        var start = await StartAsync(Cli(Here));
        using var machine = Bearer(CiToken);

        using var approve = await machine.PostAsync("/api/device/approve", Json(Decision(start, true)), Cancel);
        using var verify = await machine.PostAsync("/api/device/verify", Json(Code(start)), Cancel);
        using var delete = await machine.DeleteAsync("/api/tokens/" + id, Cancel);
        using var list = await machine.GetAsync("/api/tokens", Cancel);

        foreach (var write in new[] { approve, verify, delete })
        {
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
            Assert.Contains("A machine token only reads.", await write.Content.ReadAsStringAsync(Cancel),
                StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Single((await TokensAsync(browser)).EnumerateArray());
    }

    [Fact]
    public async Task X_Forwarded_For_is_ignored_from_a_peer_outside_the_trusted_networks()
    {
        using var browser = await SignedInAsync("member", Here);
        using var direct = Cli(Here);
        direct.DefaultRequestHeaders.Add(Peer, "192.0.2.50");
        using var mapped = Cli(Elsewhere);
        mapped.DefaultRequestHeaders.Add(Peer, "::ffff:127.0.0.1");

        var forged = await VerifyAsync(browser, Code(await StartAsync(direct)));
        var honoured = await VerifyAsync(browser, Code(await StartAsync(mapped)));

        Assert.Equal("192.0.2.50", forged.GetProperty("client_address").GetString());
        Assert.False(forged.GetProperty("same_network").GetBoolean());
        Assert.Equal(Elsewhere, honoured.GetProperty("client_address").GetString());
    }

    [Fact]
    public async Task One_IPv6_64_is_one_address_for_the_cap_and_the_network_check()
    {
        for (var host = 1; host <= 10; host++)
            await StartAsync(Cli($"2001:db8:1:2::{host:x}"));
        using var refused = await FormAsync(Cli("2001:db8:1:2::b"), "/auth/device", "client_id=perf-sentinel");
        using var browser = await SignedInAsync("member", "2001:db8:1:2::ff");
        var start = await StartAsync(Cli("2001:db8:1:3::1"));
        using var neighbour = await SignedInAsync("member", "2001:db8:1:3::ff");

        var fromNeighbour = await VerifyAsync(neighbour, Code(start));
        var fromBrowser = await VerifyAsync(browser, Code(start));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("2001:db8:1:3::/64", fromNeighbour.GetProperty("client_address").GetString());
        Assert.True(fromNeighbour.GetProperty("same_network").GetBoolean());
        Assert.False(fromBrowser.GetProperty("same_network").GetBoolean());
    }

    [Fact]
    public async Task A_same_site_approval_is_403_and_a_form_body_415()
    {
        using var browser = await SignedInAsync("member", Here);
        var start = await StartAsync(Cli(Here));

        using var sameSite = new HttpRequestMessage(HttpMethod.Post, "/api/device/approve")
        { Content = Json(Decision(start, true)) };
        sameSite.Headers.Add("Sec-Fetch-Site", "same-site");
        using var sameSiteResponse = await browser.SendAsync(sameSite, Cancel);
        using var form = new HttpRequestMessage(HttpMethod.Post, "/api/device/approve")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("user_code", "x")])
        };
        form.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var formResponse = await browser.SendAsync(form, Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, sameSiteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, formResponse.StatusCode);
    }

    [Fact]
    public async Task With_less_than_an_hour_left_the_page_challenges_and_approval_is_409()
    {
        using var browser = await SignedInAsync("member", Here);
        var start = await StartAsync(Cli(Here));

        _clock.Advance(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(1));
        using var page = await browser.GetAsync("/device", Cancel);
        using var approve = await BrowserPostAsync(browser, "/api/device/approve", Decision(start, true));

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("https://idp.example.internal/authorize", page.Headers.Location!.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
    }

    [Fact]
    public async Task Verify_names_the_requesting_address_and_whether_it_is_this_browsers_network()
    {
        var signedInAt = NowMs;
        using var here = await SignedInAsync("member", Here);
        using var elsewhere = await SignedInAsync("member", Elsewhere);
        using var cli = Cli(Here);
        var start = await StartAsync(cli);
        var requestedAt = NowMs;
        _clock.Advance(TimeSpan.FromSeconds(30));

        var fromHere = await VerifyAsync(here, Code(start));
        var fromElsewhere = await VerifyAsync(elsewhere, Code(start));
        using var unknown = await BrowserPostAsync(here, "/api/device/verify", """{"user_code":"BCDF-BCDF"}""");
        using var malformed = await BrowserPostAsync(here, "/api/device/verify", """{"user_code":"AAAA-AAAA"}""");

        Assert.Equal(start.GetProperty("user_code").GetString(), fromHere.GetProperty("user_code").GetString());
        Assert.Equal(Here, fromHere.GetProperty("client_address").GetString());
        Assert.True(fromHere.GetProperty("same_network").GetBoolean());
        Assert.Equal(requestedAt, fromHere.GetProperty("requested_at_ms").GetInt64());
        Assert.Equal("member@example.internal", fromHere.GetProperty("identity").GetString());
        Assert.InRange(fromHere.GetProperty("token_expires_at_ms").GetInt64(),
            signedInAt + 8 * 3_600_000 - 1000, signedInAt + 8 * 3_600_000 + 1000);
        Assert.False(fromElsewhere.GetProperty("same_network").GetBoolean());
        Assert.Equal(Here, fromElsewhere.GetProperty("client_address").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task Ten_failed_codes_answer_429_until_the_window_ends()
    {
        using var browser = await SignedInAsync("member", Here);
        for (var index = 0; index < 10; index++)
        {
            // Approve counts as verify does, so neither probes codes unthrottled.
            using var failed = index % 2 == 0
                ? await BrowserPostAsync(browser, "/api/device/approve", """{"user_code":"BCDF-BCDF","approve":true}""")
                : await BrowserPostAsync(browser, "/api/device/verify", """{"user_code":"BCDF-BCDF"}""");
            Assert.Equal(HttpStatusCode.NotFound, failed.StatusCode);
        }

        var start = await StartAsync(Cli(Here));
        using var refused = await BrowserPostAsync(browser, "/api/device/approve", Decision(start, true));
        using var verify = await BrowserPostAsync(browser, "/api/device/verify", Code(start));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("600", refused.Headers.RetryAfter?.ToString());
        Assert.Equal(HttpStatusCode.TooManyRequests, verify.StatusCode);
    }

    [Fact]
    public async Task A_denied_sign_in_hands_out_no_token()
    {
        using var browser = await SignedInAsync("member", Here);
        using var cli = Cli(Here);
        var start = await StartAsync(cli);

        using var deny = await BrowserPostAsync(browser, "/api/device/approve", Decision(start, false));

        Assert.Equal(HttpStatusCode.NoContent, deny.StatusCode);
        Assert.Equal("access_denied", await OAuthErrorAsync(cli, "/auth/device/token", Poll(start)));
        Assert.Equal("invalid_grant", await OAuthErrorAsync(cli, "/auth/device/token", Poll(start)));
    }

    [Fact]
    public async Task A_terminal_token_lives_as_long_as_the_approving_session_reads_findings_and_dies_with_revoke()
    {
        using var browser = await SignedInAsync("member", Here);
        var session = await AccessAsync(browser);
        using var cli = Cli(Here);
        var start = await StartAsync(cli);
        using (var approve = await BrowserPostAsync(browser, "/api/device/approve", Decision(start, true)))
        {
            Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        }

        using var issued = await FormAsync(cli, "/auth/device/token", Poll(start));
        var answer = JsonDocument.Parse(await issued.Content.ReadAsStringAsync(Cancel)).RootElement;
        var token = answer.GetProperty("access_token").GetString()!;
        using var bearer = Bearer(token);
        var access = await AccessAsync(bearer);
        using var findings = await bearer.GetAsync("/api/findings", Cancel);
        var listed = Assert.Single((await TokensAsync(browser)).EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        Assert.Equal("no-store", issued.Headers.CacheControl?.ToString());
        Assert.Equal(["access_token", "token_type", "expires_in"],
            answer.EnumerateObject().Select(property => property.Name));
        Assert.Matches("^pshub_[A-Za-z0-9_-]{43}$", token);
        Assert.Equal("Bearer", answer.GetProperty("token_type").GetString());
        var expiresAtMs = session.GetProperty("expires_at_ms").GetInt64();
        Assert.Equal((expiresAtMs - NowMs) / 1000, answer.GetProperty("expires_in").GetInt64());
        Assert.Equal(expiresAtMs, access.GetProperty("expires_at_ms").GetInt64());
        Assert.Equal(expiresAtMs, listed.GetProperty("expires_at_ms").GetInt64());
        Assert.Equal("terminal_token", access.GetProperty("caller").GetString());
        Assert.Equal("u-member", access.GetProperty("subject").GetString());
        Assert.Equal("member@example.internal", access.GetProperty("identity").GetString());
        Assert.Equal("""[{"name":"/teams/shop","origin":"provider"}]""", access.GetProperty("groups").GetRawText());
        Assert.Equal(HttpStatusCode.OK, findings.StatusCode);
        Assert.Equal(Here, listed.GetProperty("client_address").GetString());
        Assert.Equal("u-member", listed.GetProperty("subject").GetString());
        Assert.Equal(
            ["id", "identity", "subject", "client_address", "created_at_ms", "expires_at_ms", "last_used_at_ms"],
            listed.EnumerateObject().Select(property => property.Name));

        using (var revoke = await FormAsync(cli, "/auth/revoke", "token=" + token + "&token_type_hint=access_token"))
        {
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }

        using (var again = await FormAsync(cli, "/auth/revoke", "token=" + token))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        using var after = await bearer.GetAsync("/api/findings", Cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task A_terminal_token_reaches_only_its_frozen_grants_and_writes_under_its_identity()
    {
        var reports = Directory.CreateTempSubdirectory("perf-sentinel-hub-terminal-").FullName;
        try
        {
            await using var hub = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                foreach (var worker in services.Where(descriptor =>
                             descriptor.ServiceType == typeof(IHostedService) &&
                             descriptor.ImplementationType == typeof(AnalysisWorker)).ToArray())
                    services.Remove(worker);
                services.PostConfigure<HubOptions>(options =>
                {
                    options.Analysis = new AnalysisOptions
                    { EngineBinaryPath = "/usr/bin/false", ReportDirectory = reports };
                    options.Sources =
                    [
                        .. options.Sources,
                        new SourceOptions
                        {
                            Id = "tempo", Name = "Tempo", Environment = "prod", Kind = SourceKinds.Tempo,
                            BaseUrl = new Uri("http://127.0.0.1:2")
                        }
                    ];
                });
            }));
            using var lab = Bearer(await IssueAsync(await SignedInAsync("lab", Here, hub), hub), hub);
            using var member = Bearer(await IssueAsync(await SignedInAsync("member", Here, hub), hub), hub);
            const string Run = """{"source_id":"tempo","request":{"service":"checkout","lookback":"1h"}}""";

            var access = await AccessAsync(lab);
            using var labRun = await lab.PostAsync("/api/analyses", Json(Run), Cancel);
            using var machineRun = await Bearer(CiToken, hub).PostAsync("/api/analyses", Json(Run), Cancel);
            using var memberRun = await member.PostAsync("/api/analyses", Json(Run), Cancel);
            var id = JsonDocument.Parse(await memberRun.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("id")
                .GetString();
            using var run = await member.GetAsync("/api/analyses/" + id, Cancel);

            Assert.Equal(
                """[{"group":"/teams/lab","project":"lab","role":"reader","permissions":["findings.read","traces.read","analyses.run"]}]""",
                access.GetProperty("reach").GetRawText());
            Assert.Equal(HttpStatusCode.BadRequest, labRun.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, machineRun.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, memberRun.StatusCode);
            Assert.Equal("member@example.internal",
                JsonDocument.Parse(await run.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("requested_by")
                    .GetString());
        }
        finally
        {
            Directory.Delete(reports, true);
        }
    }

    [Fact]
    public async Task An_identity_with_a_control_character_is_stored_and_logged_as_the_subject()
    {
        using var browser = await SignedInAsync("forger", Here);
        await IssueAsync(browser);

        var listed = Assert.Single((await TokensAsync(browser)).EnumerateArray());

        Assert.Equal("u-forger", listed.GetProperty("identity").GetString());
        Assert.Contains(_log.Messages,
            message => message.StartsWith("Terminal sign-in approved by u-forger (u-forger)",
                StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Messages, message => message.Contains("forged line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_approval_is_logged_when_given_and_its_token_when_issued()
    {
        using var browser = await SignedInAsync("member", Here);
        using var cli = Cli(Here);
        var start = await StartAsync(cli);

        using (var approve = await BrowserPostAsync(browser, "/api/device/approve", Decision(start, true)))
        {
            Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        }

        var approved = Assert.Single(_log.Messages,
            message => message.StartsWith("Terminal sign-in approved", StringComparison.Ordinal));
        Assert.Equal(
            $"Terminal sign-in approved by member@example.internal (u-member) for {Here}, from another network: False.",
            approved);
        Assert.DoesNotContain(_log.Messages, message => message.StartsWith("Terminal token", StringComparison.Ordinal));

        using (var issued = await FormAsync(cli, "/auth/device/token", Poll(start)))
        {
            Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        }

        var id = Assert.Single((await TokensAsync(browser)).EnumerateArray()).GetProperty("id").GetString();
        Assert.Contains($"Terminal token {id} issued to {Here} for member@example.internal (u-member).", _log.Messages);
    }

    [Fact]
    public async Task A_token_approved_by_an_admin_is_not_admin()
    {
        using var browser = await SignedInAsync("admin", Here);
        Assert.True((await AccessAsync(browser)).GetProperty("admin").GetBoolean());

        using var bearer = Bearer(await IssueAsync(browser));
        var access = await AccessAsync(bearer);
        using var tokens = await bearer.GetAsync("/api/tokens", Cancel);

        Assert.False(access.GetProperty("admin").GetBoolean());
        Assert.Equal(JsonValueKind.Null, access.GetProperty("configuration").ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, tokens.StatusCode);
    }

    [Fact]
    public async Task Another_users_token_is_404_to_a_user_and_revocable_by_an_admin()
    {
        using var member = await SignedInAsync("member", Here);
        using var other = await SignedInAsync("other", Here);
        using var admin = await SignedInAsync("admin", Here);
        var token = await IssueAsync(member);
        var own = await IssueAsync(other);
        var id = Assert.Single((await TokensAsync(member)).EnumerateArray()).GetProperty("id").GetString()!;
        var ownId = Assert.Single((await TokensAsync(other)).EnumerateArray()).GetProperty("id").GetString()!;

        using var refused = await DeleteAsync(other, id);
        using var malformed = await DeleteAsync(other, "not-an-id");
        Assert.Equal(2, (await TokensAsync(admin)).GetArrayLength());
        using var revoked = await DeleteAsync(admin, id);
        using var gone = await DeleteAsync(admin, id);
        using var ownRevoked = await DeleteAsync(other, ownId);
        using var after = await Bearer(token).GetAsync("/api/findings", Cancel);
        using var ownAfter = await Bearer(own).GetAsync("/api/findings", Cancel);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ownRevoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ownAfter.StatusCode);
        Assert.Equal(0, (await TokensAsync(admin)).GetArrayLength());
    }

    [Fact]
    public async Task Last_use_is_written_at_most_once_per_five_minutes()
    {
        using var browser = await SignedInAsync("member", Here);
        using var bearer = Bearer(await IssueAsync(browser));
        Assert.Equal(JsonValueKind.Null, (await LastUseAsync(browser)).ValueKind);

        var firstUse = NowMs;
        await AccessAsync(bearer);
        _clock.Advance(TimeSpan.FromMinutes(4));
        await AccessAsync(bearer);
        Assert.Equal(firstUse, (await LastUseAsync(browser)).GetInt64());
        _clock.Advance(TimeSpan.FromMinutes(1));
        await AccessAsync(bearer);
        Assert.Equal(NowMs, (await LastUseAsync(browser)).GetInt64());
    }

    [Fact]
    public async Task No_column_holds_the_token_only_its_SHA_256()
    {
        using var browser = await SignedInAsync("member", Here);
        var token = await IssueAsync(browser);

        await using var connection = await Database.OpenConnectionAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM terminal_tokens;";
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        Assert.True(await reader.ReadAsync(Cancel));
        for (var column = 0; column < reader.FieldCount; column++)
        {
            var value = reader.GetValue(column);
            if (value is byte[] bytes)
                Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), bytes);
            else
                Assert.DoesNotContain(token["pshub_".Length..], Convert.ToString(value) ?? "",
                    StringComparison.Ordinal);
        }

        Assert.False(await reader.ReadAsync(Cancel));
    }

    [Fact]
    public async Task The_eleventh_token_drops_the_oldest()
    {
        using var browser = await SignedInAsync("member", Here);
        var first = await IssueAsync(browser);
        for (var index = 0; index < 10; index++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await IssueAsync(browser);
        }

        using var oldest = await Bearer(first).GetAsync("/api/findings", Cancel);

        Assert.Equal(10, (await TokensAsync(browser)).GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, oldest.StatusCode);
    }

    [Fact]
    public async Task A_token_follows_RequiredGroups_across_a_restart_and_a_local_one_is_unaffected()
    {
        string token;
        await using (var gated = Factory.WithWebHostBuilder(builder =>
                         builder.UseSetting("Hub:Auth:RequiredGroups:0", "/teams/shop")))
        {
            using var browser = FakeProvider.Client(gated);
            browser.DefaultRequestHeaders.Add("X-Forwarded-For", Here);
            using (var callback = await FakeProvider.SignInAsync(browser, "member"))
            {
                Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
            }

            token = await IssueAsync(browser, gated);
            using var admitted = await Bearer(token, gated).GetAsync("/api/access", Cancel);
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }

        const string
            Local =
                "pshub_local-break-glass-0123456789abcdefghijklmnopqrstu"; // gitleaks:allow -- synthetic test credential
        Assert.True(await Database.TryInsertTerminalTokenAsync(new TerminalToken(
            "0123456789abcdef0123456789abcdef", SHA256.HashData(Encoding.UTF8.GetBytes(Local)), "local:ops",
            "local:ops",
            [], null, Here, NowMs, NowMs + 3_600_000, null), NowMs, Cancel));

        await using var dropped = Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Hub:Auth:RequiredGroups:0", "/hub-admins"));
        using var refused = await Bearer(token, dropped).GetAsync("/api/access", Cancel);
        using var local = await Bearer(Local, dropped).GetAsync("/api/access", Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
    }

    [Fact]
    public async Task The_purge_removes_expired_tokens()
    {
        using var browser = await SignedInAsync("member", Here);
        using var bearer = Bearer(await IssueAsync(browser));
        var expiresAtMs = Assert.Single((await TokensAsync(browser)).EnumerateArray()).GetProperty("expires_at_ms")
            .GetInt64();

        _clock.Advance(TimeSpan.FromMilliseconds(expiresAtMs - NowMs - 1));
        using (var live = await bearer.GetAsync("/api/findings", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        using (var expired = await bearer.GetAsync("/api/findings", Cancel))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        }

        await Database.PurgeAsync(0, 0, expiresAtMs - 1, Cancel);
        Assert.Single(await Database.ListTerminalTokensAsync(null, 0, 100, Cancel));
        await Database.PurgeAsync(0, 0, expiresAtMs, Cancel);
        Assert.Empty(await Database.ListTerminalTokensAsync(null, 0, 100, Cancel));
    }

    private HttpClient Cli(string address, WebApplicationFactory<Program>? hub = null)
    {
        var client = FakeProvider.Client(hub ?? Factory);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", address);
        return client;
    }

    private HttpClient Bearer(string token, WebApplicationFactory<Program>? hub = null)
    {
        var client = Cli(Here, hub);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<HttpClient> SignedInAsync(string user, string address,
        WebApplicationFactory<Program>? hub = null)
    {
        var client = Cli(address, hub);
        using var callback = await FakeProvider.SignInAsync(client, user);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return client;
    }

    private static Task<HttpResponseMessage> FormAsync(HttpClient client, string path, string body)
    {
        return client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"),
            Cancel);
    }

    private static StringContent Json(string body)
    {
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    private static Task<HttpResponseMessage> BrowserPostAsync(HttpClient client, string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return client.SendAsync(request, Cancel);
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string id)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/tokens/" + id) { Content = Json("") };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return client.SendAsync(request, Cancel);
    }

    private static async Task<JsonElement> StartAsync(HttpClient cli)
    {
        using var response = await FormAsync(cli, "/auth/device", "client_id=perf-sentinel");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
    }

    private static string Poll(JsonElement start)
    {
        return Poll(start.GetProperty("device_code").GetString()!);
    }

    private static string Poll(string deviceCode)
    {
        return "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code&client_id=perf-sentinel&device_code="
               + Uri.EscapeDataString(deviceCode);
    }

    private static string Code(JsonElement start)
    {
        return $$"""{"user_code":"{{start.GetProperty("user_code").GetString()}}"}""";
    }

    private static string Decision(JsonElement start, bool approve)
    {
        return
            $$"""{"user_code":"{{start.GetProperty("user_code").GetString()!.ToLowerInvariant()}}","approve":{{(approve ? "true" : "false")}}}""";
    }

    private static async Task<string> OAuthErrorAsync(HttpClient client, string path, string body)
    {
        using var response = await FormAsync(client, path, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("error")
            .GetString()!;
    }

    // Approve from the browser, then poll as the CLI would from the same address.
    private async Task<string> IssueAsync(HttpClient browser, WebApplicationFactory<Program>? hub = null)
    {
        using var cli = Cli(Here, hub);
        var start = await StartAsync(cli);
        using (var approve = await BrowserPostAsync(browser, "/api/device/approve", Decision(start, true)))
        {
            Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        }

        using var issued = await FormAsync(cli, "/auth/device/token", Poll(start));
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        return JsonDocument.Parse(await issued.Content.ReadAsStringAsync(Cancel)).RootElement
            .GetProperty("access_token").GetString()!;
    }

    private static async Task<JsonElement> VerifyAsync(HttpClient browser, string body)
    {
        using var response = await BrowserPostAsync(browser, "/api/device/verify", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
    }

    private static async Task<JsonElement> AccessAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/access", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
    }

    private static async Task<JsonElement> TokensAsync(HttpClient browser)
    {
        using var response = await browser.GetAsync("/api/tokens", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
    }

    private static async Task<JsonElement> LastUseAsync(HttpClient browser)
    {
        return Assert.Single((await TokensAsync(browser)).EnumerateArray()).GetProperty("last_used_at_ms");
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

    // Every request reaches the Hub from the trusted proxy, as behind an ingress, unless
    // X-Test-Peer names another peer.
    private sealed class FromTheProxy : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue(Peer, out var peer)
                        ? IPAddress.Parse(peer.ToString())
                        : IPAddress.Loopback;
                    return nextMiddleware(context);
                });
                next(app);
            };
        }
    }
}
