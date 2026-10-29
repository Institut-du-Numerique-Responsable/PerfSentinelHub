using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     The frozen-report routes with access off: every caller is an admin and the identity is the proxy
///     header. Each test owns its clock, so one may stand on 1970-01-08 while another stands on day 0.
///     AccessRoutesTests owns what each role may read.
/// </summary>
public sealed partial class RecapReportsApiTests(HubApplicationFactory factory)
    : IClassFixture<HubApplicationFactory>, IAsyncLifetime
{
    // HubJsonContext is internal to the Hub; its options are snake_case_lower, which these mirror.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly List<WebApplicationFactory<Program>> _hubs = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hub in _hubs)
            await hub.DisposeAsync();
    }

    [Fact]
    public async Task An_on_demand_report_needs_an_identity()
    {
        using var client = Client(7);

        using var response = await PostAsync(client, """{"period":"week"}""", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The Hub does not know who is asking, and a report is generated in somebody's name.",
            await DetailAsync(response));
    }

    [Fact]
    public async Task An_on_demand_report_is_stored_and_listed()
    {
        using var client = Client(7);

        using var response = await PostAsync(client, """{"period":"day","at":"1970-01-01"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = (await JsonAsync(response)).RootElement;
        var id = item.GetProperty("id").GetString()!;
        Assert.Matches(Hex32(), id);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("subscription").ValueKind);
        Assert.Equal("alice", item.GetProperty("created_by").GetString());
        Assert.Equal(["pilotage", "tech"], item.GetProperty("views").EnumerateArray().Select(view => view.GetString()));
        Assert.Empty(item.GetProperty("deliveries").EnumerateArray());
        Assert.Equal("day", item.GetProperty("period").GetProperty("kind").GetString());
        Assert.Equal("1970-01-01", item.GetProperty("period").GetProperty("from").GetString());
        using var list = await client.SendAsync(Get("/api/recap/reports"), Token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listed = (await JsonAsync(list)).RootElement.EnumerateArray().ToList();
        Assert.Contains(listed, entry => entry.GetProperty("id").GetString() == id);
        using var detail = await client.SendAsync(Get($"/api/recap/reports/{id}"), Token);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var snapshot = (await JsonAsync(detail)).RootElement.GetProperty("snapshot");
        Assert.Equal("tech", snapshot.GetProperty("view").GetString());
    }

    [Fact]
    public async Task The_default_is_the_last_closed_period()
    {
        using var client = Client(7);

        using var response = await PostAsync(client, """{"period":"week"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var period = (await JsonAsync(response)).RootElement.GetProperty("period");
        Assert.Equal("week", period.GetProperty("kind").GetString());
        Assert.Equal("1969-12-29", period.GetProperty("from").GetString());
        Assert.Equal("1970-01-04", period.GetProperty("to").GetString());
    }

    [Fact]
    public async Task A_period_long_before_any_data_is_an_empty_report()
    {
        using var client = Client(7);

        using var response = await PostAsync(client, """{"period":"month","at":"1900-06-15"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True((await JsonAsync(response)).RootElement.GetProperty("nothing_moved").GetBoolean());
    }

    [Fact]
    public async Task An_open_period_is_a_400()
    {
        using var client = Client(0);

        using var response = await PostAsync(client, """{"period":"day","at":"1970-01-01"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("A report covers a closed period; pick a date in an earlier one.", await DetailAsync(response));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"period":"year"}""")]
    [InlineData("""{"period":"day","at":"1970-13-01"}""")]
    [InlineData("""{"period":"month","at":"0001-01-01"}""")]
    [InlineData("""{"period":"week","environment":"nowhere"}""")]
    [InlineData("""{"period":"week","service":"bad\u0001"}""")]
    public async Task A_bad_body_is_a_400(string body)
    {
        using var client = Client(7);

        using var response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_body_over_4_KiB_is_a_413()
    {
        using var client = Client(7);

        using var response =
            await PostAsync(client, $$"""{"period":"week","service":"{{new string('a', 5 * 1024)}}"}""");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_cross_site_post_is_refused()
    {
        using var client = Client(7);

        using var response = await PostAsync(client, """{"period":"week"}""", "alice", "cross-site");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_detail_serves_the_snapshot_verbatim()
    {
        using var client = Client(7);
        const string stored = """{"schema_version":1,"marker":"verbatim","ratio":1.50,"list":[1,2],"none":null}""";
        var summary = Summary([RecapViewNames.Pilotage, RecapViewNames.Tech]);
        var id = await StoreAsync(summary, stored);

        using var response = await client.SendAsync(Get($"/api/recap/reports/{id}"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = (await JsonAsync(response)).RootElement;
        Assert.Equal(stored, root.GetProperty("snapshot").GetRawText());
        Assert.Equal(summary.Subscription, root.GetProperty("report").GetProperty("subscription").GetString());
    }

    [Fact]
    public async Task The_html_page_carries_its_csp_and_nosniff()
    {
        using var client = Client(7);
        var id = await StoreAsync(Summary([RecapViewNames.Pilotage, RecapViewNames.Tech]));

        using var response = await client.SendAsync(Get($"/recaps/{id}/tech.html"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(RecapReportRenderer.ContentSecurityPolicy,
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-store", Assert.Single(response.Headers.GetValues("Cache-Control")));
        Assert.Equal("inline; filename=\"perf-sentinel-recap-2026-10-19-tech.html\"",
            response.Content.Headers.ContentDisposition!.ToString());
        Assert.Contains(RecapSnapshots.Signature, await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task The_pilotage_page_is_the_management_projection()
    {
        using var client = Client(7);
        var id = await StoreAsync(Summary([RecapViewNames.Pilotage, RecapViewNames.Tech]));

        using var response = await client.SendAsync(Get($"/recaps/{id}/pilotage.html"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Token);
        Assert.Contains("Label OK", html);
        Assert.DoesNotContain(RecapSnapshots.Signature, html);
        Assert.DoesNotContain(RecapSnapshots.Template, html);
    }

    [Fact]
    public async Task A_view_the_report_does_not_hold_is_a_404()
    {
        using var client = Client(7);
        var id = await StoreAsync(Summary([RecapViewNames.Pilotage]));

        using var held = await client.SendAsync(Get($"/recaps/{id}/pilotage.html"), Token);
        using var missing = await client.SendAsync(Get($"/recaps/{id}/tech.html"), Token);
        using var unknown = await client.SendAsync(Get($"/recaps/{id}/other.html"), Token);

        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Theory]
    [InlineData("not-a-report-id")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public async Task An_id_that_is_not_hex_or_not_held_is_a_404(string id)
    {
        using var client = Client(7);

        using var page = await client.SendAsync(Get($"/recaps/{id}/tech.html"), Token);
        using var detail = await client.SendAsync(Get($"/api/recap/reports/{id}"), Token);

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task A_report_lists_its_deliveries()
    {
        using var client = Client(7);
        var summary = Summary([RecapViewNames.Pilotage]);
        Assert.True(await factory.Database.InsertRecapReportAsync(
            new RecapReportRecord(summary, JsonSerializer.Serialize(RecapSnapshots.Distinctive(), Wire)),
            [(RecapChannels.Webhook, "ops-chat"), (RecapChannels.Email, "boss@example.test")], Token));

        using var response = await client.SendAsync(Get($"/api/recap/reports/{summary.Id}"), Token);

        var deliveries = (await JsonAsync(response)).RootElement.GetProperty("report").GetProperty("deliveries");
        Assert.Equal(["webhook ops-chat pending", "email boss@example.test pending"],
            deliveries.EnumerateArray().Select(delivery =>
                $"{delivery.GetProperty("channel").GetString()} {delivery.GetProperty("target").GetString()} " +
                delivery.GetProperty("state").GetString()));
    }

    private HttpClient Client(int daysAfterEpoch)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(10_000).AddDays(daysAfterEpoch));
        var hub = AckRelayApiTests.Scoped(factory, true, TimeSpan.FromSeconds(10)).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            }));
        _hubs.Add(hub);
        return hub.CreateClient();
    }

    // The class shares one database, and a subscription holds one report per period.
    private static RecapReportSummary Summary(IReadOnlyList<string> views)
    {
        var id = Guid.NewGuid().ToString("N");
        return new RecapReportSummary(
            id, $"weekly-{id}", "week", "2026-10-19", "2026-10-25", views, true, false, null, 5_000);
    }

    private Task<string> StoreAsync(RecapReportSummary summary)
    {
        return StoreAsync(summary, JsonSerializer.Serialize(RecapSnapshots.Distinctive(), Wire));
    }

    private async Task<string> StoreAsync(RecapReportSummary summary, string snapshot)
    {
        Assert.True(await factory.Database.InsertRecapReportAsync(new RecapReportRecord(summary, snapshot), [], Token));
        return summary.Id;
    }

    private static HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("X-Forwarded-User", "alice");
        return request;
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string body,
        string? identity = "alice",
        string? site = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/recap/reports")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (identity is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-User", identity);
        if (site is not null)
            request.Headers.Add("Sec-Fetch-Site", site);
        return client.SendAsync(request, Token);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response)
    {
        using var document = await JsonAsync(response);
        return document.RootElement.GetProperty("detail").GetString()!;
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex Hex32();
}
