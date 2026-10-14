using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

/// <summary>
///     GET /api/access: what each caller is told about themselves, and what an admin is
///     told about the whole configuration and the findings no project covers.
/// </summary>
public sealed class AccessApiTests : IAsyncLifetime
{
    private const string CiToken = "ci-page-0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential

    // F1 cart under team=shop; F2 invoice under team=billing; F3 cart with no grouping;
    // F4 cart under team=shop and under no grouping; F5 checkout under team=shop and under
    // team=billing, all at shop-d. F6 at lab-d, which a whole-source project names. F7 pay
    // under region=eu at test. F9 cart under team=billing at test. Stale is beyond retention.
    private const string F1 = "slow_sql:cart:0000000000000000000000000000000000000001";
    private const string F2 = "slow_sql:invoice:0000000000000000000000000000000000000002";
    private const string F3 = "slow_sql:cart:0000000000000000000000000000000000000003";
    private const string F4 = "slow_sql:cart:0000000000000000000000000000000000000004";
    private const string F5 = "slow_sql:checkout:0000000000000000000000000000000000000005";
    private const string F6 = "slow_sql:labsvc:0000000000000000000000000000000000000006";
    private const string F7 = "slow_sql:pay:0000000000000000000000000000000000000007";
    private const string Stale = "slow_sql:stale:0000000000000000000000000000000000000008";
    private const string F9 = "slow_sql:cart:0000000000000000000000000000000000000009";

    private static readonly Dictionary<string, string> Users = new(StringComparer.Ordinal)
    {
        ["member"] = """{"sub":"u-member","email":"member@example.internal","groups":["/teams/shop","/elsewhere"]}""",
        ["admin"] = """{"sub":"u-admin","email":"admin@example.internal","groups":["/hub-admins"]}""",
        ["nogrant"] = """{"sub":"u-none","email":"none@example.internal","groups":[]}""",
        ["root"] = """{"sub":"u-root","email":"root@example.internal","groups":[]}"""
    };

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly HubApplicationFactory _hub = new();
    private FakeProvider _provider = null!;

    private WebApplicationFactory<Program> Factory { get; set; } = null!;

