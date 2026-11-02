using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     What one submission may cost the shared store: a bounded body, a stored request
///     holding only what the parser read, and a queue that stops growing once full.
///     Each test owns its Hub and database, and no worker drains the queue.
/// </summary>
public sealed class AnalysisSubmissionTests : IAsyncLifetime
{
    private readonly HubApplicationFactory _factory = new();
    private WebApplicationFactory<Program> _hub = null!;

    public ValueTask InitializeAsync()
    {
        _hub = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            foreach (var worker in services.Where(descriptor =>
                         descriptor.ServiceType == typeof(IHostedService) &&
                         descriptor.ImplementationType == typeof(AnalysisWorker)).ToArray())
                services.Remove(worker);
            services.PostConfigure<HubOptions>(options =>
            {
                options.Analysis = new AnalysisOptions
                {
                    EngineBinaryPath = "/usr/bin/false",
                    ReportDirectory = Path.GetTempPath()
                };
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
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _hub.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_stored_request_keeps_only_the_fields_the_parser_reads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = _hub.CreateClient();

        using var submitted = await PostAsync(client, """
                                                      {"source_id":"tempo","extra":1,"request":{"service":"checkout","lookback":"1h","trace_id":null,"padding":"xxxxxxxx","detection":{"max_fanout":30,"max_fanout":40,"sanitizer_aware_classification":"strict"}}}
                                                      """);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var id = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync(cancellationToken))
            .RootElement.GetProperty("id").GetString()!;

        var run = await _hub.Services.GetRequiredService<HubDatabase>().FindRunAsync(id, cancellationToken);
        Assert.Equal("""{"service":"checkout","lookback":"1h","detection":{"max_fanout":40,"sanitizer_aware_classification":"strict"}}""", run!.RequestJson);
    }

    [Fact]
    public async Task A_body_over_64_KiB_is_refused_before_it_is_parsed()
    {
        using var client = _hub.CreateClient();

        using var response = await PostAsync(client,
            """{"source_id":"tempo","request":{"service":"checkout","lookback":"1h","padding":""" + '"' +
            new string('x', 64 * 1024) + "\"}}");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await _hub.Services.GetRequiredService<HubDatabase>()
            .CountPendingRunsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_full_queue_refuses_a_submission_and_says_when_to_come_back()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = _hub.CreateClient();
        // Two workers by default, so four runs each.
        for (var queued = 0; queued < 8; queued++)
        {
            using var accepted = await PostAsync(client, Valid);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var refused = await PostAsync(client, Valid);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("30", refused.Headers.RetryAfter?.ToString());
        Assert.Equal(8, await _hub.Services.GetRequiredService<HubDatabase>().CountPendingRunsAsync(cancellationToken));
    }

    private const string Valid = """{"source_id":"tempo","request":{"service":"checkout","lookback":"1h"}}""";

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string body)
    {
        return client.PostAsync("/api/analyses", new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
    }
}
