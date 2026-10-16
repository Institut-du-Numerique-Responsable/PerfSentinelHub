using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

/// <summary>
///     A CI publishing a disclosure: the key, the multipart body, the engine's verdict, and
///     what the Hub keeps or refuses. One Hub per test, since each sets its own engine.
/// </summary>
public sealed class DisclosureImportApiTests : IDisposable
{
    private const string ImportKey = "disclosure-import-0123456789abcdef0123"; // gitleaks:allow -- synthetic test credential

    private static readonly string Report = DisclosureRendererTests.Report;
    private readonly List<WebApplicationFactory<Program>> _hubs = [];
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-dimport-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var hub in _hubs)
            hub.Dispose();
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, true);
    }

    [Fact]
    public async Task A_hub_without_an_import_key_answers_404()
    {
        using var client = Hub(Rendering(), importKey: null).CreateClient();

        using var response = await client.SendAsync(Publish(Report), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_key_is_401_and_counted()
    {
        var hub = Hub(Rendering());
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(Publish(Report, key: "wrong"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("perf_sentinel_hub_disclosure_import_rejected_total{reason=\"unauthorized\"} 1",
            await client.GetStringAsync("/metrics", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_rendered_publication_is_201_with_the_files_and_the_pdf_stored()
    {
        var hub = Hub(Rendering());
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(
            Publish(Report, ("attestation", "statement"u8.ToArray()), ("bundle", "bundle"u8.ToArray())),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal("rendered", body.RootElement.GetProperty("pdf").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("reason").ValueKind);
        Assert.True(body.RootElement.GetProperty("content_hash_checked").GetBoolean());
        Assert.True(body.RootElement.GetProperty("binding_checked").GetBoolean());
        var id = body.RootElement.GetProperty("id").GetString()!;
        var database = Database(hub);
        Assert.Equal(Encoding.UTF8.GetBytes(Report), await database.ReadFileAsync(id, DisclosureFile.Report, CancellationToken.None));
        Assert.Equal("bundle"u8.ToArray(), await database.ReadFileAsync(id, DisclosureFile.Bundle, CancellationToken.None));
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await database.ReadFileAsync(id, DisclosureFile.Pdf, CancellationToken.None))!));
        // One published delivery per webhook that takes public reports.
        Assert.Equal("auditors:published", string.Join(',',
            (await database.ListDeliveriesAsync(id, CancellationToken.None)).Select(d => $"{d.Webhook}:{d.Event}")));
    }

    [Theory]
    [InlineData("content_hash_mismatch")]
    [InlineData("binding_mismatch")]
    public async Task A_refused_render_is_422_with_its_reason_and_nothing_stored(string reason)
    {
        var hub = Hub(new FakeEngine
        {
            DisclosePdf = true,
            DisclosePdfExitCode = 1,
            DisclosePdfOutput = $$"""{"result":"refused","reason":"{{reason}}"}"""
        });
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(Publish(Report), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal($$"""{"reason":"{{reason}}"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await Database(hub).ListDisclosuresAsync(CancellationToken.None));
        Assert.Contains("perf_sentinel_hub_disclosure_import_rejected_total{reason=\"render_refused\"} 1",
            await client.GetStringAsync("/metrics", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"schema_version":"perf-sentinel-report/v1.7"}""")]
    public async Task A_report_the_hub_cannot_read_is_422_malformed_report_before_any_engine_run(string report)
    {
        var hub = Hub(Rendering());
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(Publish(report), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("""{"reason":"malformed_report"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(EngineRan());
    }

    [Fact]
    public async Task An_engine_malformed_verdict_is_422_with_the_engine_s_reason()
    {
        var hub = Hub(new FakeEngine
        {
            DisclosePdf = true,
            DisclosePdfExitCode = 3,
            DisclosePdfOutput = """{"result":"error","reason":"file_too_large"}"""
        });
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(Publish(Report), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("""{"reason":"file_too_large"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await Database(hub).ListDisclosuresAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Exit_5_stores_without_a_pdf_and_says_why()
    {
        var hub = Hub(new FakeEngine
        {
            DisclosePdf = true,
            DisclosePdfExitCode = 5,
            DisclosePdfOutput = """{"result":"unrenderable","reason":"unsupported_characters"}"""
        });
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(Publish(Report), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal("not_checked", body.RootElement.GetProperty("pdf").GetString());
        Assert.Equal("unsupported_characters", body.RootElement.GetProperty("reason").GetString());
        Assert.False(body.RootElement.GetProperty("content_hash_checked").GetBoolean());
        var stored = Assert.Single(await Database(hub).ListDisclosuresAsync(CancellationToken.None));
        Assert.False(stored.HasPdf);
        Assert.Equal("unsupported_characters", stored.RenderReason);
    }

    [Fact]
    public async Task An_engine_without_disclose_pdf_stores_the_files_as_not_checked()
    {
        var hub = Hub(new FakeEngine { DisclosePdf = false });
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(Publish(Report), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await JsonAsync(response);
        Assert.Equal("not_checked", body.RootElement.GetProperty("pdf").GetString());
        Assert.Equal("unsupported_engine", body.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("binding_checked").ValueKind);
    }

    [Fact]
    public async Task An_internal_report_is_422_before_any_engine_run_when_internal_is_off()
    {
        var hub = Hub(Rendering());
        using var client = hub.CreateClient();
        var report = Report.Replace("\"public\"", "\"internal\"", StringComparison.Ordinal);

        using var response = await client.SendAsync(Publish(report), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("""{"reason":"internal_refused"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(EngineRan());
    }

    [Fact]
    public async Task The_same_bytes_twice_is_200_and_changes_nothing()
    {
        var hub = Hub(Rendering());
        using var client = hub.CreateClient();
        using var first = await client.SendAsync(Publish(Report), TestContext.Current.CancellationToken);
        var firstId = (await JsonAsync(first)).RootElement.GetProperty("id").GetString();

        using var second = await client.SendAsync(Publish(Report), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstId, (await JsonAsync(second)).RootElement.GetProperty("id").GetString());
        Assert.Single(await Database(hub).ListDeliveriesAsync(firstId!, CancellationToken.None));
    }

    [Fact]
    public async Task A_signed_copy_replaces_the_unsigned_one_without_a_second_webhook()
    {
        var hub = Hub(Rendering());
        using var client = hub.CreateClient();
        using var unsigned = await client.SendAsync(Publish(Report), TestContext.Current.CancellationToken);
        var id = (await JsonAsync(unsigned)).RootElement.GetProperty("id").GetString()!;
        // content_hash leaves the signature fields out, so the signed report keeps the same hash.
        var signedReport = Report.Replace("\"signed\"", "\"signed\" ", StringComparison.Ordinal);

        using var signed = await client.SendAsync(
            Publish(signedReport, ("attestation", "statement"u8.ToArray()), ("bundle", "bundle"u8.ToArray())),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        var database = Database(hub);
        var summary = (await database.GetDisclosureAsync(id, CancellationToken.None))!;
        Assert.NotNull(summary.SignatureAddedAtMs);
        Assert.True(summary.HasAttestation);
        Assert.Equal(Encoding.UTF8.GetBytes(signedReport), await database.ReadFileAsync(id, DisclosureFile.Report, CancellationToken.None));
        Assert.Single(await database.ListDeliveriesAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task The_same_uuid_with_another_hash_is_409()
    {
        var hub = Hub(Rendering());
        using var client = hub.CreateClient();
        await client.SendAsync(Publish(Report), TestContext.Current.CancellationToken);

        using var response = await client.SendAsync(
            Publish(Report.Replace("sha256:aaaa", "sha256:bbbb", StringComparison.Ordinal)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(await Database(hub).ListDisclosuresAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_body_over_8_mib_is_413()
    {
        using var client = Hub(Rendering()).CreateClient();

        using var response = await client.SendAsync(
            Publish(Report, ("attestation", new byte[(8 * 1024 * 1024) + 1])), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("report")]
    [InlineData("signature")]
    public async Task A_duplicate_or_unknown_part_is_422_malformed_report(string extraPart)
    {
        using var client = Hub(Rendering()).CreateClient();

        using var response = await client.SendAsync(
            Publish(Report, (extraPart, "{}"u8.ToArray())), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("""{"reason":"malformed_report"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(EngineRan());
    }

    [Fact]
    public async Task A_body_that_is_not_multipart_is_415()
    {
        using var client = Hub(Rendering()).CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/import/disclosures")
        {
            Content = new StringContent(Report, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-API-Key", ImportKey);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task A_second_publication_in_flight_is_503()
    {
        var hub = Hub(Rendering());
        using var client = hub.CreateClient();
        var gate = hub.Services.GetRequiredService<DisclosureImportGate>();
        Assert.True(gate.TryEnter());
        try
        {
            using var response = await client.SendAsync(Publish(Report), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("1", response.Headers.RetryAfter?.ToString());
            Assert.Contains("perf_sentinel_hub_disclosure_import_rejected_total{reason=\"gate_full\"} 1",
                await client.GetStringAsync("/metrics", TestContext.Current.CancellationToken));
        }
        finally
        {
            gate.Exit();
        }
    }

    [Fact]
    public async Task Metrics_name_every_reason_from_startup_and_say_nothing_while_disclosures_are_off()
    {
        using var on = Hub(Rendering()).CreateClient();
        using var off = Hub(Rendering(), importKey: null).CreateClient();

        var metrics = await on.GetStringAsync("/metrics", TestContext.Current.CancellationToken);
        Assert.All(
            ["unauthorized", "gate_full", "too_large", "unsupported_media_type", "malformed_report", "render_refused",
             "internal_refused", "conflict"],
            reason => Assert.Contains($"perf_sentinel_hub_disclosure_import_rejected_total{{reason=\"{reason}\"}} 0", metrics));
        Assert.DoesNotContain("disclosure", await off.GetStringAsync("/metrics", TestContext.Current.CancellationToken));
    }

    private static FakeEngine Rendering()
    {
        return new FakeEngine { DisclosePdf = true, DisclosePdfOutput = """{"result":"rendered"}""" };
    }

    private bool EngineRan()
    {
        return File.Exists(Path.Combine(_workspace, "reports", "disclose-args.txt"));
    }

    private WebApplicationFactory<Program> Hub(FakeEngine engine, string? importKey = ImportKey)
    {
        var enginePath = (engine with { Version = "perf-sentinel 0.31.0" })
            .WriteTo(Path.Combine(_workspace, $"engine-{Guid.NewGuid():N}"));
        var hub = new HubApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
            {
                options.Analysis = new AnalysisOptions
                {
                    EngineBinaryPath = enginePath,
                    ReportDirectory = Path.Combine(_workspace, "reports")
                };
                options.Disclosures = new DisclosuresOptions
                {
                    ImportKey = importKey,
                    Webhooks =
                    [
                        new WebhookOptions
                        {
                            Name = "auditors",
                            Url = new Uri("http://127.0.0.1:9/hook"),
                            Secret = "webhook-secret-0123456789abcdef0123" // gitleaks:allow -- synthetic test credential
                        }
                    ]
                };
            })));
        _hubs.Add(hub);
        return hub;
    }

    private static HubDatabase Database(WebApplicationFactory<Program> hub)
    {
        return hub.Services.GetRequiredService<HubDatabase>();
    }

    private static HttpRequestMessage Publish(string report, params (string Name, byte[] Bytes)[] extra)
    {
        return Publish(report, ImportKey, extra);
    }

    private static HttpRequestMessage Publish(string report, string key, params (string Name, byte[] Bytes)[] extra)
    {
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(report)), "report", "report.json" }
        };
        foreach (var (name, bytes) in extra)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(part, name, name);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/import/disclosures") { Content = content };
        request.Headers.Add("X-API-Key", key);
        return request;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
