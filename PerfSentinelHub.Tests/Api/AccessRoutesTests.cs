using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;
using PerfSentinelHub.Tests.Support;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     Hub:Access:Enabled on, against a fake provider that hands out a subject and groups,
///     two fake daemons that file every request, a trace backend that is never called and a
///     seeded store. Each test gets a fresh Hub: an ack relay re-reads a daemon's mirror and
///     would empty the seeded one for the next test.
/// </summary>
public sealed class AccessRoutesTests : IAsyncLifetime
{
    private const string
        CiToken = "ci-shop-0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential

    private const string
        CiAdminToken = "ci-all-0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential

    private const string
        CiOldToken = "ci-old-0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential

    // F1 is cart at shop-d under team=shop and at lab-d under team=billing; F2 invoice under
    // team=billing; F3 cart with no grouping; F4 cart seen under team=shop and under no
    // grouping; F5 checkout under team=shop at shop-d and team=billing at lab-d.
    private const string F1 = "slow_sql:cart:0000000000000000000000000000000000000001";
    private const string F2 = "slow_sql:invoice:0000000000000000000000000000000000000002";
    private const string F3 = "slow_sql:cart:0000000000000000000000000000000000000003";
    private const string F4 = "slow_sql:cart:0000000000000000000000000000000000000004";
    private const string F5 = "slow_sql:checkout:0000000000000000000000000000000000000005";

    // Seeded by the detection volume tests alone: cart at lab-d, counted elsewhere too.
    private const string F6 = "slow_sql:cart:0000000000000000000000000000000000000006";
    private const string Orphan = "slow_sql:gone:0000000000000000000000000000000000000009";
    private const string Incident = "0000000000000000000000000000000a";
    private const string MixedIncident = "0000000000000000000000000000000b";

    private const string
        ImportKey = "lab-import-0123456789abcdef0123456789"; // gitleaks:allow -- synthetic test credential

    private const string
        DisclosureKey = "disclosure-import-0123456789abcdef0123"; // gitleaks:allow -- synthetic test credential

    private static readonly Dictionary<string, string> Users = new(StringComparer.Ordinal)
    {
        ["admin"] = """{"sub":"u-admin","email":"admin@example.internal","groups":[]}""",
        ["svc"] = """{"sub":"u-svc","email":"svc@example.internal","groups":["/svc"]}""",
        ["attr"] =
            """{"sub":"u-attr","email":"attr@example.internal","groups":["/shop","/unrelated","INTERNAL:ci",7]}""",
        ["ss"] = """{"sub":"u-ss","email":"ss@example.internal","groups":"/ss"}""",
        ["lab"] = """{"sub":"u-lab","email":"lab@example.internal","groups":["/lab"]}""",
        ["tempo"] = """{"sub":"u-tempo","email":"tempo@example.internal","groups":["/tempo"]}""",
        // attr's e-mail under another subject, as a reassigned address would be.
        ["attrtwin"] = """{"sub":"u-attr-2","email":"attr@example.internal","groups":["/shop"]}""",
        ["viewer"] = """{"sub":"u-view","email":"view@example.internal","groups":["/viewers"]}""",
        ["tracer"] = """{"sub":"u-tracer","email":"tracer@example.internal","groups":["/tracers"]}""",
        ["nogrant"] = """{"sub":"u-none","email":"none@example.internal","groups":42}""",
        ["nosub"] = """{"email":"nosub@example.internal","groups":["/shop"]}""",
        ["numsub"] = """{"sub":7,"email":"num@example.internal","groups":["/shop"]}""",
        ["blanksub"] = """{"sub":" ","email":"blank@example.internal","groups":["/shop"]}""",
        ["localsub"] = """{"sub":"Local:ops","email":"local@example.internal","groups":["/shop"]}""",
        ["machineid"] = """{"sub":"u-m","email":"MACHINE:ci","groups":["/shop"]}"""
    };

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly HubApplicationFactory _hub = new();

    private readonly ConcurrentQueue<AckRelayApiTests.DaemonCall> _labCalls = new();

    private readonly string _reports =
        Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-reports-{Guid.NewGuid():N}");

    private readonly ConcurrentQueue<AckRelayApiTests.DaemonCall> _shopCalls = new();
    private FakeDaemon _labDaemon = null!;
    private FakeProvider _provider = null!;
    private FakeDaemon _shopDaemon = null!;

    private WebApplicationFactory<Program> Factory { get; set; } = null!;

