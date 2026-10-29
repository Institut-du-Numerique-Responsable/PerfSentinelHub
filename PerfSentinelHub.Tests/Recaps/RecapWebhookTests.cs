using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Disclosures;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Recaps;

/// <summary>
///     A frozen report's webhook deliveries: signed, carrying the management recap only, retried on
///     anything but a 2xx, never sent to a redirect target, and failed without a request once the
///     webhook or the report is gone.
/// </summary>
public sealed class RecapWebhookTests : IAsyncLifetime
{
    private const string Secret = "recap-secret-0123456789abcdef0123"; // gitleaks:allow -- synthetic test credential

    // HubJsonContext is internal to the Hub; its options are snake_case_lower, which these mirror.
    private static readonly JsonSerializerOptions Wire = new()
    { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly List<WebApplicationFactory<Program>> _hubs = [];
    private readonly ConcurrentQueue<Received> _received = new();
    private readonly ConcurrentQueue<int> _statuses = new();
    private FakeDaemon _receiver = null!;
    private int _redirectTargetHits;
    private string _reportId = "";

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
    public async Task A_recap_webhook_is_signed_and_carries_the_management_recap_only()
    {
        var hub = await ReportedAsync();

        await RunAsync(hub);

        var received = Assert.Single(_received);
        Assert.Equal(
            WebhookDelivery.Sign(Secret, long.Parse(received.Timestamp), Encoding.UTF8.GetBytes(received.Body)),
            received.Signature);
        using var body = JsonDocument.Parse(received.Body);
        Assert.Equal("recap", body.RootElement.GetProperty("event").GetString());
        Assert.Equal("weekly-shop", body.RootElement.GetProperty("subscription").GetString());
        Assert.Equal("pilotage", body.RootElement.GetProperty("recap").GetProperty("view").GetString());
        var period = body.RootElement.GetProperty("period");
        Assert.Equal("week", period.GetProperty("kind").GetString());
        Assert.Equal("2026-10-19", period.GetProperty("from").GetString());
        Assert.Equal("2026-10-25", period.GetProperty("to").GetString());
        Assert.True(body.RootElement.GetProperty("complete").GetBoolean());
        foreach (var value in RecapSnapshots.Values)
            Assert.DoesNotContain(value, received.Body, StringComparison.Ordinal);
        Assert.Equal(DeliveryStates.Delivered, Assert.Single(await DeliveriesAsync(hub)).State);
    }

    [Fact]
    public async Task An_unlabelled_row_reaches_the_webhook_without_its_endpoint()
    {
        var snapshot = RecapSnapshots.Distinctive() with
        {
            Priorities = [RecapSnapshots.Priority(1, null)],
            Movements = new RecapMovements([RecapSnapshots.Move(null)], [], 0, [], [], [], [])
        };
        var hub = await ReportedAsync(snapshot: snapshot);

        await RunAsync(hub);

        var received = Assert.Single(_received);
        Assert.DoesNotContain(RecapSnapshots.Endpoint, received.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"endpoint\"", received.Body, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(received.Body);
        var priority = body.RootElement.GetProperty("recap").GetProperty("priorities")[0];
        Assert.Equal(JsonValueKind.Null, priority.GetProperty("label").ValueKind);
        Assert.Equal("checkout-api", priority.GetProperty("service").GetString());
    }

    [Fact]
    public async Task With_a_public_url_the_links_name_the_hub()
    {
        var hub = await ReportedAsync("https://hub.example/");

        await RunAsync(hub);

        using var body = JsonDocument.Parse(Assert.Single(_received).Body);
        var id = body.RootElement.GetProperty("report_id").GetString();
        Assert.Equal($"https://hub.example/recaps/{id}/pilotage.html",
            body.RootElement.GetProperty("pilotage_url").GetString());
        Assert.Equal($"https://hub.example/recaps/{id}/tech.html",
            body.RootElement.GetProperty("tech_url").GetString());
    }

    [Fact]
    public async Task A_report_without_the_engineering_view_has_no_engineering_link()
    {
        var hub = await ReportedAsync("https://hub.example/", views: [RecapViewNames.Pilotage]);

        await RunAsync(hub);

        using var body = JsonDocument.Parse(Assert.Single(_received).Body);
        Assert.NotEqual(JsonValueKind.Null, body.RootElement.GetProperty("pilotage_url").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("tech_url").ValueKind);
    }

    [Fact]
    public async Task Without_a_public_url_the_links_are_null()
    {
        var hub = await ReportedAsync();

        await RunAsync(hub);

        using var body = JsonDocument.Parse(Assert.Single(_received).Body);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("pilotage_url").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("tech_url").ValueKind);
    }

    [Fact]
    public async Task The_disclosure_public_url_is_the_fallback()
    {
        var hub = await ReportedAsync(disclosureUrl: "https://disclosures.example/");

        await RunAsync(hub);

        using var body = JsonDocument.Parse(Assert.Single(_received).Body);
        Assert.StartsWith("https://disclosures.example/recaps/",
            body.RootElement.GetProperty("pilotage_url").GetString());
    }

    [Fact]
    public async Task A_500_is_retried_after_its_backoff_and_a_2xx_settles_it()
    {
        var hub = await ReportedAsync();
        _statuses.Enqueue(StatusCodes.Status500InternalServerError);

        await RunAsync(hub);
        await RunAsync(hub);
        Assert.Single(_received);
        ((FakeTimeProvider)hub.Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromSeconds(30));
        await RunAsync(hub);

        Assert.Equal(2, _received.Count);
        var delivery = Assert.Single(await DeliveriesAsync(hub));
        Assert.Equal(DeliveryStates.Delivered, delivery.State);
        Assert.Equal(2, delivery.Attempts);
        await RunAsync(hub);
        Assert.Equal(2, _received.Count);
    }

    [Fact]
    public async Task A_redirect_is_a_failure_and_is_never_followed()
    {
        var hub = await ReportedAsync();
        _statuses.Enqueue(StatusCodes.Status302Found);

        await RunAsync(hub);

        Assert.Equal(0, _redirectTargetHits);
        var delivery = Assert.Single(await DeliveriesAsync(hub));
        Assert.Equal(DeliveryStates.Pending, delivery.State);
        Assert.Equal(302, delivery.LastStatus);
    }

    [Fact]
    public async Task The_last_attempt_gives_up()
    {
        var hub = await ReportedAsync();
        var delivery = Assert.Single(await DeliveriesAsync(hub));
        for (var i = 0; i < DeliveryWorker.MaxAttempts - 1; i++)
            await Database(hub).RecordRecapDeliveryAttemptAsync(
                delivery.Id, 500, DeliveryStates.Pending, 10_000, 10_000, CancellationToken.None);
        _statuses.Enqueue(StatusCodes.Status500InternalServerError);

        await RunAsync(hub);

        var after = Assert.Single(await DeliveriesAsync(hub));
        Assert.Equal(DeliveryStates.Failed, after.State);
        Assert.Equal(DeliveryWorker.MaxAttempts, after.Attempts);
        Assert.Single(_received);
    }

    [Fact]
    public async Task A_webhook_removed_from_the_configuration_fails_without_a_request()
    {
        var hub = await ReportedAsync(target: "retired");

        await RunAsync(hub);

        Assert.Empty(_received);
        Assert.Equal(DeliveryStates.Failed, Assert.Single(await DeliveriesAsync(hub)).State);
    }

    [Fact]
    public async Task A_purged_report_fails_its_delivery_without_a_request()
    {
        var hub = await ReportedAsync();
        // The purge removes both rows together; this is the gap between a pass reading a due row and the purge.
        await using (var connection = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = hub.Services.GetRequiredService<IOptions<HubOptions>>().Value.DatabasePath,
                             Pooling = false
                         }.ToString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM recap_reports;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await RunAsync(hub);

        Assert.Empty(_received);
        Assert.Equal(DeliveryStates.Failed, Assert.Single(await DeliveriesAsync(hub)).State);
    }

    [Fact]
    public async Task An_email_delivery_makes_no_webhook_request()
    {
        var hub = await ReportedAsync(channel: RecapChannels.Email, target: "ops@acme.test");

        await RunAsync(hub);

        // No relay is configured here, so the e-mail row fails; the mail tests cover the relay.
        Assert.Empty(_received);
        Assert.Equal(DeliveryStates.Failed, Assert.Single(await DeliveriesAsync(hub)).State);
    }

    [Fact]
    public async Task A_restarted_hub_sends_what_was_due_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-recap-webhook-{Guid.NewGuid():N}.db");
        try
        {
            var first = await ReportedAsync(databasePath: path);
            await first.DisposeAsync();
            _hubs.Remove(first);

            var second = Hub(null, null, path);
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

    private async Task<WebApplicationFactory<Program>> ReportedAsync(
        string? publicUrl = null,
        string? disclosureUrl = null,
        string[]? views = null,
        string channel = RecapChannels.Webhook,
        string target = "ops-chat",
        string? databasePath = null,
        RecapSnapshot? snapshot = null)
    {
        var hub = Hub(publicUrl, disclosureUrl, databasePath);
        _reportId = Guid.NewGuid().ToString("N");
        var report = new RecapReportRecord(
            new RecapReportSummary(
                _reportId, "weekly-shop", "week", "2026-10-19", "2026-10-25",
                views ?? [RecapViewNames.Pilotage, RecapViewNames.Tech], true, false, null, 10_000),
            JsonSerializer.Serialize(snapshot ?? RecapSnapshots.Distinctive(), Wire));
        Assert.True(await Database(hub).InsertRecapReportAsync(report, [(channel, target)], CancellationToken.None));
        return hub;
    }

    private WebApplicationFactory<Program> Hub(string? publicUrl, string? disclosureUrl, string? databasePath = null)
    {
        var hub = new HubApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
            {
                if (databasePath is not null)
                    options.DatabasePath = databasePath;
                options.Disclosures = new DisclosuresOptions
                {
                    PublicUrl = disclosureUrl is null ? null : new Uri(disclosureUrl)
                };
                options.Recaps = new RecapsOptions
                {
                    PublicUrl = publicUrl is null ? null : new Uri(publicUrl),
                    Webhooks =
                    [
                        new WebhookOptions
                            { Name = "ops-chat", Url = new Uri(_receiver.BaseUrl, "hook"), Secret = Secret }
                    ]
                };
            })));
        _hubs.Add(hub);
        return hub;
    }

    private static Task RunAsync(WebApplicationFactory<Program> hub)
    {
        return hub.Services.GetRequiredService<RecapDeliveryWorker>()
            .RunOnceAsync(TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<RecapDeliveryRecord>> DeliveriesAsync(
        WebApplicationFactory<Program> hub)
    {
        return await Database(hub).ListRecapDeliveriesAsync([_reportId], CancellationToken.None);
    }

    private static HubDatabase Database(WebApplicationFactory<Program> hub)
    {
        return hub.Services.GetRequiredService<HubDatabase>();
    }

    private sealed record Received(string Timestamp, string Signature, string Body);
}
