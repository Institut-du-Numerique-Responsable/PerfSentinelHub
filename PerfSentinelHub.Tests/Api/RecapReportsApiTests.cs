using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Localization;
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
    public async Task A_held_write_lock_delays_the_frozen_report_past_the_request_wait_rather_than_losing_it()
    {
        using var client = Client(7);
        // No public seam holds the write lock. Held past WriteGateWait (5 s), where a request's write gives up.
        var database = _hubs[^1].Services.GetRequiredService<HubDatabase>();
        var writeGate = (SemaphoreSlim)typeof(HubDatabase)
            .GetField("_writeGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(database)!;
        var before = (await database.ListRecapReportsAsync(null, 1000, Token)).Count;
        await writeGate.WaitAsync(Token);
        Task<HttpResponseMessage> posting;
        try
        {
            posting = PostAsync(client, """{"period":"day","at":"1970-01-01"}""");
            await Task.Delay(TimeSpan.FromSeconds(6), Token);
            Assert.False(posting.IsCompleted);
        }
        finally
        {
            writeGate.Release();
        }

        using var response = await posting;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(before + 1, (await database.ListRecapReportsAsync(null, 1000, Token)).Count);
    }

    [Fact]
    public async Task An_on_demand_report_keeps_its_language()
    {
        using var client = Client(7);

        using var french = await PostAsync(client, """{"period":"day","at":"1970-01-01","lang":"fr"}""");
        using var plain = await PostAsync(client, """{"period":"day","at":"1970-01-02"}""");
        using var unknown = await PostAsync(client, """{"period":"day","at":"1970-01-01","lang":"de"}""");

        Assert.Equal(HttpStatusCode.Created, french.StatusCode);
        var item = (await JsonAsync(french)).RootElement;
        Assert.Equal("fr", item.GetProperty("language").GetString());
        Assert.Equal("en", (await JsonAsync(plain)).RootElement.GetProperty("language").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var id = item.GetProperty("id").GetString()!;
        using var page = await client.SendAsync(Get($"/recaps/{id}/pilotage.html"), Token);
        Assert.StartsWith("<!doctype html>\n<html lang=\"fr\"", await page.Content.ReadAsStringAsync(Token),
            StringComparison.Ordinal);
        Assert.Equal(RecapReportRenderer.ContentSecurityPolicyFor(HubLang.Fr),
            Assert.Single(page.Headers.GetValues("Content-Security-Policy")));
        // A link may ask for the other language: the text is rendered at read time.
        using var english = await client.SendAsync(Get($"/recaps/{id}/pilotage.html?lang=en"), Token);
        Assert.StartsWith("<!doctype html>\n<html lang=\"en\"", await english.Content.ReadAsStringAsync(Token),
            StringComparison.Ordinal);
        using var detail = await client.SendAsync(Get($"/api/recap/reports/{id}"), Token);
        Assert.Equal("fr",
            (await JsonAsync(detail)).RootElement.GetProperty("report").GetProperty("language").GetString());
        using var list = await client.SendAsync(Get("/api/recap/reports"), Token);
        Assert.Contains((await JsonAsync(list)).RootElement.EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == id && entry.GetProperty("language").GetString() == "fr");
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
    [InlineData("""{"period":"week","service":"a","services":["a"]}""")]
    [InlineData("""{"period":"week","grouping":"g","groupings":["g"]}""")]
    [InlineData("""{"period":"week","services":[1]}""")]
    [InlineData("""{"period":"week","services":["a",null]}""")]
    [InlineData("""{"period":"week","services":[["a"]]}""")]
    [InlineData("""{"period":"week","services":"a"}""")]
    [InlineData("""{"period":"week","groupings":{"a":1}}""")]
    [InlineData("""{"period":"week","service":["a"]}""")]
    [InlineData("""{"period":"week","services":[""]}""")]
    [InlineData("""{"period":"week","groupings":["bad\u0001"]}""")]
    public async Task A_bad_body_is_a_400(string body)
    {
        using var client = Client(7);

        using var response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_body_over_32_KiB_is_a_413()
    {
        using var client = Client(7);

        using var response =
            await PostAsync(client, $$"""{"period":"week","service":"{{new string('a', 33 * 1024)}}"}""");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // Both lists at their cap, 50 values of 256 characters each, are a body the Hub reads.
    [Fact]
    public async Task Both_lists_at_their_cap_fit_the_body_limit()
    {
        using var client = Client(7);
        var names = string.Join(',', Enumerable.Range(0, 50).Select(index => $"\"{index:D3}{new string('a', 253)}\""));

        using var response = await PostAsync(
            client, $$"""{"period":"day","at":"1970-01-01","services":[{{names}}],"groupings":[{{names}}]}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("services", 51, 5)]
    [InlineData("groupings", 51, 5)]
    [InlineData("services", 1, 257)]
    [InlineData("groupings", 2, 257)]
    public async Task A_list_past_its_cap_is_a_400(string name, int count, int length)
    {
        using var client = Client(7);
        var values = string.Join(',', Enumerable.Range(0, count).Select(index =>
            $"\"{index:D3}{new string('a', Math.Max(length - 3, 0))}\""));

        using var response = await PostAsync(client, $$"""{"period":"week","{{name}}":[{{values}}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_report_takes_one_service_as_a_string_or_a_list_and_several_as_a_list()
    {
        using var client = Client(7);
        const string day = "\"period\":\"day\",\"at\":\"1970-01-01\"";

        using var text = await PostAsync(client, $$"""{ {{day}},"service":"rep-a","grouping":"rep-x"}""");
        using var listOfOne = await PostAsync(client, $$"""{ {{day}},"services":["rep-a"],"groupings":["rep-x"]}""");
        using var several = await PostAsync(client,
            $$"""{ {{day}},"service":null,"services":["rep-b","rep-a","rep-a"],"groupings":["rep-y","rep-x"]}""");
        var one = await FiltersAsync(client, text);
        var alsoOne = await FiltersAsync(client, listOfOne);
        var many = await FiltersAsync(client, several);

        // One value reads as it did before the lists: strings, and no key for the lists.
        Assert.Equal("rep-a", one.GetProperty("service").GetString());
        Assert.Equal("rep-x", one.GetProperty("grouping").GetString());
        Assert.False(one.TryGetProperty("services", out _));
        Assert.False(one.TryGetProperty("groupings", out _));
        Assert.Equal(one.GetRawText(), alsoOne.GetRawText());
        Assert.Equal(JsonValueKind.Null, many.GetProperty("service").ValueKind);
        Assert.Equal(JsonValueKind.Null, many.GetProperty("grouping").ValueKind);
        Assert.Equal(["rep-a", "rep-b"], many.GetProperty("services").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(["rep-x", "rep-y"], many.GetProperty("groupings").EnumerateArray().Select(v => v.GetString()));
    }

    // Stored before the lists, a report has five keys in its filters. It reads through the Hub's own
    // serializer and renders the same words as one written now with the same choices.
    [Fact]
    public async Task A_report_stored_in_the_old_shape_reads_and_renders_as_it_did()
    {
        using var client = Client(7);
        var snapshot = RecapSnapshots.Distinctive() with
        {
            Filters = new RecapFilters("prod", null, "checkout-api", null, "Payment")
        };
        var current = JsonSerializer.Serialize(snapshot, Wire);
        var root = JsonNode.Parse(current)!;
        root["filters"] = JsonNode.Parse(
            """{"environment":"prod","source_id":null,"service":"checkout-api","grouping":null,"service_label":"Payment"}""");
        var old = root.ToJsonString();
        Assert.DoesNotContain("services", old, StringComparison.Ordinal);
        var oldId = await StoreAsync(Summary([RecapViewNames.Pilotage, RecapViewNames.Tech]), old);
        var currentId = await StoreAsync(Summary([RecapViewNames.Pilotage, RecapViewNames.Tech]), current);

        foreach (var view in new[] { "tech", "pilotage" })
        {
            using var oldPage = await client.SendAsync(Get($"/recaps/{oldId}/{view}.html"), Token);
            using var currentPage = await client.SendAsync(Get($"/recaps/{currentId}/{view}.html"), Token);
            Assert.Equal(HttpStatusCode.OK, oldPage.StatusCode);
            Assert.Equal(await currentPage.Content.ReadAsStringAsync(Token),
                await oldPage.Content.ReadAsStringAsync(Token));
        }

        using var tech = await client.SendAsync(Get($"/recaps/{oldId}/tech.html"), Token);
        Assert.Contains("Scope: environment prod, service checkout-api",
            await tech.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        using var detail = await client.SendAsync(Get($"/api/recap/reports/{oldId}"), Token);
        Assert.Equal(old, (await JsonAsync(detail)).RootElement.GetProperty("snapshot").GetRawText());
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

    private static async Task<JsonElement> FiltersAsync(HttpClient client, HttpResponseMessage created)
    {
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).RootElement.GetProperty("id").GetString()!;
        using var detail = await client.SendAsync(Get($"/api/recap/reports/{id}"), Token);
        return (await JsonAsync(detail)).RootElement.GetProperty("snapshot").GetProperty("filters").Clone();
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
