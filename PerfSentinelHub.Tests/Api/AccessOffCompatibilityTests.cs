using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     Hub:Access:Enabled off, the default, is today's Hub byte for byte: with Auth off,
///     and with Auth on and access off. The fixtures were captured before enforcement
///     existed, on a seeded database under a fixed clock. The Hub's own version is the
///     one field that moves with a release, so it is read from the build.
/// </summary>
public sealed partial class AccessOffCompatibilityTests : IAsyncLifetime
{
    private const string Succeeded = "0123456789abcdef";
    private const string Pending = "fedcba9876543210";
    private const string Signature = "slow_sql:cart:0000000000000000000000000000000000000001";
    private const string IncidentId = "0000000000000000000000000000000a";

    // Every header a golden route answered with before enforcement, Date aside.
    private const string Json = "Content-Type: application/json";
    private const string JsonUtf8 = "Content-Type: application/json; charset=utf-8";

    private static readonly DateTimeOffset SignInAt = DateTimeOffset.UtcNow;
    private readonly FakeTimeProvider _clock = new(SignInAt);

    private readonly HubApplicationFactory _hub = new();
    private WebApplicationFactory<Program> _off = null!;
    private FakeProvider _provider = null!;
    private WebApplicationFactory<Program> _signedIn = null!;

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync(new Dictionary<string, string>
        {
            ["alice"] = """{"sub":"1","email":"alice@example.internal","groups":["/hub-users"]}""",
            // Names access on would refuse: no subject, and the reserved prefixes.
            ["local"] = """{"email":"local:x","groups":["/hub-users"]}""",
            ["machine"] = """{"email":"machine:ci"}"""
        });
        // No analysis worker: it would claim, interrupt or expire the seeded runs.
        _off = _hub.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            RemoveAnalysisWorker(services);
            AddLabSource(services);
        }));
        _signedIn = _hub.WithWebHostBuilder(builder =>
        {
            _provider.Configure(builder);
            builder.ConfigureServices(RemoveAnalysisWorker);
            builder.ConfigureServices(AddLabSource);
            // Today's clock rather than the factory's 1970 one, so a cookie is still alive when replayed.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });
        await SeedAsync(_off.Services.GetRequiredService<HubDatabase>());
    }

    public async ValueTask DisposeAsync()
    {
        await _signedIn.DisposeAsync();
        await _off.DisposeAsync();
        await _hub.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Theory]
    [InlineData(false, "/api/status", "access-off-status.json", JsonUtf8)]
    [InlineData(true, "/api/status", "access-off-status-session.json", JsonUtf8)]
    [InlineData(false, "/api/sources", "access-off-sources.json", JsonUtf8)]
    [InlineData(true, "/api/sources", "access-off-sources.json", JsonUtf8)]
    [InlineData(false, "/api/analyses", "access-off-analyses.json", Json)]
    [InlineData(true, "/api/analyses", "access-off-analyses.json", Json)]
    [InlineData(false, "/api/analyses/" + Succeeded, "access-off-analysis.json", Json)]
    [InlineData(true, "/api/analyses/" + Succeeded, "access-off-analysis.json", Json)]
    [InlineData(false, "/api/findings", "access-off-findings.json", Json)]
    [InlineData(true, "/api/findings", "access-off-findings-session.json", Json)]
    [InlineData(false, "/api/findings/rider-trace", "access-off-findings.json", Json)]
    [InlineData(true, "/api/findings/rider-trace", "access-off-findings-session.json", Json)]
    [InlineData(false, "/api/incidents", "access-off-incidents.json", Json)]
    [InlineData(true, "/api/incidents", "access-off-incidents.json", Json)]
    [InlineData(false, "/api/incidents/" + IncidentId, "access-off-incident.json", Json)]
    [InlineData(true, "/api/incidents/" + IncidentId, "access-off-incident.json", Json)]
    public async Task The_raw_json_is_the_one_captured_before_enforcement(
        bool auth, string path, string fixture, string headers)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = auth ? await SignedInClientAsync() : _off.CreateClient();

        using var response = await client.GetAsync(path, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await response.Content.ReadAsStringAsync(cancellationToken);
        var golden = (await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), cancellationToken))
            .TrimEnd('\n').Replace("{hub_version}", HubVersion.Current, StringComparison.Ordinal);
        // A source's age is the session clock, today, less the time it was last seen.
        golden = AgePlaceholder().Replace(golden, match => ((SignInAt.ToUnixTimeMilliseconds() -
                                                             long.Parse(match.Groups[1].Value,
                                                                 CultureInfo.InvariantCulture)) / 1000)
            .ToString(CultureInfo.InvariantCulture));
        Assert.Equal(golden, actual);
        Assert.Equal(headers, Headers(response));
    }

    [Theory]
    [InlineData("/api/findings")]
    [InlineData("/api/findings/rider-trace")]
    [InlineData("/metrics")]
    public async Task With_auth_on_and_access_off_the_machine_routes_stay_anonymous_on_the_main_port(string path)
    {
        using var client = FakeProvider.Client(_signedIn);

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task Without_required_groups_and_access_off_the_session_still_slides()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var signIn = FakeProvider.Client(_signedIn);
        using var callback = await FakeProvider.SignInAsync(signIn, "alice");
        var session = FakeProvider.SessionCookie(callback);

        _clock.Advance(TimeSpan.FromHours(5));
        using var client = FakeProvider.Client(_signedIn);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add("Cookie", session);
        using var status = await client.SendAsync(request, cancellationToken);

        // Past half its life, a sliding ticket is reissued.
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Contains(status.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("hub_session=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("GET", "/", HttpStatusCode.OK)]
    [InlineData("GET", "/app.js", HttpStatusCode.OK)]
    [InlineData("GET", "/api/incidents", HttpStatusCode.OK)]
    [InlineData("GET", "/api/incidents/nope", HttpStatusCode.NotFound)]
    [InlineData("GET", "/api/sources/nope/daemon", HttpStatusCode.NotFound)]
    [InlineData("GET", "/api/analyses/" + Pending, HttpStatusCode.OK)]
    [InlineData("GET", "/reports/" + Pending + ".html", HttpStatusCode.NotFound)]
    [InlineData("POST", "/api/analyses", HttpStatusCode.ServiceUnavailable)]
    [InlineData("POST", "/api/sources/test/acks", HttpStatusCode.Forbidden)]
    [InlineData("POST", "/api/sources/test/acks/revoke", HttpStatusCode.Forbidden)]
    [InlineData("POST", "/api/incidents/refresh", HttpStatusCode.OK)]
    [InlineData("GET", "/health/live", HttpStatusCode.OK)]
    [InlineData("GET", "/health/ready", HttpStatusCode.OK)]
    public async Task Every_route_answers_as_before_with_auth_off(string method, string path, HttpStatusCode expected)
    {
        using var client = _off.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    // A session's write without Sec-Fetch-Site passes as before, and the ack routes then find
    // no such finding where a caller with no session is refused an identity.
    [Theory]
    [InlineData("GET", "/", HttpStatusCode.OK)]
    [InlineData("GET", "/app.js", HttpStatusCode.OK)]
    [InlineData("GET", "/api/incidents", HttpStatusCode.OK)]
    [InlineData("GET", "/api/incidents/nope", HttpStatusCode.NotFound)]
    [InlineData("GET", "/api/sources/nope/daemon", HttpStatusCode.NotFound)]
    [InlineData("GET", "/api/analyses/" + Pending, HttpStatusCode.OK)]
    [InlineData("GET", "/reports/" + Pending + ".html", HttpStatusCode.NotFound)]
    [InlineData("POST", "/api/analyses", HttpStatusCode.ServiceUnavailable)]
    [InlineData("POST", "/api/sources/test/acks", HttpStatusCode.NotFound)]
    [InlineData("POST", "/api/sources/test/acks/revoke", HttpStatusCode.NotFound)]
    [InlineData("POST", "/api/incidents/refresh", HttpStatusCode.OK)]
    [InlineData("GET", "/health/live", HttpStatusCode.OK)]
    [InlineData("GET", "/health/ready", HttpStatusCode.OK)]
    public async Task Every_route_answers_as_before_to_a_session_with_auth_on(string method, string path,
        HttpStatusCode expected)
    {
        using var client = await SignedInClientAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("local")]
    [InlineData("machine")]
    public async Task A_sign_in_with_access_off_adds_no_access_claim_and_refuses_no_name(string user)
    {
        using var client = FakeProvider.Client(_signedIn);

        using var callback = await FakeProvider.SignInAsync(client, user);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.DoesNotContain(AccessRoutesTests.Claims(_signedIn, callback),
            claim => claim.Type.StartsWith("hub:", StringComparison.Ordinal));
    }

    private static string Headers(HttpResponseMessage response)
    {
        return string.Join("; ", response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key != "Date")
            .OrderBy(header => header.Key, StringComparer.Ordinal)
            .Select(header => $"{header.Key}: {string.Join(',', header.Value)}"));
    }

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = FakeProvider.Client(_signedIn);
        using var callback = await FakeProvider.SignInAsync(client, "alice");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return client;
    }

    private static void RemoveAnalysisWorker(IServiceCollection services)
    {
        foreach (var worker in services.Where(descriptor =>
                     descriptor.ServiceType == typeof(IHostedService) &&
                     descriptor.ImplementationType == typeof(AnalysisWorker)).ToArray())
            services.Remove(worker);
    }

    // A second source, so a finding and an incident can be held at two.
    private static void AddLabSource(IServiceCollection services)
    {
        services.PostConfigure<HubOptions>(options => options.Sources =
        [
            .. options.Sources,
            new SourceOptions { Id = "lab", Name = "Lab", Environment = "lab", BaseUrl = new Uri("http://127.0.0.1:2") }
        ]);
    }

    // A finished run with its result and a pending one, one finding with attributes seen at
    // two sources and an incident at both, at fixed times and ids.
    private static async Task SeedAsync(HubDatabase database)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var (source, environment, team, at) in new[]
                     { ("test", "test", "shop", 4_000L), ("lab", "lab", "billing", 5_000L) })
        {
            var finding = $$"""
                            {"finding":{"type":"slow_sql","severity":"warning","trace_id":"rider-trace","service":"cart",
                             "grouping":[{"key":"team","value":"{{team}}"}],"source_endpoint":"GET /orders",
                             "pattern":{"template":"SELECT 1"},"confidence":"daemon_production","signature":"{{Signature}}"},
                             "first_seen_ms":1}
                            """;
            await database.UpsertBatchAsync(
                new SourceSnapshot(source, source, environment, "0.26.2"),
                FindingParser.Parse(Encoding.UTF8.GetBytes($"[{finding}]")), at, cancellationToken);
            var incident =
                $$"""{"id":"{{IncidentId}}","service":"cart","kind":"oom_kill","at_ms":1000,"window_from_ms":900,"window_to_ms":1100}""";
            await database.UpsertIncidentsAsync(
                source,
                [
                    new ParsedIncident(IncidentId, "cart", "oom_kill", 1000, null, 900, 1100, null, 1, incident,
                        $"[{finding}]")
                ],
                at, cancellationToken);
        }

        await database.TryInsertRunAsync(new AnalysisRun(
            Succeeded, AnalysisStatuses.Pending, "test", "Test", "test", "daemon", "{}", "alice@example.internal",
            1_000, null, null, null, null, null, null), cancellationToken);
        await database.CompleteRunAsync(
            Succeeded, AnalysisStatuses.Succeeded, 2_000, 90_000, "0.24.0", null,
            """{"findings":3,"traces":12}""", cancellationToken);
        await database.TryInsertRunAsync(new AnalysisRun(
            Pending, AnalysisStatuses.Pending, "test", "Test", "test", "daemon", "{}", "unknown",
            3_000, null, null, null, null, null, null), cancellationToken);
    }

    [GeneratedRegex(@"\{age:(\d+)\}")]
    private static partial Regex AgePlaceholder();
}