    private HubDatabase Database => Factory.Services.GetRequiredService<HubDatabase>();

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_reports);
        _provider = await FakeProvider.StartAsync(Users);
        _shopDaemon = await AckRelayApiTests.DaemonAsync(_shopCalls, StatusCodes.Status201Created);
        _labDaemon = await AckRelayApiTests.DaemonAsync(_labCalls, StatusCodes.Status201Created);
        Factory = Hub();
        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _hub.DisposeAsync();
        await _provider.DisposeAsync();
        await _shopDaemon.DisposeAsync();
        await _labDaemon.DisposeAsync();
        Directory.Delete(_reports, true);
    }

    // ------------------------------------------------------------ no credential

    [Theory]
    [InlineData("GET", "/api/status")]
    [InlineData("GET", "/api/sources")]
    [InlineData("GET", "/api/sources/shop-d/daemon")]
    [InlineData("GET", "/api/findings")]
    [InlineData("GET", "/api/findings/trace-x")]
    [InlineData("GET", "/api/incidents")]
    [InlineData("GET", "/api/incidents/" + Incident)]
    [InlineData("POST", "/api/incidents/refresh")]
    [InlineData("GET", "/api/correlations")]
    [InlineData("POST", "/api/sources/shop-d/acks")]
    [InlineData("GET", "/api/sources/shop-d/acks")]
    [InlineData("POST", "/api/actions")]
    [InlineData("PATCH", "/api/actions")]
    [InlineData("PUT", "/api/labels")]
    [InlineData("GET", "/api/recap")]
    [InlineData("GET", "/api/recap/reports")]
    [InlineData("POST", "/api/recap/reports")]
    [InlineData("GET", "/api/recap/reports/0123456789abcdef0123456789abcdef")]
    [InlineData("GET", "/recaps/0123456789abcdef0123456789abcdef/tech.html")]
    [InlineData("GET", "/api/disclosures")]
    [InlineData("GET", "/api/analyses")]
    [InlineData("POST", "/api/analyses")]
    [InlineData("GET", "/api/analyses/0123456789abcdef")]
    [InlineData("GET", "/api/access")]
    [InlineData("GET", "/reports/0123456789abcdef.html")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/app.js")]
    public async Task Every_route_needs_a_credential(string method, string path)
    {
        using var client = FakeProvider.Client(Factory);

        using var response = await client.SendAsync(Request(method, path, "{}"), TestContext.Current.CancellationToken);

        Assert.Equal(
            path.StartsWith("/api", StringComparison.Ordinal) ? HttpStatusCode.Unauthorized : HttpStatusCode.Redirect,
            response.StatusCode);
    }

    [Theory]
    [InlineData("/health/live", HttpStatusCode.OK)]
    [InlineData("/health/ready", HttpStatusCode.OK)]
    public async Task Health_stays_anonymous(string path, HttpStatusCode expected)
    {
        using var client = FakeProvider.Client(Factory);

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task The_import_still_answers_to_its_key_alone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = FakeProvider.Client(Factory);
        using var request = Request("POST", "/api/import/findings?source_id=lab-d", ImportBody(F2, "invoice", "[]"));
        request.Headers.Add("X-API-Key", ImportKey);

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------ status and sources

    [Fact]
    public async Task Status_answers_403_to_a_caller_no_grant_reaches()
    {
        using var client = await SignedInAsync("nogrant");

        using var response = await client.GetAsync("/api/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("No grant on this Hub reaches you.", await DetailAsync(response));
        using var sources = await client.GetAsync("/api/sources", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, sources.StatusCode);
    }

    // With access on every caller carries a name, so the header is never reached: no code
    // path ignores it, and these pin that status, requested_by and the ack by stay the caller's.
    [Fact]
    public async Task The_identity_header_names_nobody_in_status_requested_by_or_the_ack_by()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await SignedInAsync("attr");
        using var request = Request("GET", "/api/status");
        request.Headers.Add("X-Forwarded-User", "mallory@example.internal");
        using var run = Post("/api/analyses",
            """{"source_id":"tempo","request":{"service":"checkout","lookback":"1h"}}""");
        run.Headers.Add("X-Forwarded-User", "mallory@example.internal");
        using var ack = Post("/api/sources/shop-d/acks", AckBody(F5));
        ack.Headers.Add("X-Forwarded-User", "mallory@example.internal");

        using var response = await client.SendAsync(request, cancellationToken);
        using var started = await client.SendAsync(run, cancellationToken);
        using var acked = await client.SendAsync(ack, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal("attr@example.internal", payload.RootElement.GetProperty("identity").GetString());
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        using var row = await client.GetAsync(started.Headers.Location, cancellationToken);
        using var stored = JsonDocument.Parse(await row.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal("attr@example.internal", stored.RootElement.GetProperty("requested_by").GetString());
        Assert.Equal(HttpStatusCode.NoContent, acked.StatusCode);
        using var sent = JsonDocument.Parse(Assert.Single(_shopCalls, call => call.Method == "POST").Body);
        Assert.Equal("attr@example.internal", sent.RootElement.GetProperty("by").GetString());
    }

    [Theory]
    [InlineData("attr",
        "lab-d:findings.read,acks.write;shop-d:findings.read,acks.write;tempo:traces.read,analyses.run;test:findings.read,acks.write")]
    [InlineData("ss", "shop-d:findings.read,acks.write")]
    [InlineData("lab", "lab-d:findings.read,sources.read,analyses.run,acks.write")]
    [InlineData("tempo", "tempo:traces.read,analyses.run")]
    [InlineData("admin",
        "lab-d:findings.read,sources.read,analyses.run,acks.write;shop-d:findings.read,sources.read,analyses.run,acks.write;tempo:traces.read,analyses.run;test:findings.read,sources.read,analyses.run,acks.write")]
    public async Task Sources_lists_what_each_caller_may_do_at_each_source(string user, string expected)
    {
        using var client = await SignedInAsync(user);

        Assert.Equal(expected, await SourcePermissionsAsync(client));
    }

    // ------------------------------------------------------------ findings

    [Theory]
    [InlineData("svc", F4 + "," + F3 + "," + F1)]
    [InlineData("attr", F5 + "," + F4 + "," + F1)]
    [InlineData("ss", F4 + "," + F3 + "," + F1)]
    [InlineData("admin", F5 + "," + F4 + "," + F3 + "," + F2 + "," + F1)]
    public async Task Findings_list_only_what_the_caller_reads(string user, string expected)
    {
        using var client = await SignedInAsync(user);

        Assert.Equal(expected, string.Join(',', (await FindingsAsync(client, "/api/findings")).Select(Signature)));
    }

    [Fact]
    public async Task A_finding_carries_only_the_sources_and_acks_in_scope()
    {
        // F1 acked at both sources: ss reads the shop-d copy alone, svc reads both.
        await Database.ReplaceSourceAcksAsync(
            "lab-d",
            [
                new ParsedAck(F1, "daemon", "robin", "known", "2026-09-20T10:00:00Z", null, null),
                new ParsedAck(Orphan, "daemon", "robin", "known", "2026-09-20T10:00:00Z", null, null)
            ],
            AckReadStates.Ok, NowMs + 2, TestContext.Current.CancellationToken);
        using var client = await SignedInAsync("ss");

        foreach (var path in new[] { "/api/findings", "/api/findings/trace-x" })
        {
            var finding = Assert.Single(await FindingsAsync(client, path), item => Signature(item) == F1);
            var source = Assert.Single(finding.GetProperty("sources").EnumerateArray());
            Assert.Equal("shop-d", source.GetProperty("id").GetString());
            Assert.Equal("shop-d", Assert.Single(finding.GetProperty("acks").EnumerateArray())
                .GetProperty("source_id").GetString());
        }

        using var svc = await SignedInAsync("svc");
        foreach (var path in new[] { "/api/findings", "/api/findings/trace-x" })
        {
            var both = Assert.Single(await FindingsAsync(svc, path), item => Signature(item) == F1);
            Assert.Equal(2, both.GetProperty("sources").GetArrayLength());
            Assert.Equal(2, both.GetProperty("acks").GetArrayLength());
        }
    }

    [Fact]
    public async Task Every_page_of_a_restricted_caller_is_a_slice_of_its_full_list()
    {
        using var client = await SignedInAsync("attr");
        var full = (await FindingsAsync(client, "/api/findings")).Select(Signature).ToList();

        var paged = new List<string>();
        for (var offset = 0; offset < full.Count + 1; offset++)
            paged.AddRange((await FindingsAsync(client, $"/api/findings?limit=1&offset={offset}")).Select(Signature));

        Assert.Equal(full, paged);
    }

    [Fact]
    public async Task The_ack_grouping_and_window_filters_judge_only_copies_in_scope()
    {
        using var ss = await SignedInAsync("ss");
        using var svc = await SignedInAsync("svc");
        using var attr = await SignedInAsync("attr");

        // F1 is acked at shop-d alone: the only copy ss reads, one of the two svc reads.
        Assert.DoesNotContain(F1, (await FindingsAsync(ss, "/api/findings?include_acked=false")).Select(Signature));
        Assert.Contains(F1, (await FindingsAsync(svc, "/api/findings?include_acked=false")).Select(Signature));
        // team=billing is F1's grouping at lab-d, a copy attr does not read.
        Assert.DoesNotContain(F1, (await FindingsAsync(attr, "/api/findings?grouping=billing")).Select(Signature));
        Assert.Contains(F1, (await FindingsAsync(svc, "/api/findings?grouping=billing")).Select(Signature));
        Assert.Equal(3,
            (await FindingsAsync(attr, $"/api/findings?from={NowMs - 60_000}&to={NowMs}&status=active")).Count);
    }

    [Fact]
    public async Task The_window_and_the_status_judge_only_copies_in_scope()
    {
        // Past Hub:ResolutionGrace, F1 is seen again at lab-d alone: its shop-d copy, the only one attr
        // reads, is outside the window and no longer active.
        _clock.Advance(TimeSpan.FromDays(8));
        await SeedFindingAsync("lab-d", "lab", F1, "cart", """[{"key":"team","value":"billing"}]""");
        using var attr = await SignedInAsync("attr");
        using var svc = await SignedInAsync("svc");
        var window = $"/api/findings?from={NowMs - 60_000}&to={NowMs}";

        Assert.DoesNotContain(F1, (await FindingsAsync(attr, window)).Select(Signature));
        Assert.Contains(F1, (await FindingsAsync(svc, window)).Select(Signature));
        Assert.DoesNotContain(F1, (await FindingsAsync(attr, "/api/findings?status=active")).Select(Signature));
        Assert.Contains(F1, (await FindingsAsync(svc, "/api/findings?status=active")).Select(Signature));
    }

    [Fact]
    public async Task A_count_only_source_joins_only_for_a_caller_holding_the_whole_source()
    {
        // F6 has its one copy at lab-d and is counted at lab-d and at shop-d.
        await SeedFindingAsync("lab-d", "lab", F6, "cart", "[]");
        await RecordDetectionsAsync("lab-d", F6, 2);
        await RecordDetectionsAsync("shop-d", F6, 3);
        var window = $"/api/findings?from={NowMs - 60_000}&to={NowMs}";

        // lab holds lab-d whole, svc reads cart at every source: neither holds shop-d whole.
        foreach (var user in new[] { "lab", "svc" })
        {
            using var client = await SignedInAsync(user);
            var finding = Assert.Single(await FindingsAsync(client, window), item => Signature(item) == F6);
            Assert.Equal("lab-d",
                Assert.Single(finding.GetProperty("sources").EnumerateArray()).GetProperty("id").GetString());
            Assert.Equal(2, finding.GetProperty("detections").GetInt64());
        }

        using var admin = await SignedInAsync("admin");
        var whole = Assert.Single(await FindingsAsync(admin, window), item => Signature(item) == F6);
        Assert.Equal(2, whole.GetProperty("sources").GetArrayLength());
        Assert.Equal(5, whole.GetProperty("detections").GetInt64());
    }

    [Fact]
    public async Task A_count_admits_a_finding_to_a_window_only_where_the_caller_reads_a_copy()
    {
        // F6 seen at lab-d, then counted eight days later at shop-d alone, where it has no copy.
        await SeedFindingAsync("lab-d", "lab", F6, "cart", "[]");
        _clock.Advance(TimeSpan.FromDays(8));
        await RecordDetectionsAsync("shop-d", F6, 3);
        using var lab = await SignedInAsync("lab");
        using var admin = await SignedInAsync("admin");
        var window = $"/api/findings?from={NowMs - 60_000}&to={NowMs}";

        Assert.DoesNotContain(F6, (await FindingsAsync(lab, window)).Select(Signature));
        Assert.Contains(F6, (await FindingsAsync(admin, window)).Select(Signature));
    }

    [Fact]
    public async Task A_source_outside_the_caller_s_list_is_refused_like_an_unknown_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await SignedInAsync("ss");

        using var outside = await client.GetAsync("/api/findings?source_id=lab-d", cancellationToken);
        using var unknown = await client.GetAsync("/api/findings?source_id=nope", cancellationToken);
        using var environment = await client.GetAsync("/api/findings?environment=lab", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsByteArrayAsync(cancellationToken),
            await outside.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(HttpStatusCode.BadRequest, environment.StatusCode);
    }

    [Fact]
    public async Task An_import_that_renames_a_service_does_not_move_another_source_s_copy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var anonymous = FakeProvider.Client(Factory);
        using var import = Request("POST", "/api/import/findings?source_id=lab-d", ImportBody(F2, "cart", "[]"));
        import.Headers.Add("X-API-Key", ImportKey);
        using var imported = await anonymous.SendAsync(import, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        using var client = await SignedInAsync("svc");

        // findings.service now reads cart, but shop-d's own copy is still invoice.
        var finding = Assert.Single(await FindingsAsync(client, "/api/findings"), item => Signature(item) == F2);
        Assert.Equal("lab-d",
            Assert.Single(finding.GetProperty("sources").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task The_recap_reads_only_the_copies_in_scope()
    {
        // F1 is acked at shop-d, the only copy attr reads: counted, not ranked. F2 is outside its scope.
        using var attr = await SignedInAsync("attr");

        using var response = await attr.GetAsync("/api/recap", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        var ranked = root.GetProperty("priorities").EnumerateArray()
            .Select(priority => priority.GetProperty("signature").GetString()).ToList();
        // Both warnings with no volume, so the signature breaks the tie.
        Assert.Equal([F4, F5], ranked);
        Assert.DoesNotContain(F2, ranked);
        Assert.DoesNotContain(F1, ranked);
        Assert.Equal(1, root.GetProperty("tiles").GetProperty("acked_findings").GetInt32());
    }

    [Fact]
    public async Task A_caller_without_findings_read_gets_403_on_the_recap()
    {
        using var client = await SignedInAsync("tracer");

        using var response = await client.GetAsync("/api/recap", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("No grant gives you findings.read on this Hub.", await DetailAsync(response));
    }

    // The findings are seeded at the clock's now and the Hub shares this clock, so a week on, the seeded
    // week is the last closed one.
    [Fact]
    public async Task An_on_demand_report_keeps_the_callers_scope()
    {
        _clock.Advance(TimeSpan.FromDays(7));
        using var attr = await SignedInAsync("attr");

        using var created = await attr.SendAsync(Post("/api/recap/reports", """{"period":"week"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = await ReportIdAsync(created);
        using var detail = await attr.GetAsync($"/api/recap/reports/{id}", TestContext.Current.CancellationToken);
        using var document =
            JsonDocument.Parse(await detail.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var ranked = document.RootElement.GetProperty("snapshot").GetProperty("priorities").EnumerateArray()
            .Select(priority => priority.GetProperty("signature").GetString()).Order().ToList();
        Assert.Equal([F4, F5], ranked);
        Assert.DoesNotContain(F2, ranked);
    }

    [Fact]
    public async Task The_creator_lists_and_reads_its_own_report()
    {
        _clock.Advance(TimeSpan.FromDays(7));
        using var attr = await SignedInAsync("attr");
        var own = await ReportIdAsync(await attr.SendAsync(Post("/api/recap/reports", """{"period":"week"}"""),
            TestContext.Current.CancellationToken));
        var subscription = await StoreSubscriptionReportAsync();

        using var list = await attr.GetAsync("/api/recap/reports", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal([own], await ListedIdsAsync(list));
        foreach (var path in new[] { $"/api/recap/reports/{own}", $"/recaps/{own}/tech.html" })
        {
            using var read = await attr.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        foreach (var path in new[] { $"/api/recap/reports/{subscription}", $"/recaps/{subscription}/tech.html" })
        {
            using var hidden = await attr.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
    }

    // Ownership follows the immutable subject, never the display name two callers can share.
    [Fact]
    public async Task A_caller_sharing_the_creators_name_but_not_its_subject_reads_nothing()
    {
        _clock.Advance(TimeSpan.FromDays(7));
        using var attr = await SignedInAsync("attr");
        var own = await ReportIdAsync(await attr.SendAsync(Post("/api/recap/reports", """{"period":"week"}"""),
            TestContext.Current.CancellationToken));
        using var twin = await SignedInAsync("attrtwin");

        using var list = await twin.GetAsync("/api/recap/reports", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Empty(await ListedIdsAsync(list));
        foreach (var path in new[] { $"/api/recap/reports/{own}", $"/recaps/{own}/tech.html" })
        {
            using var hidden = await twin.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
    }

    [Fact]
    public async Task A_reader_without_recaps_read_gets_the_404_an_unknown_report_gets()
    {
        _clock.Advance(TimeSpan.FromDays(7));
        using var attr = await SignedInAsync("attr");
        var own = await ReportIdAsync(await attr.SendAsync(Post("/api/recap/reports", """{"period":"week"}"""),
            TestContext.Current.CancellationToken));
        var subscription = await StoreSubscriptionReportAsync();
        using var viewer = await SignedInAsync("viewer");

        foreach (var template in new[] { "/api/recap/reports/{0}", "/recaps/{0}/tech.html" })
        {
            using var unknown = await viewer.GetAsync(string.Format(template, "0123456789abcdef0123456789abcdef"),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            var expected = await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            foreach (var id in new[] { own, subscription })
            {
                using var hidden = await viewer.GetAsync(string.Format(template, id),
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
                Assert.Equal(expected, await hidden.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }
        }

        using var list = await viewer.GetAsync("/api/recap/reports", TestContext.Current.CancellationToken);
        Assert.Empty(await ListedIdsAsync(list));
    }

    [Fact]
    public async Task Admin_lists_every_report()
    {
        _clock.Advance(TimeSpan.FromDays(7));
        using var attr = await SignedInAsync("attr");
        var own = await ReportIdAsync(await attr.SendAsync(Post("/api/recap/reports", """{"period":"week"}"""),
            TestContext.Current.CancellationToken));
        var subscription = await StoreSubscriptionReportAsync();
        using var admin = await SignedInAsync("admin");

        using var list = await admin.GetAsync("/api/recap/reports", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(new[] { own, subscription }.Order(), (await ListedIdsAsync(list)).Order());
        foreach (var id in new[] { own, subscription })
        {
            using var read = await admin.GetAsync($"/recaps/{id}/tech.html", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }
    }

    [Fact]
    public async Task A_caller_without_findings_read_cannot_generate()
    {
        using var tracer = await SignedInAsync("tracer");

        using var response = await tracer.SendAsync(Post("/api/recap/reports", """{"period":"week"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("No grant gives you findings.read on this Hub.", await DetailAsync(response));
    }

    [Fact]
    public async Task A_caller_with_neither_permission_cannot_list()
    {
        using var tracer = await SignedInAsync("tracer");

        using var response = await tracer.GetAsync("/api/recap/reports", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You may not read recap reports.", await DetailAsync(response));
    }

    [Fact]
    public async Task A_caller_without_findings_read_anywhere_gets_403_on_both_findings_routes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await SignedInAsync("tracer");

        using var list = await client.GetAsync("/api/findings", cancellationToken);
        using var trace = await client.GetAsync("/api/findings/trace-x", cancellationToken);
        using var incidents = await client.GetAsync("/api/incidents", cancellationToken);
        using var correlations = await client.GetAsync("/api/correlations", cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, trace.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, incidents.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, correlations.StatusCode);
    }

    // ------------------------------------------------------------ incidents

    [Fact]
    public async Task An_incident_serves_the_richest_copy_in_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var attr = await SignedInAsync("attr");
        using var admin = await SignedInAsync("admin");

        Assert.Equal("shop-d", await IncidentSourceAsync(attr, Incident));
        Assert.Equal("lab-d", await IncidentSourceAsync(admin, Incident));
        var listed = await FindingsAsync(attr, "/api/incidents");
        Assert.Equal($"{Incident} shop-d", string.Join(',', listed.Select(item =>
            item.GetProperty("id").GetString() + " " + item.GetProperty("source_id").GetString())));
        // Only one of its two frozen findings carries team=shop.
        using var mixed = await attr.GetAsync($"/api/incidents/{MixedIncident}", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, mixed.StatusCode);
    }

    [Fact]
    public async Task An_episode_is_read_only_where_both_its_services_are()
    {
        // svc reads cart anywhere, so not e2 whose target is invoice. ss reads cart at shop-d,
        // lab reads all of lab-d, and attr reads only a team attribute an episode never carries.
        Assert.Equal("e1 shop-d,e3 lab-d", await EpisodesAsync("svc"));
        Assert.Equal("e1 shop-d", await EpisodesAsync("ss"));
        Assert.Equal("e3 lab-d", await EpisodesAsync("lab"));
        Assert.Equal("", await EpisodesAsync("attr"));
        Assert.Equal("e1 shop-d,e2 shop-d,e3 lab-d", await EpisodesAsync("admin"));
    }

    [Fact]
    public async Task A_refresh_reads_only_the_daemons_in_scope()
    {
        using var client = await SignedInAsync("ss");
        // Past the 10 s debounce the seeded incident reads started.
        _clock.Advance(TimeSpan.FromSeconds(11));

        using var response =
            await client.SendAsync(Post("/api/incidents/refresh"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_shopCalls, call => call.Target.Contains("incidents", StringComparison.Ordinal));
        Assert.DoesNotContain(_labCalls, call => call.Target.Contains("incidents", StringComparison.Ordinal));
        using var listed =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal($"{Incident} shop-d,{MixedIncident} shop-d", string.Join(',', listed.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("id").GetString() + " " + item.GetProperty("source_id").GetString())));
    }

    [Fact]
    public async Task A_refresh_by_a_caller_without_findings_read_is_refused_before_the_gate()
    {
        var gate = Factory.Services.GetRequiredService<IncidentRefreshGate>();
        using var client = await SignedInAsync("tracer");
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        try
        {
            using var response =
                await client.SendAsync(Post("/api/incidents/refresh"), TestContext.Current.CancellationToken);

            // A 503 would mean the request queued for a slot before anything judged it.
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("No grant gives you findings.read on this Hub.", await DetailAsync(response));
        }
        finally
        {
            gate.Exit();
            gate.Exit();
        }
    }

    // ------------------------------------------------------------ acks

    [Fact]
    public async Task A_held_but_unreadable_finding_answers_the_404_an_unheld_one_gets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await SignedInAsync("attr");

        using var hidden = await client.SendAsync(Post("/api/sources/shop-d/acks", AckBody(F2)), cancellationToken);
        using var unheld = await client.SendAsync(Post("/api/sources/shop-d/acks", AckBody(Orphan)), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(await unheld.Content.ReadAsByteArrayAsync(cancellationToken),
            await hidden.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.DoesNotContain(_shopCalls, call => call.Method != "GET");
    }

    [Fact]
    public async Task A_readable_finding_seen_in_a_grouping_without_the_attribute_cannot_be_acked()
    {
        using var client = await SignedInAsync("attr");

        using var response = await client.SendAsync(Post("/api/sources/shop-d/acks", AckBody(F4)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You can read this finding at this source but not acknowledge it there.",
            await DetailAsync(response));
        Assert.DoesNotContain(_shopCalls, call => call.Method != "GET");
    }

    [Fact]
    public async Task A_caller_without_acks_write_at_the_source_is_refused_before_the_gate()
    {
        var gate = Factory.Services.GetRequiredService<AckRelayGate>();
        using var client = await SignedInAsync("svc");
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        try
        {
            using var response = await client.SendAsync(Post("/api/sources/shop-d/acks", AckBody(F1)),
                TestContext.Current.CancellationToken);

            // A 503 would mean the request queued for a slot before anything judged it.
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("You may not acknowledge findings at this source.", await DetailAsync(response));
        }
        finally
        {
            gate.Exit();
            gate.Exit();
        }
    }

    [Fact]
    public async Task An_ack_through_an_attribute_grant_is_relayed_with_no_expiry_in_the_caller_s_name()
    {
        using var client = await SignedInAsync("attr");

        using var response = await client.SendAsync(Post("/api/sources/shop-d/acks", AckBody(F5)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var sent = JsonDocument.Parse(Assert.Single(_shopCalls, call => call.Method == "POST").Body);
        Assert.False(sent.RootElement.TryGetProperty("expires_at", out _));
        Assert.Equal("attr@example.internal", sent.RootElement.GetProperty("by").GetString());
    }

    [Theory]
    [InlineData("ss", "shop-d", F1)]
    [InlineData("attr", "shop-d", F1)]
    [InlineData("lab", "lab-d", Orphan)]
    // An ack whose finding the Hub no longer holds names no service: the source as a whole.
    [InlineData("attr", "lab-d", "")]
    public async Task The_ack_listing_holds_what_the_caller_reads_at_the_source(
        string user,
        string sourceId,
        string expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await SignedInAsync(user);

        using var response = await client.GetAsync($"/api/sources/{sourceId}/acks", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var listing = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(expected, string.Join(',', listing.RootElement.EnumerateArray()
            .Select(ack => ack.GetProperty("signature").GetString())));
    }

    [Fact]
    public async Task A_caller_who_reads_no_finding_at_the_source_gets_no_ack_listing()
    {
        using var client = await SignedInAsync("tempo");

        using var response = await client.GetAsync("/api/sources/shop-d/acks", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------ actions

    [Fact]
    public async Task A_member_acts_on_a_finding_its_attribute_grant_writes()
    {
        using var client = await SignedInAsync("attr");

        using var response = await client.SendAsync(Post("/api/actions", ActionBody(F1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var action = Assert.Single(await Database.ReadActionsAsync([F1], TestContext.Current.CancellationToken));
        Assert.Equal("attr@example.internal", action.Owner);
        Assert.Equal("attr@example.internal", action.CreatedBy);
    }

    [Fact]
    public async Task A_readable_finding_its_grant_cannot_write_is_a_403()
    {
        using var client = await SignedInAsync("attr");

        using var response = await client.SendAsync(Post("/api/actions", ActionBody(F4)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You can read this finding but not act on it.", await DetailAsync(response));
        Assert.Empty(await Database.ReadActionsAsync([F4], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_hidden_finding_answers_the_404_an_unknown_one_gets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await SignedInAsync("attr");

        using var hidden = await client.SendAsync(Post("/api/actions", ActionBody(F2)), cancellationToken);
        using var unknown = await client.SendAsync(Post("/api/actions", ActionBody(Orphan)), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsByteArrayAsync(cancellationToken),
            await hidden.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Empty(await Database.ReadActionsAsync([F2, Orphan], cancellationToken));
    }

    [Fact]
    public async Task A_caller_without_actions_write_is_refused_before_the_body()
    {
        using var client = await SignedInAsync("viewer");

        // No body worth reading: the refusal comes before it is parsed.
        using var response = await client.SendAsync(Post("/api/actions", "not json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You may not act on findings.", await DetailAsync(response));
    }

    // ------------------------------------------------------------ disclosures

    [Theory]
    [InlineData("/share/pshare_unknown0123456789abcdef0123456789abcdef/report.json")]
    [InlineData("/share/anything/")]
    public async Task The_share_route_answers_strangers_without_a_sign_in(string path)
    {
        using var client = FakeProvider.Client(Factory);

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // A 404, never the redirect to sign-in every other route gives a stranger.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("svc", HttpStatusCode.Forbidden)]
    [InlineData("viewer", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    public async Task Disclosures_are_read_with_disclosures_read_only(string user, HttpStatusCode expected)
    {
        using var client = await SignedInAsync(user);

        using var response = await client.GetAsync("/api/disclosures", TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task A_disclosures_reader_cannot_manage_and_an_admin_can()
    {
        await DisclosuresApiTests.SeedAsync(Factory, "d1", "u1", "2026-09-30");
        using var viewer = await SignedInAsync("viewer");
        using var admin = await SignedInAsync("admin");

        using var refused = await viewer.SendAsync(
            Post("/api/disclosures/d1/withdraw", """{"reason":"wrong"}"""), TestContext.Current.CancellationToken);
        using var withdrawn = await admin.SendAsync(
            Post("/api/disclosures/d1/withdraw", """{"reason":"wrong"}"""), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("You may not manage disclosures.", await DetailAsync(refused));
        Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        Assert.Equal("admin@example.internal",
            (await Database.GetDisclosureAsync("d1", TestContext.Current.CancellationToken))!.WithdrawnBy);
    }

    [Fact]
    public async Task A_machine_token_never_reads_disclosures_even_through_an_admin_group()
    {
        using var client = Bearer(CiAdminToken);

        using var response = await client.GetAsync("/api/disclosures", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("viewer", true)]
    [InlineData("svc", false)]
    public async Task Status_offers_the_disclosures_tab_to_a_reader_of_them_only(string user, bool expected)
    {
        using var client = await SignedInAsync(user);

        using var status =
            JsonDocument.Parse(await client.GetStringAsync("/api/status", TestContext.Current.CancellationToken));

        Assert.Equal(expected, status.RootElement.TryGetProperty("disclosures", out _));
    }

    [Theory]
    [InlineData("lab", HttpStatusCode.NoContent)]
    [InlineData("attr", HttpStatusCode.NotFound)]
    public async Task An_orphan_revoke_needs_the_source_as_a_whole(string user, HttpStatusCode expected)
    {
        using var client = await SignedInAsync(user);

        using var response = await client.SendAsync(
            Post("/api/sources/lab-d/acks/revoke", $$"""{"signature":"{{Orphan}}"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("same-site")]
    [InlineData(null)]
    public async Task A_cookie_write_without_same_origin_is_refused(string? site)
    {
        using var client = await SignedInAsync("attr");
        using var request = Request("POST", "/api/analyses", """{"source_id":"tempo","request":{"trace_id":"abc"}}""");
        if (site is not null) request.Headers.Add("Sec-Fetch-Site", site);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("A request from another site is refused.", await DetailAsync(response));
    }

    [Fact]
    public async Task A_bearer_write_is_judged_as_a_machine_and_not_as_a_cross_site_request()
    {
        using var client = FakeProvider.Client(Factory);
        using var request = Request("POST", "/api/incidents/refresh");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CiToken);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("A machine token only reads.", await DetailAsync(response));
    }

    // ------------------------------------------------------------ analyses

    [Theory]
    [InlineData("attr", """{"source_id":"shop-d"}""", HttpStatusCode.Forbidden)]
    [InlineData("lab", """{"source_id":"lab-d"}""", HttpStatusCode.Accepted)]
    [InlineData("attr", """{"source_id":"tempo","request":{"service":"cart","lookback":"1h"}}""",
        HttpStatusCode.Forbidden)]
    [InlineData("attr", """{"source_id":"tempo","request":{"service":"checkout","lookback":"1h"}}""",
        HttpStatusCode.Accepted)]
    [InlineData("attr", """{"source_id":"tempo","request":{"service":"checkout","lookback":"1h","trace_id":null}}""",
        HttpStatusCode.Accepted)]
    [InlineData("attr", """{"source_id":"tempo","request":{"trace_id":"abc"}}""", HttpStatusCode.Forbidden)]
    [InlineData("tempo", """{"source_id":"tempo","request":{"trace_id":"abc"}}""", HttpStatusCode.Accepted)]
    [InlineData("viewer", """{"source_id":"tempo","request":{"trace_id":"abc"}}""", HttpStatusCode.BadRequest)]
    public async Task Starting_a_run_needs_its_permissions_on_its_subject(string user, string body,
        HttpStatusCode expected)
    {
        using var client = await SignedInAsync(user);

        using var response = await client.SendAsync(Post("/api/analyses", body), TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        // Two checks of the expected status: a switch would ask for every other HttpStatusCode.
        // ReSharper disable once ConvertIfStatementToSwitchStatement
        if (expected == HttpStatusCode.Forbidden)
            Assert.Equal("You may not start this analysis.", await DetailAsync(response));
        if (expected == HttpStatusCode.BadRequest)
            Assert.Equal("Unknown source_id.", await DetailAsync(response));
    }

    [Fact]
    public async Task A_service_report_is_read_on_the_requested_service_by_whoever_holds_traces_read_there()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = await SeedRunAsync("tempo", "tempo", """{"service":"checkout","lookback":"1h"}""",
            "tempo@example.internal");
        using var attr = await SignedInAsync("attr");
        using var viewer = await SignedInAsync("viewer");

        using var row = await attr.GetAsync($"/api/analyses/{id}", cancellationToken);
        using var report = await attr.GetAsync($"/reports/{id}.html", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, row.StatusCode);
        using var payload = JsonDocument.Parse(await row.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(4, payload.RootElement.GetProperty("result").GetProperty("findings").GetInt32());
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Equal("frame-ancestors 'self'", report.Headers.GetValues("Content-Security-Policy").Single());

        using var hiddenRow = await viewer.GetAsync($"/api/analyses/{id}", cancellationToken);
        using var hiddenReport = await viewer.GetAsync($"/reports/{id}.html", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, hiddenRow.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenReport.StatusCode);
    }

    [Fact]
    public async Task Run_rows_follow_the_one_rule_for_every_kind()
    {
        var trace = await SeedRunAsync("tempo", "tempo", """{"trace_id":"abc"}""", "tempo@example.internal");
        // Requested by attr, on a service whose copies attr cannot write: no requester clause.
        var cart = await SeedRunAsync("tempo", "tempo", """{"service":"cart","lookback":"1h"}""",
            "attr@example.internal");
        var daemon = await SeedRunAsync("lab-d", "daemon", "{}", "lab@example.internal");
        var checkout = await SeedRunAsync("tempo", "tempo", """{"service":"checkout","lookback":"1h"}""", "x");
        // The newest run is one attr cannot see, so a LIMIT taken before the rule would hide checkout.
        var newest = await SeedRunAsync("tempo", "tempo", """{"trace_id":"def"}""", "tempo@example.internal");

        Assert.Equal(checkout, await RunIdsAsync("attr"));
        Assert.Equal($"{newest},{checkout},{daemon},{cart},{trace}", await RunIdsAsync("admin"));
        Assert.Equal($"{newest},{checkout},{cart},{trace}", await RunIdsAsync("tempo"));
        Assert.Equal(daemon, await RunIdsAsync("lab"));
        Assert.Equal(checkout, await RunIdsAsync("attr", "?limit=1"));
        using var attr = await SignedInAsync("attr");
        using var hidden = await attr.GetAsync($"/reports/{trace}.html", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    // ------------------------------------------------------------ daemon view

    [Theory]
    [InlineData("attr", "shop-d", HttpStatusCode.NotFound)]
    [InlineData("lab", "lab-d", HttpStatusCode.OK)]
    [InlineData("lab", "shop-d", HttpStatusCode.NotFound)]
    [InlineData("tempo", "tempo", HttpStatusCode.NotFound)]
    [InlineData("admin", "tempo", HttpStatusCode.BadRequest)]
    public async Task The_daemon_view_needs_sources_read_at_the_source_as_a_whole(
        string user, string sourceId, HttpStatusCode expected)
    {
        using var client = await SignedInAsync(user);

        using var response = await client.GetAsync($"/api/sources/{sourceId}/daemon?refresh=status",
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    // ------------------------------------------------------------ framing and bearer

    [Fact]
    public async Task The_launcher_can_be_framed_by_the_hub_alone()
    {
        using var client = await SignedInAsync("attr");

        using var page = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("frame-ancestors 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task A_valid_cookie_with_a_foreign_bearer_is_judged_on_the_bearer()
    {
        using var client = await SignedInAsync("admin");
        using var request = Request("GET", "/api/findings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "a-proxy-token");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer error=\"invalid_token\"", response.Headers.WwwAuthenticate.Single().ToString());
    }

    // ------------------------------------------------------------ sessions

    [Fact]
    public async Task With_access_on_the_session_ends_eight_hours_after_sign_in()
    {
        using var signIn = FakeProvider.Client(Factory);
        using var callback = await FakeProvider.SignInAsync(signIn, "attr");
        var session = FakeProvider.SessionCookie(callback);
        Assert.False(CookieOptions(Factory).SlidingExpiration);

        _clock.Advance(TimeSpan.FromHours(5));
        using var atFive = await WithCookieAsync(Factory, session, "/api/status");
        Assert.Equal(HttpStatusCode.OK, atFive.StatusCode);
        Assert.False(atFive.Headers.Contains("Set-Cookie"));

        _clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
        using var afterEight = await WithCookieAsync(Factory, session, "/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, afterEight.StatusCode);
    }

    [Fact]
    public async Task A_session_opened_before_access_was_turned_on_signs_in_again()
    {
        await using var before = _hub.WithWebHostBuilder(builder =>
        {
            _provider.Configure(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });
        using var signIn = FakeProvider.Client(before);
        using var callback = await FakeProvider.SignInAsync(signIn, "admin");

        using var response = await WithCookieAsync(Factory, FakeProvider.SessionCookie(callback), "/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_sign_in_records_the_subject_the_filtered_groups_and_the_caller_kind()
    {
        using var client = FakeProvider.Client(Factory);
        using var callback = await FakeProvider.SignInAsync(client, "attr");

        var claims = Claims(Factory, callback);

        Assert.Equal("u-attr", Assert.Single(claims, claim => claim.Type == "hub:subject").Value);
        // Only groups a grant or AdminGroups names, never one spelled internal:, never a number.
        Assert.Equal("/shop", Assert.Single(claims, claim => claim.Type == "hub:group").Value);
        Assert.Equal("1", Assert.Single(claims, claim => claim.Type == "hub:access").Value);
        Assert.Equal("session", Assert.Single(claims, claim => claim.Type == "hub:caller").Value);
        Assert.Equal("attr@example.internal", Assert.Single(claims, claim => claim.Type == ClaimTypes.Name).Value);
    }

    [Theory]
    [InlineData("ss", "/ss")]
    [InlineData("nogrant", "")]
    [InlineData("admin", "")]
    public async Task The_groups_field_reads_a_list_a_lone_string_or_nothing(string user, string groups)
    {
        using var client = FakeProvider.Client(Factory);
        using var callback = await FakeProvider.SignInAsync(client, user);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(groups, string.Join(',', Claims(Factory, callback)
            .Where(claim => claim.Type == "hub:group").Select(claim => claim.Value)));
    }

    [Theory]
    [InlineData("nosub", "lacks a string Hub:Auth:SubjectClaim field")]
    [InlineData("numsub", "lacks a string Hub:Auth:SubjectClaim field")]
    [InlineData("blanksub", "lacks a string Hub:Auth:SubjectClaim field")]
    [InlineData("localsub", "reserved prefix")]
    [InlineData("machineid", "reserved prefix")]
    public async Task A_sign_in_without_a_usable_subject_or_with_a_reserved_prefix_is_refused(string user,
        string reason)
    {
        var logger = new ListLogger<Program>();
        await using var logged = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(new Capture(logger))));
        using var client = FakeProvider.Client(logged);

        using var callback = await FakeProvider.SignInAsync(client, user);

        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.Contains(reason, Assert.Single(logger.Messages, message =>
            message.StartsWith("Sign-in refused", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_required_groups_a_non_member_is_refused_before_access_and_a_member_keeps_both_claims()
    {
        await using var gated = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Hub:Auth:RequiredGroups:0", "/shop");
            builder.UseSetting("Hub:Auth:RequiredGroups:1", "/ss");
        });
        using var client = FakeProvider.Client(gated);

        using var refused = await FakeProvider.SignInAsync(client, "svc");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var callback = await FakeProvider.SignInAsync(client, "attr");
        var claims = Claims(gated, callback);
        Assert.Equal("/shop", Assert.Single(claims, claim => claim.Type == "hub_required_group").Value);
        Assert.Contains(claims, claim => claim.Type == "hub:subject");
        Assert.Contains(claims, claim => claim.Type == "hub:group");

        await using var dropped =
            Factory.WithWebHostBuilder(builder => builder.UseSetting("Hub:Auth:RequiredGroups:0", "/ss"));
        using var after = await WithCookieAsync(dropped, FakeProvider.SessionCookie(callback), "/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    // ------------------------------------------------------------ machine tokens

    [Fact]
    public async Task A_machine_token_reads_findings_in_its_group_s_scope()
    {
        using var client = Bearer(CiToken);

        Assert.Equal(F5 + "," + F4 + "," + F1,
            string.Join(',', (await FindingsAsync(client, "/api/findings")).Select(Signature)));
        Assert.Equal("lab-d:findings.read;shop-d:findings.read;test:findings.read",
            await SourcePermissionsAsync(client));
        using var status = await client.GetAsync("/api/status", TestContext.Current.CancellationToken);
        using var payload =
            JsonDocument.Parse(await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("machine:ci", payload.RootElement.GetProperty("identity").GetString());
    }

    [Theory]
    [InlineData(CiOldToken)]
    [InlineData("ci-unknown-0123456789abcdef0123456789abcdef")]
    [InlineData("pshub_0123456789abcdef0123456789abcdef")]
    public async Task An_unknown_or_expired_machine_token_gets_401(string token)
    {
        using var client = Bearer(token);

        using var response = await client.GetAsync("/api/findings", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer error=\"invalid_token\"", response.Headers.WwwAuthenticate.Single().ToString());
    }

    [Fact]
    public async Task A_machine_token_in_an_admin_group_still_only_reads_findings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRunAsync("tempo", "tempo", """{"trace_id":"abc"}""", "tempo@example.internal");
        using var client = Bearer(CiAdminToken);

        Assert.Equal(5, (await FindingsAsync(client, "/api/findings")).Count);
        Assert.Empty(await FindingsAsync(client, "/api/analyses"));
        using var view = await client.GetAsync("/api/sources/lab-d/daemon", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, view.StatusCode);
        foreach (var path in new[] { "/api/incidents/refresh", "/api/sources/shop-d/acks", "/api/analyses" })
        {
            using var write = await client.SendAsync(Request("POST", path, "{}"), cancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        }
    }

    [Fact]
    public async Task An_expired_machine_token_is_logged_at_startup_by_name_alone()
    {
        var logger = new ListLogger<Program>();
        await using var logged = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(new Capture(logger))));

        using var client = logged.CreateClient();

        var expired = Assert.Single(logger.Messages, message => message.Contains("ci-old", StringComparison.Ordinal));
        Assert.DoesNotContain(CiOldToken, expired, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("'ci'", StringComparison.Ordinal));
    }

    private HttpClient Bearer(string token)
    {
        var client = FakeProvider.Client(Factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ------------------------------------------------------------ helpers

    private WebApplicationFactory<Program> Hub()
    {
        return _hub.WithWebHostBuilder(builder =>
        {
            _provider.Configure(builder);
            builder.UseSetting("Hub:Access:Enabled", "true");
            builder.UseSetting("Hub:MetricsPort", "9464");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
                foreach (var worker in services.Where(descriptor =>
                             descriptor.ServiceType == typeof(IHostedService) &&
                             descriptor.ImplementationType == typeof(AnalysisWorker)).ToArray())
                    services.Remove(worker);
                services.PostConfigure<HubOptions>(options =>
                {
                    options.Analysis = new AnalysisOptions
                    { EngineBinaryPath = "/usr/bin/false", ReportDirectory = _reports };
                    options.Sources =
                    [
                        .. options.Sources,
                        Daemon("shop-d", "prod", _shopDaemon),
                        Daemon("lab-d", "lab", _labDaemon) with { ImportApiKey = ImportKey },
                        new SourceOptions
                        {
                            Id = "tempo", Name = "Tempo", Environment = "prod", Kind = SourceKinds.Tempo,
                            BaseUrl = new Uri("http://127.0.0.1:2")
                        }
                    ];
                    options.Access = Access(_clock.GetUtcNow());
                    options.Disclosures = new DisclosuresOptions { ImportKey = DisclosureKey };
                });
            });
        });
    }

    private static AccessOptions Access(DateTimeOffset now)
    {
        return new AccessOptions
        {
            Enabled = true,
            Admins = ["u-admin"],
            AdminGroups = ["internal:ci-admin"],
            Groups = [new InternalGroupOptions { Name = "ci" }, new InternalGroupOptions { Name = "ci-admin" }],
            Projects =
            [
                new ProjectOptions { Name = "cart-svc", Patterns = [new PatternOptions { Service = "cart" }] },
                new ProjectOptions { Name = "shop", Patterns = [new PatternOptions { Attribute = "team=shop" }] },
                new ProjectOptions
                    { Name = "shop-cart", Patterns = [new PatternOptions { Source = "shop-d", Service = "cart" }] },
                new ProjectOptions { Name = "lab", Patterns = [new PatternOptions { Source = "lab-d" }] },
                new ProjectOptions { Name = "tempo-all", Patterns = [new PatternOptions { Source = "tempo" }] }
            ],
            Roles =
            [
                new RoleOptions { Name = "viewer", Permissions = ["findings.read"] },
                new RoleOptions { Name = "operator", Permissions = ["findings.read", "sources.read"] },
                new RoleOptions { Name = "tracer", Permissions = ["traces.read"] },
                new RoleOptions { Name = "disclosure-reader", Permissions = ["disclosures.read"] }
            ],
            Grants =
            [
                new GrantOptions { Group = "/svc", Project = "cart-svc", Role = "reader" },
                new GrantOptions { Group = "/shop", Project = "shop", Role = "member" },
                new GrantOptions { Group = "/ss", Project = "shop-cart", Role = "member" },
                new GrantOptions { Group = "/lab", Project = "lab", Role = "member" },
                new GrantOptions { Group = "/lab", Project = "lab", Role = "operator" },
                new GrantOptions { Group = "/tempo", Project = "tempo-all", Role = "reader" },
                new GrantOptions { Group = "/viewers", Project = "shop", Role = "viewer" },
                new GrantOptions { Group = "/viewers", Project = "*", Role = "disclosure-reader" },
                new GrantOptions { Group = "/tracers", Project = "tempo-all", Role = "tracer" },
                new GrantOptions { Group = "internal:ci", Project = "shop", Role = "viewer" }
            ],
            MachineTokens =
            [
                new MachineTokenOptions
                    { Name = "ci", Group = "internal:ci", Token = CiToken, ExpiresAt = now.AddDays(30) },
                new MachineTokenOptions
                {
                    Name = "ci-admin", Group = "internal:ci-admin", Token = CiAdminToken, ExpiresAt = now.AddDays(30)
                },
                new MachineTokenOptions
                    { Name = "ci-old", Group = "internal:ci", Token = CiOldToken, ExpiresAt = now.AddDays(-1) }
            ]
        };
    }

    private static SourceOptions Daemon(string id, string environment, FakeDaemon daemon)
    {
        return AckRelayApiTests.Relayed(id, daemon) with { Name = id, Environment = environment };
    }

    private async Task SeedAsync()
    {
        await SeedFindingAsync("shop-d", "prod", F1, "cart", """[{"key":"team","value":"shop"}]""");
        await SeedFindingAsync("lab-d", "lab", F1, "cart", """[{"key":"team","value":"billing"}]""");
        await SeedFindingAsync("shop-d", "prod", F2, "invoice", """[{"key":"team","value":"billing"}]""");
        await SeedFindingAsync("shop-d", "prod", F3, "cart", "[]");
        await SeedFindingAsync("shop-d", "prod", F4, "cart", """[{"key":"team","value":"shop"}]""");
        await SeedFindingAsync("shop-d", "prod", F4, "cart", "[]");
        await SeedFindingAsync("shop-d", "prod", F5, "checkout", """[{"key":"team","value":"shop"}]""");
        await SeedFindingAsync("lab-d", "lab", F5, "checkout", """[{"key":"team","value":"billing"}]""");
        await Database.ReplaceSourceAcksAsync(
            "shop-d",
            [new ParsedAck(F1, "daemon", "robin", "known", "2026-09-20T10:00:00Z", null, null)],
            AckReadStates.Ok, NowMs + 1, TestContext.Current.CancellationToken);
        await Database.ReplaceSourceAcksAsync(
            "lab-d",
            [new ParsedAck(Orphan, "daemon", "robin", "known", "2026-09-20T10:00:00Z", null, null)],
            AckReadStates.Ok, NowMs + 1, TestContext.Current.CancellationToken);
        await SeedIncidentAsync("shop-d", Incident, ["shop", "shop", "shop"]);
        await SeedIncidentAsync("lab-d", Incident, ["billing", "billing", "billing", "billing", "billing"]);
        await SeedIncidentAsync("shop-d", MixedIncident, ["shop", null]);
        await SeedEpisodesAsync();
    }

    // Each finding is seen a millisecond after the previous one, so the order is fixed.
    private async Task SeedFindingAsync(string source, string environment, string signature, string service,
        string grouping)
    {
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await Database.UpsertBatchAsync(
            new SourceSnapshot(source, source, environment, "0.26.2"),
            FindingParser.Parse(Encoding.UTF8.GetBytes($"[{Envelope(signature, service, grouping)}]")),
            NowMs,
            TestContext.Current.CancellationToken);
    }

    private static string Envelope(string signature, string service, string grouping)
    {
        return $$"""
                 {"finding":{"type":"slow_sql","severity":"warning","trace_id":"trace-x","service":"{{service}}",
                  "grouping":{{grouping}},"source_endpoint":"GET /orders","pattern":{"template":"SELECT 1"},
                  "confidence":"daemon_production","signature":"{{signature}}"},"first_seen_ms":1}
                 """;
    }

    // One pushed page of a ledger: today, open, one signature's count.
    private Task RecordDetectionsAsync(string source, string signature, long detections)
    {
        var day = (int)(NowMs / 86_400_000);
        var page = new DetectionLedgerPage(
            "00000000-0000-4000-8000-000000000001", 0, day, day, null,
            [new DetectionCountRow(day, signature, detections, 10)],
            [new DetectionDayRow(day, false, false, 0, null, 0, 0, 1.0)],
            null);
        return Database.MergeDetectionPageAsync(source, page, true, NowMs, TestContext.Current.CancellationToken);
    }

    private static string ImportBody(string signature, string service, string grouping)
    {
        return $$"""{"producer_version":"0.26.2","findings":[{{Envelope(signature, service, grouping)}}]}""";
    }

    // e1 is cart then cart at shop-d, e2 cart then invoice at shop-d, e3 cart then cart at lab-d.
    private async Task SeedEpisodesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await Database.TryUpsertEpisodesAsync("shop-d", EpisodeJson.Parsed(
                EpisodeJson.Episode(EpisodeJson.Id(0xe1), 1_800_000_300_000, 1_800_000_360_000, null,
                    EpisodeJson.Id(0xa1), "cart", "cart"),
                EpisodeJson.Episode(EpisodeJson.Id(0xe2), 1_800_000_200_000, 1_800_000_260_000, null,
                    EpisodeJson.Id(0xa2), "cart", "invoice")),
            NowMs, cancellationToken);
        await Database.TryUpsertEpisodesAsync("lab-d", EpisodeJson.Parsed(
                EpisodeJson.Episode(EpisodeJson.Id(0xe3), 1_800_000_100_000, 1_800_000_160_000, null,
                    EpisodeJson.Id(0xa1), "cart", "cart")),
            NowMs, cancellationToken);
    }

    private async Task<string> EpisodesAsync(string user)
    {
        using var client = await SignedInAsync(user);
        return string.Join(',', (await FindingsAsync(client, "/api/correlations")).Select(episode =>
            episode.GetProperty("id").GetString()![^2..] + " " + episode.GetProperty("source_id").GetString()));
    }

    private async Task SeedIncidentAsync(string source, string id, string?[] teams)
    {
        var findings = string.Join(',', teams.Select(team => Envelope(F1, "cart",
            team is null ? "[]" : $$"""[{"key":"team","value":"{{team}}"}]""")));
        var incident =
            $$"""{"id":"{{id}}","service":"cart","kind":"oom_kill","at_ms":1000,"window_from_ms":900,"window_to_ms":1100}""";
        await Database.UpsertIncidentsAsync(
            source,
            [
                new ParsedIncident(id, "cart", "oom_kill", 1000, null, 900, 1100, null, teams.Length, incident,
                    $"[{findings}]")
            ],
            NowMs,
            TestContext.Current.CancellationToken);
    }

    private async Task<string> SeedRunAsync(string sourceId, string kind, string request, string requestedBy)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var id = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()[..8]);
        await Database.TryInsertRunAsync(new AnalysisRun(
            id, AnalysisStatuses.Pending, sourceId, sourceId, kind == "daemon" ? "lab" : "prod", kind, request,
            requestedBy, NowMs, null, null, null, null, null, null), cancellationToken);
        await Database.CompleteRunAsync(id, AnalysisStatuses.Succeeded, NowMs, NowMs + 3_600_000, "0.26.2", null,
            """{"findings":4}""", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_reports, $"{id}.html"), "<html>report</html>", cancellationToken);
        return id;
    }

    private async Task<string> RunIdsAsync(string user, string query = "")
    {
        using var client = await SignedInAsync(user);
        return string.Join(',', (await FindingsAsync(client, "/api/analyses" + query))
            .Select(run => run.GetProperty("id").GetString()));
    }

    private async Task<HttpClient> SignedInAsync(string user)
    {
        var client = FakeProvider.Client(Factory);
        using var callback = await FakeProvider.SignInAsync(client, user);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return client;
    }

    private static HttpRequestMessage Request(string method, string path, string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    // A browser's own write: the launcher's fetch carries same-origin.
    private static HttpRequestMessage Post(string path, string? body = null)
    {
        var request = Request("POST", path, body ?? "{}");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return request;
    }

    private static async Task<string> ReportIdAsync(HttpResponseMessage created)
    {
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var document =
            JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<List<string>> ListedIdsAsync(HttpResponseMessage list)
    {
        using var document =
            JsonDocument.Parse(await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return [.. document.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetString()!)];
    }

    // A scheduled report: nobody asked for it, so no caller created it.
    private async Task<string> StoreSubscriptionReportAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var snapshot = JsonSerializer.Serialize(RecapSnapshots.Distinctive(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        Assert.True(await Database.InsertRecapReportAsync(
            new RecapReportRecord(
                new RecapReportSummary(
                    id, "weekly-shop", "week", "2026-10-19", "2026-10-25",
                    [RecapViewNames.Pilotage, RecapViewNames.Tech], true, false, null, NowMs),
                snapshot),
            [], TestContext.Current.CancellationToken));
        return id;
    }

    private static string ActionBody(string signature)
    {
        return $$"""{"signature":"{{signature}}","take":true}""";
    }

    private static string AckBody(string signature)
    {
        return $$"""{"signature":"{{signature}}","reason":"known"}""";
    }

    private static async Task<List<JsonElement>> FindingsAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return [.. document.RootElement.EnumerateArray().Select(item => item.Clone())];
    }

    private static async Task<string> SourcePermissionsAsync(HttpClient client)
    {
        return string.Join(';', (await FindingsAsync(client, "/api/sources"))
            .OrderBy(source => source.GetProperty("id").GetString(), StringComparer.Ordinal)
            .Select(source => source.GetProperty("id").GetString() + ":" +
                              string.Join(',',
                                  source.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()))));
    }

    private static async Task<string?> IncidentSourceAsync(HttpClient client, string id)
    {
        using var response = await client.GetAsync($"/api/incidents/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("source_id").GetString();
    }

    private static string Signature(JsonElement finding)
    {
        return finding.GetProperty("finding").GetProperty("signature").GetString()!;
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response)
    {
        using var document =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.GetProperty("detail").GetString()!;
    }

    private static async Task<HttpResponseMessage> WithCookieAsync(WebApplicationFactory<Program> factory,
        string cookie, string path)
    {
        using var client = FakeProvider.Client(factory);
        using var request = Request("GET", path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static CookieAuthenticationOptions CookieOptions(WebApplicationFactory<Program> factory)
    {
        return factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    // What the session cookie a sign-in set carries, read back through the cookie handler's own format.
    internal static List<Claim> Claims(WebApplicationFactory<Program> factory, HttpResponseMessage callback)
    {
        var cookie = FakeProvider.SessionCookie(callback)["hub_session=".Length..];
        var ticket = CookieOptions(factory).TicketDataFormat.Unprotect(cookie);
        Assert.NotNull(ticket);
        return [.. ticket.Principal.Claims];
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
}
