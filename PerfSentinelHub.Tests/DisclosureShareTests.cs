using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Disclosures;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

/// <summary>
///     The anonymous share route: five fixed files, one identical 404 for every other case,
///     headers that keep the token out of referrers and caches, and no write per request.
/// </summary>
public sealed class DisclosureShareTests : IDisposable
{
    private const string
        ImportKey = "disclosure-import-0123456789abcdef0123"; // gitleaks:allow -- synthetic test credential

    private const string Token = "pshare_live0123456789abcdef0123456789abcdef0123";
    private const long NowMs = 10_000;

    private static readonly string[] Files =
        ["report.json", "attestation.intoto.jsonl", "bundle.sig", "report.pdf", "SHA256SUMS"];

    private readonly List<WebApplicationFactory<Program>> _hubs = [];

    public void Dispose()
    {
        foreach (var hub in _hubs)
            hub.Dispose();
    }

    [Fact]
    public async Task Each_file_is_served_as_an_attachment_with_the_four_headers()
    {
        var hub = await SharedAsync();
        using var client = hub.CreateClient();

        foreach (var name in Files)
        {
            using var response = await client.GetAsync($"/share/{Token}/{name}", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertGuarded(response);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal(name,
                response.Content.Headers.ContentDisposition?.FileNameStar ??
                response.Content.Headers.ContentDisposition?.FileName);
        }
    }

    [Theory]
    [InlineData("/share/" + Token + "/")]
    [InlineData("/share/" + Token + "/hub.db")]
    [InlineData("/share/" + Token + "/report.json/x")]
    [InlineData("/share/pshare_unknown0123456789abcdef0123456789abcdef/report.json")]
    [InlineData("/share/not-a-token/report.json")]
    [InlineData("/share/")]
    public async Task Anything_else_is_the_same_bare_404(string path)
    {
        var hub = await SharedAsync();
        using var client = hub.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        await AssertBare404Async(response);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("withdrawn")]
    public async Task A_dead_link_is_the_same_bare_404(string death)
    {
        var hub = await SharedAsync(death == "expired" ? NowMs : NowMs + 86_400_000);
        var database = Database(hub);
        if (death == "revoked")
            await database.RevokeShareLinkAsync("d1", "l1", "alice", NowMs, CancellationToken.None);
        if (death == "withdrawn")
            await database.WithdrawAsync("d1", "wrong", "alice", NowMs, CancellationToken.None);
        using var client = hub.CreateClient();

        using var response =
            await client.GetAsync($"/share/{Token}/report.json", TestContext.Current.CancellationToken);

        await AssertBare404Async(response);
    }

    [Fact]
    public async Task Downloads_are_counted_in_memory_and_written_once_on_flush()
    {
        var hub = await SharedAsync();
        using var client = hub.CreateClient();
        var database = Database(hub);

        for (var i = 0; i < 100; i++)
            (await client.GetAsync($"/share/{Token}/report.pdf", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(0, Assert.Single(await database.ListShareLinksAsync("d1", CancellationToken.None)).Downloads);
        await hub.Services.GetRequiredService<ShareDownloadCounter>().FlushAsync(TestContext.Current.CancellationToken);
        var link = Assert.Single(await database.ListShareLinksAsync("d1", CancellationToken.None));
        Assert.Equal(100, link.Downloads);
        // No trusted proxy configured: the address is not recorded.
        Assert.Null(link.LastClient);
    }

    [Fact]
    public async Task A_withdrawal_kills_a_link_at_once_whatever_it_counted()
    {
        var hub = await SharedAsync();
        using var client = hub.CreateClient();
        (await client.GetAsync($"/share/{Token}/report.json", TestContext.Current.CancellationToken)).Dispose();

        await Database(hub).WithdrawAsync("d1", "wrong", "alice", NowMs, CancellationToken.None);

        await AssertBare404Async(await client.GetAsync($"/share/{Token}/report.json",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Verify_hash_finds_the_attestation_and_bundle_beside_the_report()
    {
        var hub = await SharedAsync();
        using var client = hub.CreateClient();
        var report = new Uri($"http://localhost/share/{Token}/report.json");

        foreach (var sibling in new[] { "attestation.intoto.jsonl", "bundle.sig" })
        {
            using var response = await client.GetAsync(new Uri(report, sibling), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_hub_without_disclosures_has_no_share_route()
    {
        var hub = await SharedAsync(importKey: null);
        using var client = hub.CreateClient();

        using var response =
            await client.GetAsync($"/share/{Token}/report.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // Not mapped at all: the route's own guarded 404 never answers.
        Assert.False(response.Headers.Contains("X-Robots-Tag"));
    }

    [Fact]
    public async Task A_busy_share_route_is_503_before_any_lookup()
    {
        var hub = await SharedAsync();
        var gate = hub.Services.GetRequiredService<ShareGate>();
        var held = 0;
        while (gate.TryEnter())
            held++;
        using var client = hub.CreateClient();

        using var response = await client.GetAsync($"/share/{Token}/report.pdf", TestContext.Current.CancellationToken);

        for (var i = 0; i < held; i++)
            gate.Exit();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertGuarded(response);
        Assert.Equal("1", response.Headers.RetryAfter?.ToString());
    }

    private static void AssertGuarded(HttpResponseMessage response)
    {
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
    }

    private static async Task AssertBare404Async(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            AssertGuarded(response);
            Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    private async Task<WebApplicationFactory<Program>> SharedAsync(long expiresAtMs = NowMs + 86_400_000,
        string? importKey = ImportKey)
    {
        var hub = new HubApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
                options.Disclosures = new DisclosuresOptions { ImportKey = importKey })));
        _hubs.Add(hub);
        await DisclosuresApiTests.SeedAsync(hub, "d1", "u1", "2026-09-30", [1], [37, 80, 68, 70]);
        var database = Database(hub);
        await using (var connection = await database.OpenConnectionAsync(CancellationToken.None))
        await using (var bundle = connection.CreateCommand())
        {
            bundle.CommandText = "UPDATE disclosures SET bundle = x'0102' WHERE id = 'd1';";
            await bundle.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await database.InsertShareLinkAsync(
            new ShareLinkRecord("l1", "d1", SHA256.HashData(Encoding.UTF8.GetBytes(Token)), "alice", NowMs,
                expiresAtMs),
            CancellationToken.None);
        return hub;
    }

    private static HubDatabase Database(WebApplicationFactory<Program> hub)
    {
        return hub.Services.GetRequiredService<HubDatabase>();
    }
}
