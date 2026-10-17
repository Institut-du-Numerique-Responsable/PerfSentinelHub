using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;
using PerfSentinelHub.Tests.Disclosures;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     The Disclosures API on an open Hub: listing, detail, files, and the manage actions,
///     which name their caller through the trusted proxy header like the ack relay.
/// </summary>
public sealed class DisclosuresApiTests : IDisposable
{
    private const string
        ImportKey = "disclosure-import-0123456789abcdef0123"; // gitleaks:allow -- synthetic test credential

    private const long NowMs = 10_000;

    private static readonly byte[] ReportBytes = Encoding.UTF8.GetBytes(DisclosureRendererTests.Report);
    private readonly List<WebApplicationFactory<Program>> _hubs = [];
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-dapi-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var hub in _hubs)
            hub.Dispose();
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, true);
    }

    [Theory]
    [InlineData("/api/disclosures")]
    [InlineData("/api/disclosures/d1")]
    [InlineData("/api/disclosures/d1/files/report.json")]
    public async Task Every_read_route_is_404_while_disclosures_are_off(string path)
    {
        using var client = Hub(importKey: null).CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Status_says_disclosures_only_while_they_are_on()
    {
        using var on = Hub().CreateClient();
        using var off = Hub(importKey: null).CreateClient();

        using var onStatus =
            JsonDocument.Parse(await on.GetStringAsync("/api/status", TestContext.Current.CancellationToken));
        var offStatus = await off.GetStringAsync("/api/status", TestContext.Current.CancellationToken);

        Assert.True(onStatus.RootElement.GetProperty("disclosures").GetBoolean());
        Assert.DoesNotContain("disclosures", offStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_list_comes_newest_period_first_with_withdrawn_ones_marked()
    {
        var hub = Hub();
        await SeedAsync(hub, "q2", "u2", "2026-06-30");
        await SeedAsync(hub, "q3", "u3", "2026-09-30");
        await Database(hub).WithdrawAsync("q2", "wrong quarter", "alice", NowMs, CancellationToken.None);
        using var client = hub.CreateClient();

        using var list =
            JsonDocument.Parse(await client.GetStringAsync("/api/disclosures", TestContext.Current.CancellationToken));

        var items = list.RootElement.EnumerateArray().ToArray();
        Assert.Equal(["q3", "q2"], items.Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("withdrawn").ValueKind);
        Assert.Equal("wrong quarter", items[1].GetProperty("withdrawn").GetProperty("reason").GetString());
        Assert.Equal(18.42, items[0].GetProperty("figures").GetProperty("energy_kwh").GetDouble());
        Assert.Equal("2026-09-30", items[0].GetProperty("period").GetProperty("to").GetString());
    }

    [Fact]
    public async Task The_detail_carries_the_checks_links_and_deliveries_never_a_url()
    {
        var hub = Hub();
        await SeedAsync(hub, "d1", "u1", "2026-09-30", [1]);
        await Database(hub).EnqueueDeliveriesAsync("d1", "published", ["auditors"], NowMs, CancellationToken.None);
        using var client = hub.CreateClient();
        using var created = await client.SendAsync(Manage("/api/disclosures/d1/share-links", "{}"),
            TestContext.Current.CancellationToken);

        var detail = await client.GetStringAsync("/api/disclosures/d1", TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(detail);
        var checks = document.RootElement.GetProperty("checks");
        Assert.Equal("checked", checks.GetProperty("content_hash").GetString());
        Assert.Equal("checked", checks.GetProperty("binding").GetString());
        // The Hub never says a signature is verified: it is declared, and checked by whoever runs verify-hash.
        Assert.Equal("declared", checks.GetProperty("signature").GetString());
        Assert.Equal("active", document.RootElement.GetProperty("share_links")[0].GetProperty("state").GetString());
        Assert.Equal("auditors", document.RootElement.GetProperty("deliveries")[0].GetProperty("webhook").GetString());
        Assert.DoesNotContain("127.0.0.1:9", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("pshare_", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_come_back_byte_for_byte_with_their_type_and_sha256sums_verifies()
    {
        var hub = Hub();
        await SeedAsync(hub, "d1", "u1", "2026-09-30", [1, 2], Encoding.ASCII.GetBytes("%PDF-1.7 x"));
        using var client = hub.CreateClient();

        using var report = await client.GetAsync("/api/disclosures/d1/files/report.json",
            TestContext.Current.CancellationToken);
        using var pdf =
            await client.GetAsync("/api/disclosures/d1/files/report.pdf", TestContext.Current.CancellationToken);
        using var bundle = await client.GetAsync("/api/disclosures/d1/files/bundle.sig",
            TestContext.Current.CancellationToken);
        using var other =
            await client.GetAsync("/api/disclosures/d1/files/hub.db", TestContext.Current.CancellationToken);
        var sums = await client.GetStringAsync("/api/disclosures/d1/files/SHA256SUMS",
            TestContext.Current.CancellationToken);

        Assert.Equal(ReportBytes, await report.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("application/json", report.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", report.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, bundle.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(
            $"{Convert.ToHexStringLower(SHA256.HashData(ReportBytes))}  report.json\n" +
            $"{Convert.ToHexStringLower(SHA256.HashData([1, 2]))}  attestation.intoto.jsonl\n" +
            $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("%PDF-1.7 x")))}  report.pdf\n",
            sums);
    }

    [Fact]
    public async Task A_share_link_is_shown_once_and_stored_as_a_hash()
    {
        var hub = Hub();
        await SeedAsync(hub, "d1", "u1", "2026-09-30");
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(
            Manage("/api/disclosures/d1/share-links", """{"days":3}"""), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var token = body.RootElement.GetProperty("token").GetString()!;
        Assert.StartsWith("pshare_", token, StringComparison.Ordinal);
        Assert.Equal($"/share/{token}/", body.RootElement.GetProperty("path").GetString());
        Assert.Equal(NowMs + 3 * 86_400_000L, body.RootElement.GetProperty("expires_at_ms").GetInt64());
        Assert.NotNull(await Database(hub).FindShareLinkAsync(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)), NowMs, CancellationToken.None));
    }

    [Theory]
    [InlineData("internal", false, """{"days":3}""", HttpStatusCode.UnprocessableEntity)]
    [InlineData("restricted", false, """{"days":3}""", HttpStatusCode.UnprocessableEntity)]
    [InlineData("public", true, """{"days":3}""", HttpStatusCode.Conflict)]
    [InlineData("public", false, """{"days":31}""", HttpStatusCode.BadRequest)]
    [InlineData("public", false, """{"days":0}""", HttpStatusCode.BadRequest)]
    public async Task A_share_link_is_refused_on_an_internal_or_withdrawn_report_and_past_30_days(
        string confidentiality,
        bool withdrawn,
        string body,
        HttpStatusCode expected)
    {
        var hub = Hub();
        await SeedAsync(hub, "d1", "u1", "2026-09-30", confidentiality: confidentiality);
        if (withdrawn)
            await Database(hub).WithdrawAsync("d1", "wrong", "alice", NowMs, CancellationToken.None);
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(Manage("/api/disclosures/d1/share-links", body),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.UnprocessableEntity)
            Assert.Equal("""{"reason":"internal_not_shareable"}""",
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_share_link_is_revoked_once()
    {
        var hub = Hub();
        await SeedAsync(hub, "d1", "u1", "2026-09-30");
        using var client = hub.CreateClient();
        using var created = await client.SendAsync(Manage("/api/disclosures/d1/share-links", "{}"),
            TestContext.Current.CancellationToken);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.GetProperty("id").GetString();

        using var first = await client.SendAsync(
            Manage($"/api/disclosures/d1/share-links/{id}", "{}", HttpMethod.Delete),
            TestContext.Current.CancellationToken);
        using var second = await client.SendAsync(
            Manage($"/api/disclosures/d1/share-links/{id}", "{}", HttpMethod.Delete),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Theory]
    [InlineData(true, "auditors:published,auditors:withdrawn")]
    [InlineData(false, "")]
    public async Task A_withdrawal_tells_only_the_webhooks_that_were_told_of_the_publication(bool published,
        string expected)
    {
        var hub = Hub();
        await SeedAsync(hub, "d1", "u1", "2026-09-30");
        if (published)
            await Database(hub).EnqueueDeliveriesAsync("d1", "published", ["auditors"], NowMs, CancellationToken.None);
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(
            Manage("/api/disclosures/d1/withdraw", """{"reason":"replaced by Q3 v2"}"""),
            TestContext.Current.CancellationToken);
        using var again = await client.SendAsync(
            Manage("/api/disclosures/d1/withdraw", """{"reason":"again"}"""), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var summary = (await Database(hub).GetDisclosureAsync("d1", CancellationToken.None))!;
        Assert.Equal("alice", summary.WithdrawnBy);
        Assert.Equal(expected, string.Join(',',
            (await Database(hub).ListDeliveriesAsync("d1", CancellationToken.None))
            .Select(d => $"{d.Webhook}:{d.Event}")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"reason":""}""")]
    [InlineData("""{"reason":7}""")]
    public async Task A_withdrawal_needs_a_reason(string body)
    {
        var hub = Hub();
        await SeedAsync(hub, "d1", "u1", "2026-09-30");
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(Manage("/api/disclosures/d1/withdraw", body),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await Database(hub).GetDisclosureAsync("d1", CancellationToken.None))!.Withdrawn);
    }

    [Fact]
    public async Task Rendering_again_turns_not_checked_into_rendered_once_the_engine_can()
    {
        var hub = Hub(new FakeEngine { DisclosePdf = true, DisclosePdfOutput = """{"result":"rendered"}""" });
        await SeedAsync(hub, "d1", "u1", "2026-09-30", pdf: null, renderReason: "unsupported_engine");
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(Manage("/api/disclosures/d1/render", "{}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = (await Database(hub).GetDisclosureAsync("d1", CancellationToken.None))!;
        Assert.True(summary.HasPdf);
        Assert.Null(summary.RenderReason);
    }

    [Fact]
    public async Task Rendering_again_is_503_while_an_import_holds_the_engine()
    {
        var hub = Hub(new FakeEngine { DisclosePdf = true, DisclosePdfOutput = """{"result":"rendered"}""" });
        await SeedAsync(hub, "d1", "u1", "2026-09-30", pdf: null, renderReason: "unsupported_engine");
        var gate = hub.Services.GetRequiredService<DisclosureImportGate>();
        Assert.True(gate.TryEnter());
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(Manage("/api/disclosures/d1/render", "{}"),
            TestContext.Current.CancellationToken);

        gate.Exit();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False((await Database(hub).GetDisclosureAsync("d1", CancellationToken.None))!.HasPdf);
    }

    [Fact]
    public async Task A_render_again_the_engine_refuses_is_422_and_stays_on_the_disclosure()
    {
        var hub = Hub(new FakeEngine
        {
            DisclosePdf = true,
            DisclosePdfExitCode = 1,
            DisclosePdfOutput = """{"result":"refused","reason":"content_hash_mismatch"}"""
        });
        await SeedAsync(hub, "d1", "u1", "2026-09-30", pdf: null, renderReason: "unsupported_engine");
        using var client = hub.CreateClient();

        using var response = await client.SendAsync(Manage("/api/disclosures/d1/render", "{}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var summary = (await Database(hub).GetDisclosureAsync("d1", CancellationToken.None))!;
        Assert.Equal("content_hash_mismatch", summary.RenderReason);
        Assert.False(summary.HashChecked);
    }

    [Fact]
    public async Task The_links_of_a_withdrawn_disclosure_read_withdrawn()
    {
        var hub = Hub();
        await SeedAsync(hub, "d1", "u1", "2026-09-30");
        using var client = hub.CreateClient();
        (await client.SendAsync(Manage("/api/disclosures/d1/share-links", "{}"), TestContext.Current.CancellationToken))
            .Dispose();
        await Database(hub).WithdrawAsync("d1", "wrong", "alice", NowMs, CancellationToken.None);

        using var detail =
            JsonDocument.Parse(
                await client.GetStringAsync("/api/disclosures/d1", TestContext.Current.CancellationToken));

        Assert.Equal("withdrawn", detail.RootElement.GetProperty("share_links")[0].GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task The_detail_says_whether_the_caller_may_act_on_it(bool named, bool expected)
    {
        var hub = Hub();
        await SeedAsync(hub, "d1", "u1", "2026-09-30");
        using var client = hub.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/disclosures/d1");
        if (named)
            request.Headers.TryAddWithoutValidation("X-Forwarded-User", "alice");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        using var detail =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, detail.RootElement.GetProperty("can_manage").GetBoolean());
    }

    [Fact]
    public async Task A_failed_delivery_is_retried_and_a_delivered_one_is_not()
    {
        var hub = Hub();
        await SeedAsync(hub, "d1", "u1", "2026-09-30");
        var database = Database(hub);
        await database.EnqueueDeliveriesAsync("d1", "published", ["auditors", "mail"], NowMs, CancellationToken.None);
        var deliveries = await database.ListDeliveriesAsync("d1", CancellationToken.None);
        await database.RecordDeliveryAttemptAsync(deliveries[0].Id, 0, 500, DeliveryStates.Failed, NowMs, null,
            CancellationToken.None);
        await database.RecordDeliveryAttemptAsync(deliveries[1].Id, 0, 204, DeliveryStates.Delivered, NowMs, null,
            CancellationToken.None);
        using var client = hub.CreateClient();

        using var failed = await client.SendAsync(
            Manage($"/api/disclosures/d1/deliveries/{deliveries[0].Id}/retry", "{}"),
            TestContext.Current.CancellationToken);
        using var delivered = await client.SendAsync(
            Manage($"/api/disclosures/d1/deliveries/{deliveries[1].Id}/retry", "{}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delivered.StatusCode);
        // A retry starts a fresh run of attempts, or a delivery that gave up would give up again at once.
        Assert.Equal(0, Assert.Single(await database.DueDeliveriesAsync(NowMs, CancellationToken.None)).Attempts);
    }

    [Fact]
    public async Task A_manage_action_needs_someone_to_name_and_a_same_origin_json_request()
    {
        var hub = Hub();
        await SeedAsync(hub, "d1", "u1", "2026-09-30");
        using var client = hub.CreateClient();
        var unnamed = Manage("/api/disclosures/d1/withdraw", """{"reason":"x"}""");
        unnamed.Headers.Remove("X-Forwarded-User");
        var foreign = Manage("/api/disclosures/d1/withdraw", """{"reason":"x"}""");
        foreign.Headers.Add("Sec-Fetch-Site", "cross-site");

        using var first = await client.SendAsync(unnamed, TestContext.Current.CancellationToken);
        using var second = await client.SendAsync(foreign, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.False((await Database(hub).GetDisclosureAsync("d1", CancellationToken.None))!.Withdrawn);
    }

    private WebApplicationFactory<Program> Hub(FakeEngine? engine = null, string? importKey = ImportKey)
    {
        var enginePath = ((engine ?? new FakeEngine()) with { Version = "perf-sentinel 0.31.0" })
            .WriteTo(Path.Combine(_workspace, $"engine-{Guid.NewGuid():N}"));
        var hub = new HubApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
            {
                options.AckRelay = new AckRelayOptions { TrustIdentityHeader = true };
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
                            Secret =
                                "webhook-secret-0123456789abcdef0123" // gitleaks:allow -- synthetic test credential
                        }
                    ]
                };
            })));
        _hubs.Add(hub);
        return hub;
    }

    internal static Task SeedAsync(
        WebApplicationFactory<Program> hub,
        string id,
        string uuid,
        string periodTo,
        byte[]? attestation = null,
        byte[]? pdf = null,
        string? renderReason = null,
        string confidentiality = "public")
    {
        pdf ??= renderReason is null ? [37, 80, 68, 70] : null;
        return Database(hub).InsertDisclosureAsync(
            new DisclosureRecord(
                id, uuid, "Example SAS", "calendar-quarter", "2026-07-01", periodTo, "official", confidentiality,
                "sha256:aaaa", "signed",
                """{"energy_kwh":18.42,"carbon_kgco2eq":1.91,"avoidable_ratio":0.18,"coverage":0.91,"applications":[]}""",
                new DisclosureFiles(ReportBytes, attestation, null), pdf, renderReason, "0.31.0",
                pdf is null ? null : true, pdf is null || attestation is null ? null : true, NowMs),
            CancellationToken.None);
    }

    private static HubDatabase Database(WebApplicationFactory<Program> hub)
    {
        return hub.Services.GetRequiredService<HubDatabase>();
    }

    private static HttpRequestMessage Manage(string path, string body, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Forwarded-User", "alice");
        return request;
    }
}
