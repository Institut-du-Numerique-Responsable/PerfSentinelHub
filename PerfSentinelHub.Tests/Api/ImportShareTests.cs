using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     The import gate shared fairly: one source holds one slot at most, and a body that
///     trickles gives its slot back at the deadline. Each test owns its Hub and its clock.
/// </summary>
public sealed class ImportShareTests : IAsyncLifetime
{
    private const string TestKey = "0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential
    private const string OtherKey = "fedcba9876543210fedcba9876543210"; // gitleaks:allow -- synthetic test credential

    private const string Batch = """
                                 {"producer_version":"0.11.2","findings":[]}
                                 """;

    private readonly HubApplicationFactory _factory = new();
    private WebApplicationFactory<Program> _hub = null!;

    public ValueTask InitializeAsync()
    {
        _hub = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<HubOptions>(options => options.Sources =
            [
                .. options.Sources,
                new SourceOptions
                {
                    Id = "other", Name = "Other", Environment = "test",
                    BaseUrl = new Uri("http://127.0.0.1:1"), ImportApiKey = OtherKey
                }
            ])));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _hub.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_source_holding_its_share_is_refused_while_another_still_imports()
    {
        var gate = _hub.Services.GetRequiredService<ImportGate>();
        using var client = _hub.CreateClient();
        Assert.True(gate.TryEnter("test"));
        try
        {
            using var busy = await client.SendAsync(Import("test", TestKey, Body()),
                TestContext.Current.CancellationToken);
            // An empty batch is a 400 once admitted, which is what the other source gets.
            using var admitted = await client.SendAsync(Import("other", OtherKey, Body()),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.Equal("1", busy.Headers.GetValues("Retry-After").Single());
            Assert.Equal(HttpStatusCode.BadRequest, admitted.StatusCode);
        }
        finally
        {
            gate.Exit("test");
        }
    }

    [Theory]
    [InlineData("/api/import/findings")]
    [InlineData("/api/import/incidents")]
    public async Task A_body_that_trickles_past_the_deadline_gives_its_slot_back(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = (FakeTimeProvider)_hub.Services.GetRequiredService<TimeProvider>();
        // The server's own client: the factory's follows redirects, so it buffers a body before sending it.
        using var client = _hub.Server.CreateClient();
        var release = new TaskCompletionSource();
        try
        {
            using var stalled = Import("test", TestKey, new StalledContent(release.Task), path);
            var sent = client.SendAsync(stalled, cancellationToken);
            for (var tick = 0; !sent.IsCompleted; tick++)
            {
                Assert.True(tick < 200, "the read never timed out");
                clock.Advance(TimeSpan.FromSeconds(61));
                await Task.WhenAny(sent, Task.Delay(20, cancellationToken));
            }

            using var timedOut = await sent;
            Assert.Equal(HttpStatusCode.RequestTimeout, timedOut.StatusCode);
        }
        finally
        {
            release.TrySetResult();
        }

        // The slot came back: the same source is admitted again.
        using var again = await client.SendAsync(Import("test", TestKey, Body(), path), cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    private static StringContent Body()
    {
        return new StringContent(Batch, Encoding.UTF8, "application/json");
    }

    private static HttpRequestMessage Import(string sourceId, string key, HttpContent content,
        string path = "/api/import/findings")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{path}?source_id={sourceId}") { Content = content };
        request.Headers.Add("X-API-Key", key);
        return request;
    }

    /// <summary>A first few bytes, then nothing until the test lets go.</summary>
    private sealed class StalledContent(Task release) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync("{\"producer_version\""u8.ToArray());
            await stream.FlushAsync();
            await release;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