    private HubDatabase Database => Factory.Services.GetRequiredService<HubDatabase>();

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync(Users);
        Factory = _hub.WithWebHostBuilder(builder =>
        {
            _provider.Configure(builder);
            builder.UseSetting("Hub:Access:Enabled", "true");
            builder.UseSetting("Hub:MetricsPort", "9464");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
                services.PostConfigure<HubOptions>(options =>
                {
                    options.Sources =
                    [
                        .. options.Sources,
                        Daemon("shop-d", "prod"),
                        Daemon("lab-d", "lab"),
                        new SourceOptions
                        {
                            Id = "tempo", Name = "Tempo", Environment = "prod", Kind = SourceKinds.Tempo,
                            BaseUrl = new Uri("http://127.0.0.1:2")
                        }
                    ];
                    options.Access = Access(_clock.GetUtcNow());
                });
            });
        });
        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _hub.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task A_member_reads_their_identity_subject_groups_and_reach_and_nothing_of_the_configuration()
    {
        var signedInAt = NowMs;
        using var client = await SignedInAsync("member");

        var access = await AccessAsync(client);

        Assert.Equal("member@example.internal", access.GetProperty("identity").GetString());
        Assert.Equal("u-member", access.GetProperty("subject").GetString());
        Assert.Equal("session", access.GetProperty("caller").GetString());
        // The ticket keeps whole seconds.
        Assert.InRange(access.GetProperty("expires_at_ms").GetInt64(),
            signedInAt + 8 * 3_600_000 - 1000, signedInAt + 8 * 3_600_000 + 1000);
        Assert.False(access.GetProperty("admin").GetBoolean());
        // A provider group no grant names was dropped at sign-in.
        Assert.Equal(
            """[{"name":"/teams/shop","origin":"provider"},{"name":"oncall","origin":"internal"}]""",
            access.GetProperty("groups").GetRawText());
        Assert.Equal(
            """[{"group":"/teams/shop","project":"shop","role":"member","permissions":["findings.read","traces.read","analyses.run","acks.write"]},""" +
            """{"group":"internal:oncall","project":"lab","role":"auditor","permissions":["findings.read","traces.read"]}]""",
            access.GetProperty("reach").GetRawText());
        Assert.Equal(
            """[{"name":"shop","patterns":[{"source":null,"service":null,"attribute":"team=shop"}]},""" +
            """{"name":"lab","patterns":[{"source":"lab-d","service":null,"attribute":null}]}]""",
            access.GetProperty("projects").GetRawText());
        Assert.Equal(JsonValueKind.Null, access.GetProperty("configuration").ValueKind);
        Assert.Equal(JsonValueKind.Null, access.GetProperty("unmatched").ValueKind);
        Assert.Equal(JsonValueKind.Null, access.GetProperty("conflicts").ValueKind);
    }

    [Fact]
    public async Task A_caller_no_grant_reaches_still_reads_their_own_page()
    {
        using var client = await SignedInAsync("nogrant");

        using var status = await client.GetAsync("/api/status", TestContext.Current.CancellationToken);
        var access = await AccessAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);
        Assert.Equal("u-none", access.GetProperty("subject").GetString());
        Assert.Equal("[]", access.GetProperty("groups").GetRawText());
        Assert.Equal("[]", access.GetProperty("reach").GetRawText());
        Assert.Equal("[]", access.GetProperty("projects").GetRawText());
    }

    [Fact]
    public async Task A_machine_token_reads_findings_read_alone_and_never_its_token()
    {
        using var client = FakeProvider.Client(Factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CiToken);

        using var response = await client.GetAsync("/api/access", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var access = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("machine:ci", access.GetProperty("identity").GetString());
        Assert.Equal("machine:ci", access.GetProperty("subject").GetString());
        Assert.Equal("machine_token", access.GetProperty("caller").GetString());
        Assert.Equal(_clock.GetUtcNow().AddDays(30).ToUnixTimeMilliseconds(), access.GetProperty("expires_at_ms").GetInt64());
        Assert.False(access.GetProperty("admin").GetBoolean());
        Assert.Equal("""[{"name":"ci","origin":"internal"}]""", access.GetProperty("groups").GetRawText());
        Assert.Equal(
            """[{"group":"internal:ci","project":"shop","role":"member","permissions":["findings.read"]}]""",
            access.GetProperty("reach").GetRawText());
        Assert.Equal(JsonValueKind.Null, access.GetProperty("configuration").ValueKind);
        Assert.DoesNotContain(CiToken, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_subject_listed_in_Admins_reaches_everything_through_their_own_id()
    {
        using var client = await SignedInAsync("root");

        var access = await AccessAsync(client);

        Assert.True(access.GetProperty("admin").GetBoolean());
        Assert.Equal(
            """[{"group":null,"project":"*","role":"admin","permissions":["findings.read","traces.read","sources.read","analyses.run","acks.write","access.admin"]}]""",
            access.GetProperty("reach").GetRawText());
    }

    [Fact]
    public async Task A_machine_token_in_an_admin_group_reaches_everything_with_findings_read_alone()
    {
        await using var hub = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<HubOptions>(options =>
                options.Access.AdminGroups = [.. options.Access.AdminGroups, "internal:ci"])));
        using var client = FakeProvider.Client(hub);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CiToken);

        var access = await AccessAsync(client);

        Assert.False(access.GetProperty("admin").GetBoolean());
        Assert.Equal(
            """[{"group":"internal:ci","project":"*","role":"admin","permissions":["findings.read"]},""" +
            """{"group":"internal:ci","project":"shop","role":"member","permissions":["findings.read"]}]""",
            access.GetProperty("reach").GetRawText());
        Assert.Equal(JsonValueKind.Null, access.GetProperty("configuration").ValueKind);
    }

    [Fact]
    public async Task An_admin_through_a_provider_admin_group_reads_the_whole_configuration_without_any_token()
    {
        using var client = await SignedInAsync("admin");

        using var response = await client.GetAsync("/api/access", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var access = JsonDocument.Parse(body).RootElement;

        Assert.True(access.GetProperty("admin").GetBoolean());
        Assert.Equal(
            """[{"group":"/hub-admins","project":"*","role":"admin","permissions":["findings.read","traces.read","sources.read","analyses.run","acks.write","access.admin"]}]""",
            access.GetProperty("reach").GetRawText());
        Assert.Equal("[]", access.GetProperty("projects").GetRawText());
        var configuration = access.GetProperty("configuration");
        Assert.Equal("shop,lab", Names(configuration.GetProperty("projects")));
        Assert.Equal(
            """[{"name":"reader","permissions":["findings.read","traces.read","analyses.run"],"built_in":true},""" +
            """{"name":"member","permissions":["findings.read","traces.read","analyses.run","acks.write"],"built_in":true},""" +
            """{"name":"admin","permissions":["findings.read","traces.read","sources.read","analyses.run","acks.write","access.admin"],"built_in":true},""" +
            """{"name":"auditor","permissions":["findings.read","traces.read"],"built_in":false}]""",
            configuration.GetProperty("roles").GetRawText());
        Assert.Equal(
            """[{"group":"/teams/shop","project":"shop","role":"member"},{"group":"internal:oncall","project":"lab","role":"auditor"},""" +
            """{"group":"internal:ci","project":"shop","role":"member"}]""",
            configuration.GetProperty("grants").GetRawText());
        Assert.Equal("""[{"name":"oncall","members":["u-member"]},{"name":"ci","members":[]}]""",
            configuration.GetProperty("internal_groups").GetRawText());
        Assert.Equal("""["u-root"]""", configuration.GetProperty("admins").GetRawText());
        Assert.Equal("""["/hub-admins"]""", configuration.GetProperty("admin_groups").GetRawText());
        var now = _clock.GetUtcNow();
        Assert.Equal(
            $$"""[{"name":"ci","group":"internal:ci","expires_at_ms":{{now.AddDays(30).ToUnixTimeMilliseconds()}},"active":true},""" +
            $$"""{"name":"ci-old","group":"internal:ci","expires_at_ms":{{now.AddDays(-1).ToUnixTimeMilliseconds()}},"active":false}]""",
            configuration.GetProperty("machine_tokens").GetRawText());
        Assert.DoesNotContain(CiToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain("ci-old-page-", body, StringComparison.Ordinal);
        Assert.DoesNotContain("fingerprint", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_admin_reads_the_services_attributes_and_sources_no_project_covers()
    {
        using var client = await SignedInAsync("admin");

        var unmatched = (await AccessAsync(client)).GetProperty("unmatched");

        Assert.Equal(
            """[{"service":"cart","source_ids":["shop-d","test"],"findings":2},{"service":"invoice","source_ids":["shop-d"],"findings":1},""" +
            """{"service":"pay","source_ids":["test"],"findings":1}]""",
            unmatched.GetProperty("services").GetRawText());
        Assert.Equal(
            """[{"key":"team","value":"billing","findings":2},{"key":"region","value":"eu","findings":1}]""",
            unmatched.GetProperty("attributes").GetRawText());
        // lab-d is named by a whole-source pattern; a trace backend has no count.
        Assert.Equal(
            """[{"source_id":"shop-d","findings":2},{"source_id":"test","findings":2},{"source_id":"tempo","findings":null}]""",
            unmatched.GetProperty("sources").GetRawText());
        Assert.False(unmatched.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task A_copy_seen_under_team_shop_and_under_no_grouping_is_a_conflict()
    {
        using var client = await SignedInAsync("admin");

        var conflicts = (await AccessAsync(client)).GetProperty("conflicts");

        Assert.Equal(
            $$"""[{"signature":"{{F4}}","source_id":"shop-d","service":"cart","key":"team","values":["shop"],"identities_without_key":1},""" +
            $$"""{"signature":"{{F5}}","source_id":"shop-d","service":"checkout","key":"team","values":["billing","shop"],"identities_without_key":0}]""",
            conflicts.GetProperty("copies").GetRawText());
        Assert.False(conflicts.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Each_admin_list_is_capped_at_200_and_says_so()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var many = Enumerable.Range(100, 201).ToArray();
        await Database.UpsertBatchAsync(Snapshot("shop-d", "prod"), Batch(many.Select(i =>
            Envelope($"slow_sql:s{i}:{i:x40}", $"s{i}", $$"""[{"key":"team","value":"shop"}]"""))), NowMs, cancellationToken);
        await Database.UpsertBatchAsync(Snapshot("shop-d", "prod"), Batch(many.Select(i =>
            Envelope($"slow_sql:s{i}:{i:x40}", $"s{i}", "[]"))), NowMs, cancellationToken);
        await Database.UpsertBatchAsync(Snapshot("test", "test"), Batch(many.Select(i =>
            Envelope($"slow_sql:u{i}:{i:x40}", $"u{i}", $$"""[{"key":"k{{i}}","value":"v"}]"""))), NowMs, cancellationToken);
        using var client = await SignedInAsync("admin");

        var access = await AccessAsync(client);

        var unmatched = access.GetProperty("unmatched");
        Assert.Equal(200, unmatched.GetProperty("services").GetArrayLength());
        Assert.Equal(200, unmatched.GetProperty("attributes").GetArrayLength());
        Assert.True(unmatched.GetProperty("truncated").GetBoolean());
        var conflicts = access.GetProperty("conflicts");
        Assert.Equal(200, conflicts.GetProperty("copies").GetArrayLength());
        Assert.True(conflicts.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task With_access_off_the_route_is_not_mapped()
    {
        // The real clock: the client's cookie jar drops a cookie that expired by its own.
        await using var authOn = _hub.WithWebHostBuilder(builder =>
        {
            _provider.Configure(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });
        using var signedIn = FakeProvider.Client(authOn);
        using var callback = await FakeProvider.SignInAsync(signedIn, "member");
        using var anonymous = _hub.CreateClient();

        using var off = await anonymous.GetAsync("/api/access", TestContext.Current.CancellationToken);
        using var offWithAuth = await signedIn.GetAsync("/api/access", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, offWithAuth.StatusCode);
    }

    // ------------------------------------------------------------ helpers

    private static AccessOptions Access(DateTimeOffset now)
    {
        return new AccessOptions
        {
            Enabled = true,
            Admins = ["u-root"],
            AdminGroups = ["/hub-admins"],
            Groups =
            [
                new InternalGroupOptions { Name = "oncall", Members = ["u-member"] },
                new InternalGroupOptions { Name = "ci" }
            ],
            Projects =
            [
                new ProjectOptions { Name = "shop", Patterns = [new PatternOptions { Attribute = "team=shop" }] },
                new ProjectOptions { Name = "lab", Patterns = [new PatternOptions { Source = "lab-d" }] }
            ],
            Roles = [new RoleOptions { Name = "auditor", Permissions = ["findings.read", "traces.read"] }],
            Grants =
            [
                new GrantOptions { Group = "/teams/shop", Project = "shop", Role = "member" },
                new GrantOptions { Group = "internal:oncall", Project = "lab", Role = "auditor" },
                new GrantOptions { Group = "internal:ci", Project = "shop", Role = "member" }
            ],
            MachineTokens =
            [
                new MachineTokenOptions { Name = "ci", Group = "internal:ci", Token = CiToken, ExpiresAt = now.AddDays(30) },
                new MachineTokenOptions
                {
                    Name = "ci-old", Group = "internal:ci", Token = "ci-old-page-0123456789abcdef0123456789abcdef", // gitleaks:allow -- synthetic test credential
                    ExpiresAt = now.AddDays(-1)
                }
            ]
        };
    }

    private static SourceOptions Daemon(string id, string environment)
    {
        return new SourceOptions { Id = id, Name = id, Environment = environment, BaseUrl = new Uri("http://127.0.0.1:1") };
    }

    private async Task SeedAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var retention = Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HubOptions>>().Value.Retention;
        await Database.UpsertBatchAsync(Snapshot("shop-d", "prod"), Batch(
        [
            Envelope(Stale, "stale", "[]")
        ]), NowMs - (long)retention.TotalMilliseconds - 3_600_000, cancellationToken);
        await Database.UpsertBatchAsync(Snapshot("shop-d", "prod"), Batch(
        [
            Envelope(F1, "cart", """[{"key":"team","value":"shop"}]"""),
            Envelope(F2, "invoice", """[{"key":"team","value":"billing"}]"""),
            Envelope(F3, "cart", "[]"),
            Envelope(F4, "cart", """[{"key":"team","value":"shop"}]"""),
            Envelope(F5, "checkout", """[{"key":"team","value":"shop"}]""")
        ]), NowMs, cancellationToken);
        await Database.UpsertBatchAsync(Snapshot("shop-d", "prod"), Batch(
        [
            Envelope(F4, "cart", "[]"),
            Envelope(F5, "checkout", """[{"key":"team","value":"billing"}]""")
        ]), NowMs, cancellationToken);
        await Database.UpsertBatchAsync(Snapshot("lab-d", "lab"), Batch(
            [Envelope(F6, "labsvc", """[{"key":"team","value":"lab"}]""")]), NowMs, cancellationToken);
        await Database.UpsertBatchAsync(Snapshot("test", "test"), Batch(
        [
            Envelope(F7, "pay", """[{"key":"region","value":"eu"}]"""),
            Envelope(F9, "cart", """[{"key":"team","value":"billing"}]""")
        ]), NowMs, cancellationToken);
    }

    private static SourceSnapshot Snapshot(string source, string environment)
    {
        return new SourceSnapshot(source, source, environment, "0.26.2");
    }

    private static ParsedBatch Batch(IEnumerable<string> envelopes)
    {
        return FindingParser.Parse(Encoding.UTF8.GetBytes($"[{string.Join(',', envelopes)}]"));
    }

    private static string Envelope(string signature, string service, string grouping)
    {
        return $$"""
                 {"finding":{"type":"slow_sql","severity":"warning","trace_id":"trace-x","service":"{{service}}",
                  "grouping":{{grouping}},"source_endpoint":"GET /orders","pattern":{"template":"SELECT 1"},
                  "confidence":"daemon_production","signature":"{{signature}}"},"first_seen_ms":1}
                 """;
    }

    private async Task<HttpClient> SignedInAsync(string user)
    {
        var client = FakeProvider.Client(Factory);
        using var callback = await FakeProvider.SignInAsync(client, user);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return client;
    }

    private static async Task<JsonElement> AccessAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/access", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    private static string Names(JsonElement array)
    {
        return string.Join(',', array.EnumerateArray().Select(item => item.GetProperty("name").GetString()));
    }
}
