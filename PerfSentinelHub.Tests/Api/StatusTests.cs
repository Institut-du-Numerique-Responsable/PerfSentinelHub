using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Tests.Support;

namespace PerfSentinelHub.Tests.Api;

public sealed class StatusTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>, IDisposable
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly List<WebApplicationFactory<Program>> _hubs = [];
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-status-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var hub in _hubs)
            hub.Dispose();
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, true);
    }

    [Fact]
    public async Task Status_is_stable_and_health_endpoints_are_distinct()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var status = await _client.GetAsync("/api/status", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        using var body = await JsonDocument.ParseAsync(
            await status.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal("perf-sentinel-hub", body.RootElement.GetProperty("service").GetString());
        // The padded four-component AssemblyVersion would read 0.1.6.0, matching
        // neither the tag, the chart appVersion, nor the image label.
        Assert.Matches(
            @"^\d+\.\d+\.\d+$", body.RootElement.GetProperty("version").GetString());

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live", cancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/ready", cancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Static_assets_are_revalidated_rather_than_cached_by_age()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var asset = await _client.GetAsync("/app.js", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        // No name here carries a fingerprint, so the browser must ask every time
        // rather than pick its own freshness from the last-modified date.
        Assert.True(asset.Headers.CacheControl?.NoCache);
        Assert.NotNull(asset.Headers.ETag);
    }

    [Fact]
    public void The_suite_does_not_reach_github()
    {
        // The update check is the Hub's only outbound destination that is not a
        // configured source. A test host that leaves it on makes the suite
        // non-hermetic and spends the caller's GitHub rate limit.
        var options = factory.Services.GetRequiredService<IOptions<HubOptions>>().Value;
        Assert.False(options.UpdateCheck.Enabled);
    }

    [Theory]
    [InlineData("Acme", true, "true")]
    [InlineData("Acme", false, "false")]
    [InlineData(null, true, null)]
    public async Task Status_says_cards_when_on_and_the_caller_may_write_them(
        string? organisation,
        bool engineHasCard,
        string? expected)
    {
        using var client = Hub(organisation, engineHasCard).CreateClient();

        using var status =
            JsonDocument.Parse(await client.GetStringAsync("/api/status", TestContext.Current.CancellationToken));

        // false keeps the screen's "cannot make cards yet" banner reachable; absent hides the screen.
        Assert.Equal(expected,
            status.RootElement.TryGetProperty("cards", out var cards) ? cards.GetRawText() : null);
    }

    private WebApplicationFactory<Program> Hub(string? organisation, bool engineHasCard)
    {
        var enginePath = new FakeEngine { Version = "perf-sentinel 0.38.0", Card = engineHasCard }
            .WriteTo(Path.Combine(_workspace, $"engine-{Guid.NewGuid():N}"));
        var hub = new HubApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
            {
                options.Analysis = new AnalysisOptions
                {
                    EngineBinaryPath = enginePath,
                    ReportDirectory = Path.Combine(_workspace, "reports")
                };
                options.Cards = new CardsOptions { Organisation = organisation };
            })));
        _hubs.Add(hub);
        return hub;
    }
}
