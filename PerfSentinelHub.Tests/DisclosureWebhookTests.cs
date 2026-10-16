using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Disclosures;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

/// <summary>
///     Published and withdrawn events, signed, sent from the deliveries table so a restart
///     resumes them, retried on anything but a 2xx, and never sent to a redirect target.
/// </summary>
public sealed class DisclosureWebhookTests : IAsyncLifetime
{
    private const string ImportKey = "disclosure-import-0123456789abcdef0123"; // gitleaks:allow -- synthetic test credential
    private const string Secret = "webhook-secret-0123456789abcdef0123"; // gitleaks:allow -- synthetic test credential

    private readonly ConcurrentQueue<Received> _received = new();
    private readonly ConcurrentQueue<int> _statuses = new();
    private readonly List<WebApplicationFactory<Program>> _hubs = [];
    private FakeDaemon _receiver = null!;
    private int _redirectTargetHits;

    public async ValueTask InitializeAsync()
    {
        _receiver = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/elsewhere")
            {
                Interlocked.Increment(ref _redirectTargetHits);
                return;
            }

            using var reader = new StreamReader(context.Request.Body);
            _received.Enqueue(new Received(
                context.Request.Headers[WebhookDelivery.TimestampHeader].ToString(),
                context.Request.Headers[WebhookDelivery.SignatureHeader].ToString(),
                await reader.ReadToEndAsync(context.RequestAborted)));
            var status = _statuses.TryDequeue(out var next) ? next : StatusCodes.Status204NoContent;
            if (status == StatusCodes.Status302Found)
                context.Response.Headers.Location = "/elsewhere";
            context.Response.StatusCode = status;
        }, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hub in _hubs)
            await hub.DisposeAsync();
        await _receiver.DisposeAsync();
    }

    [Fact]
    public void The_signature_matches_a_fixed_vector()
    {
        Assert.Equal(
            "sha256=5fe835750b212f54a758b691e3f8458fedd6d3945c8adc9b3910d184182b327f",
            WebhookDelivery.Sign(new string('s', 32), 1_800_000_000, "{}"u8));
    }

    [Fact]
    public async Task A_receiver_gets_a_signed_published_event_with_a_share_link()
    {
        var hub = await PublishedAsync(includeShareLink: true);

        await RunAsync(hub);

        var received = Assert.Single(_received);
        Assert.Equal(WebhookDelivery.Sign(Secret, long.Parse(received.Timestamp), Encoding.UTF8.GetBytes(received.Body)),
            received.Signature);
        using var body = JsonDocument.Parse(received.Body);
        Assert.Equal("published", body.RootElement.GetProperty("event").GetString());
        Assert.Equal("d1", body.RootElement.GetProperty("disclosure_id").GetString());
        Assert.Equal("sha256:aaaa", body.RootElement.GetProperty("content_hash").GetString());
        Assert.StartsWith("https://hub.example/share/pshare_", body.RootElement.GetProperty("share_url").GetString());
        Assert.EndsWith("/report.pdf", body.RootElement.GetProperty("share_url").GetString());
        var token = body.RootElement.GetProperty("share_url").GetString()!.Split('/')[4];
        Assert.NotNull(await Database(hub).FindShareLinkAsync(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)), 10_000, CancellationToken.None));
        Assert.Equal(DeliveryStates.Delivered, Assert.Single(await Database(hub).ListDeliveriesAsync("d1", CancellationToken.None)).State);
    }

    [Fact]
    public async Task A_500_is_retried_after_its_backoff_and_a_2xx_settles_it()
    {
        var hub = await PublishedAsync();
        _statuses.Enqueue(StatusCodes.Status500InternalServerError);

        await RunAsync(hub);
        await RunAsync(hub);
        Assert.Single(_received);
        ((FakeTimeProvider)hub.Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromSeconds(30));
        await RunAsync(hub);

        Assert.Equal(2, _received.Count);
        var delivery = Assert.Single(await Database(hub).ListDeliveriesAsync("d1", CancellationToken.None));
        Assert.Equal(DeliveryStates.Delivered, delivery.State);
        Assert.Equal(2, delivery.Attempts);
        await RunAsync(hub);
        Assert.Equal(2, _received.Count);
    }

    [Fact]
    public async Task A_redirect_is_a_failure_and_is_never_followed()
    {
        var hub = await PublishedAsync(includeShareLink: true);
        _statuses.Enqueue(StatusCodes.Status302Found);

        await RunAsync(hub);

        Assert.Equal(0, _redirectTargetHits);
        var delivery = Assert.Single(await Database(hub).ListDeliveriesAsync("d1", CancellationToken.None));
        Assert.Equal(DeliveryStates.Pending, delivery.State);
        Assert.Equal(302, delivery.LastStatus);
        // The link that went nowhere died with the attempt.
        Assert.NotNull(Assert.Single(await Database(hub).ListShareLinksAsync("d1", CancellationToken.None)).RevokedAtMs);
    }

    [Fact]
    public async Task The_last_attempt_gives_up_and_is_counted()
    {
        var hub = await PublishedAsync();
        var database = Database(hub);
        var delivery = Assert.Single(await database.ListDeliveriesAsync("d1", CancellationToken.None));
        for (var i = 0; i < DeliveryWorker.MaxAttempts - 1; i++)
            await database.RecordDeliveryAttemptAsync(delivery.Id, 500, DeliveryStates.Pending, 10_000, 10_000, CancellationToken.None);
        _statuses.Enqueue(StatusCodes.Status500InternalServerError);

        await RunAsync(hub);

        Assert.Equal(DeliveryStates.Failed, Assert.Single(await database.ListDeliveriesAsync("d1", CancellationToken.None)).State);
        Assert.Contains("perf_sentinel_hub_disclosure_deliveries_failed_total 1",
            await hub.CreateClient().GetStringAsync("/metrics", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_restarted_hub_sends_what_was_due_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-webhook-{Guid.NewGuid():N}.db");
        try
        {
            var first = await PublishedAsync(databasePath: path);
            await first.DisposeAsync();
            _hubs.Remove(first);

            var second = Hub(databasePath: path);
            await RunAsync(second);
            await RunAsync(second);

            Assert.Single(_received);
        }
        finally
        {
            TestPool.ClearFor(path);
            File.Delete(path);
            File.Delete($"{path}-shm");
            File.Delete($"{path}-wal");
        }
    }

    [Fact]
    public async Task A_withdrawn_event_carries_no_share_link()
    {
        var hub = await PublishedAsync(includeShareLink: true);
        await RunAsync(hub);
        await Database(hub).WithdrawAsync("d1", "wrong", "alice", 10_000, ["auditors"], CancellationToken.None);

        await RunAsync(hub);

        using var body = JsonDocument.Parse(_received.Last().Body);
        Assert.Equal("withdrawn", body.RootElement.GetProperty("event").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("share_url").ValueKind);
    }

    private async Task<WebApplicationFactory<Program>> PublishedAsync(bool includeShareLink = false, string? databasePath = null)
    {
        var hub = Hub(includeShareLink, databasePath);
        await DisclosuresApiTests.SeedAsync(hub, "d1", "u1", "2026-09-30");
        await Database(hub).EnqueueDeliveriesAsync("d1", "published", ["auditors"], 10_000, CancellationToken.None);
        return hub;
    }

    private WebApplicationFactory<Program> Hub(bool includeShareLink = false, string? databasePath = null)
    {
        var hub = new HubApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
            {
                if (databasePath is not null)
                    options.DatabasePath = databasePath;
                options.Disclosures = new DisclosuresOptions
                {
                    ImportKey = ImportKey,
                    PublicUrl = new Uri("https://hub.example"),
                    Webhooks =
                    [
                        new WebhookOptions
                        {
                            Name = "auditors", Url = new Uri(_receiver.BaseUrl, "hook"), Secret = Secret,
                            IncludeShareLink = includeShareLink
                        }
                    ]
                };
            })));
        _hubs.Add(hub);
        return hub;
    }

    private static Task RunAsync(WebApplicationFactory<Program> hub)
    {
        return hub.Services.GetRequiredService<DeliveryWorker>().RunOnceAsync(TestContext.Current.CancellationToken);
    }

    private static HubDatabase Database(WebApplicationFactory<Program> hub)
    {
        return hub.Services.GetRequiredService<HubDatabase>();
    }

    private sealed record Received(string Timestamp, string Signature, string Body);
}
