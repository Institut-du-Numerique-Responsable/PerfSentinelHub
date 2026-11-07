using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;
using PerfSentinelHub.Tests.Support;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     What a submission does with <c>fetch_concurrency</c>, per engine version and per configured max: the
///     version gate lives in the handler, where the version is known, and the default is written into the
///     stored request there, because the worker replays that request without knowing the version.
///     No worker drains the queue, so each run stays as it was stored.
/// </summary>
public sealed class AnalysisFetchConcurrencyTests
{
    private const string Service = """{"service":"checkout","lookback":"1h"}""";

    [Theory]
    // The engine cannot read the flag: a value is refused with both versions, a max under 16 is refused
    // because the engine would still read 16, and a max of 16 or more leaves nothing to write.
    [InlineData("0.42.1", 64, """{"service":"checkout","lookback":"1h","fetch_concurrency":4}""", 400,
        "fetch_concurrency needs engine 0.43.0 or later, this Hub runs 0.42.1.")]
    [InlineData("0.42.1", 8, Service, 400, "8")]
    [InlineData("banana", 8, Service, 400, "has not read its engine's version")]
    [InlineData("0.42.1", 64, Service, 202, Service)]
    // The engine reads it: the default is written, under the max when the operator set one below 16.
    [InlineData("0.43.0", 8, Service, 202, """{"service":"checkout","lookback":"1h","fetch_concurrency":8}""")]
    [InlineData("0.43.0", 64, Service, 202, """{"service":"checkout","lookback":"1h","fetch_concurrency":16}""")]
    [InlineData("0.43.0", 64, """{"service":"checkout","lookback":"1h","fetch_concurrency":4}""", 202,
        """{"service":"checkout","lookback":"1h","fetch_concurrency":4}""")]
    // A trace ID reads one trace: no default, and no refusal for a max the old engine cannot hold.
    [InlineData("0.43.0", 64, """{"trace_id":"abc123"}""", 202, """{"trace_id":"abc123"}""")]
    [InlineData("0.42.1", 8, """{"trace_id":"abc123"}""", 202, """{"trace_id":"abc123"}""")]
    public async Task A_submission_is_judged_against_the_engine_version_and_the_max(
        string engineVersion, int max, string request, int status, string expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-fetch-{Guid.NewGuid():N}");
        try
        {
            var enginePath = new FakeEngine { Version = $"perf-sentinel {engineVersion}" }
                .WriteTo(Path.Combine(workspace, "perf-sentinel"));
            using var factory = new HubApplicationFactory();
            await using var hub = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                foreach (var worker in services.Where(descriptor =>
                             descriptor.ServiceType == typeof(IHostedService) &&
                             descriptor.ImplementationType == typeof(AnalysisWorker)).ToArray())
                    services.Remove(worker);
                services.PostConfigure<HubOptions>(options =>
                {
                    options.Analysis = new AnalysisOptions
                    {
                        EngineBinaryPath = enginePath,
                        ReportDirectory = workspace,
                        FetchConcurrencyWarn = Math.Min(16, max),
                        FetchConcurrencyMax = max
                    };
                    options.Sources =
                    [
                        new SourceOptions
                        {
                            Id = "tempo", Name = "Tempo", Environment = "prod", Kind = SourceKinds.Tempo,
                            BaseUrl = new Uri("http://127.0.0.1:2")
                        }
                    ];
                });
            }));
            using var client = hub.CreateClient();

            using var response = await client.PostAsync("/api/analyses",
                new StringContent($$"""{"source_id":"tempo","request":{{request}}}""", Encoding.UTF8,
                    "application/json"), cancellationToken);

            Assert.Equal(status, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (status == (int)HttpStatusCode.Accepted)
            {
                var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
                var run = await hub.Services.GetRequiredService<HubDatabase>().FindRunAsync(id, cancellationToken);
                Assert.Equal(expected, run!.RequestJson);
                return;
            }

            var problem = JsonDocument.Parse(body).RootElement;
            Assert.Equal("fetch_concurrency_unsupported", problem.GetProperty("code").GetString());
            Assert.Contains(expected, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.Equal(0, await hub.Services.GetRequiredService<HubDatabase>()
                .CountPendingRunsAsync(cancellationToken));
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, true);
        }
    }
}
