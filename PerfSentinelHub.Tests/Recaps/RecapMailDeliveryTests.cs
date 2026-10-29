using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Recaps;

/// <summary>
///     A frozen report's e-mail deliveries through a relay: settled on a 250, retried on a 4xx or when the
///     relay does not answer (once per pass, not once per address), failed at once on a 5xx, and failed
///     without a connection when no relay is configured.
/// </summary>
public sealed class RecapMailDeliveryTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Wire = new()
    { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly List<WebApplicationFactory<Program>> _hubs = [];
    private FakeSmtp _relay = null!;
    private string _reportId = "";

    public async ValueTask InitializeAsync()
    {
        _relay = await FakeSmtp.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hub in _hubs)
            await hub.DisposeAsync();
        await _relay.DisposeAsync();
    }

    [Fact]
    public async Task A_mail_reaches_the_relay_and_settles_the_delivery()
    {
        var hub = await ReportedAsync(Relay(_relay.Port), "pm@example.test");

        await RunAsync(hub);

        var message = Assert.Single(_relay.Received);
        Assert.Equal(["pm@example.test"], message.To);
        Assert.Equal("hub", message.AuthUser);
        Assert.Contains("Subject: Perf Sentinel recap: week 2026-10-19 to 2026-10-25", message.Data,
            StringComparison.Ordinal);
        var delivery = Assert.Single(await DeliveriesAsync(hub));
        Assert.Equal(DeliveryStates.Delivered, delivery.State);
        Assert.Equal(250, delivery.LastStatus);
    }

    [Fact]
    public async Task A_relay_offering_gssapi_first_still_gets_login()
    {
        _relay.AuthMechanisms = "GSSAPI NTLM LOGIN";
        var hub = await ReportedAsync(Relay(_relay.Port), "pm@example.test");

        await RunAsync(hub);

        Assert.Equal("hub", Assert.Single(_relay.Received).AuthUser);
        Assert.Equal(DeliveryStates.Delivered, Assert.Single(await DeliveriesAsync(hub)).State);
        // The image has no libgssapi: a client that so much as tries GSSAPI or NTLM breaks there.
        Assert.Equal(["login"], _relay.AuthAttempts);
    }

    [Fact]
    public async Task A_4xx_from_the_relay_is_retried()
    {
        _relay.RcptReply = "451 4.7.1 later";
        var hub = await ReportedAsync(Relay(_relay.Port), "pm@example.test");

        await RunAsync(hub);

        var delivery = Assert.Single(await DeliveriesAsync(hub));
        Assert.Equal(DeliveryStates.Pending, delivery.State);
        Assert.Equal(451, delivery.LastStatus);
        Assert.Equal(1, delivery.Attempts);
        Assert.Equal(delivery.LastAttemptMs + 30_000, delivery.NextAttemptMs);
        Assert.Empty(_relay.Received);
    }

    [Fact]
    public async Task A_5xx_from_the_relay_fails_at_once()
    {
        _relay.RcptReply = "550 5.1.1 nobody";
        var hub = await ReportedAsync(Relay(_relay.Port), "pm@example.test");

        await RunAsync(hub);

        var delivery = Assert.Single(await DeliveriesAsync(hub));
        Assert.Equal(DeliveryStates.Failed, delivery.State);
        Assert.Equal(1, delivery.Attempts);
        Assert.Equal(550, delivery.LastStatus);
    }

    [Fact]
    public async Task An_unreachable_relay_is_retried()
    {
        var port = await StoppedPortAsync();
        var hub = await ReportedAsync(Relay(port), "pm@example.test");

        await RunAsync(hub);

        var delivery = Assert.Single(await DeliveriesAsync(hub));
        Assert.Equal(DeliveryStates.Pending, delivery.State);
        Assert.Equal(1, delivery.Attempts);
        Assert.Null(delivery.LastStatus);
        Assert.NotNull(delivery.NextAttemptMs);
    }

    [Fact]
    public async Task An_unreachable_relay_is_tried_once_per_pass()
    {
        // A relay that accepts and drops each connection: the only way to count attempts.
        var accepted = 0;
        using var dropper = new TcpListener(IPAddress.Loopback, 0);
        dropper.Start();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var _ = await dropper.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                    Interlocked.Increment(ref accepted);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException
                                                  or SocketException)
            {
                // Stopped.
            }
        }, TestContext.Current.CancellationToken);
        var hub = await ReportedAsync(
            Relay(((IPEndPoint)dropper.LocalEndpoint).Port), "a@example.test", "b@example.test", "c@example.test");

        await RunAsync(hub);

        dropper.Stop();
        await accepting;
        Assert.Equal(1, accepted);
        var deliveries = await DeliveriesAsync(hub);
        Assert.Equal(3, deliveries.Count);
        Assert.All(deliveries, delivery =>
        {
            Assert.Equal(DeliveryStates.Pending, delivery.State);
            Assert.Equal(1, delivery.Attempts);
            Assert.Null(delivery.LastStatus);
        });
    }

    [Fact]
    public async Task Mail_without_an_smtp_host_fails_without_a_connection()
    {
        var hub = await ReportedAsync(new RecapSmtpOptions(), "pm@example.test");

        await RunAsync(hub);

        Assert.Empty(_relay.Received);
        var delivery = Assert.Single(await DeliveriesAsync(hub));
        Assert.Equal(DeliveryStates.Failed, delivery.State);
        Assert.Null(delivery.LastStatus);
    }

    private static RecapSmtpOptions Relay(int port)
    {
        return new RecapSmtpOptions
        {
            Host = "127.0.0.1",
            Port = port,
            StartTls = false,
            UserName = "hub",
            Password = "secret", // gitleaks:allow -- synthetic test credential
            From = "hub@example.test"
        };
    }

    private static async Task<int> StoppedPortAsync()
    {
        await using var stopped = await FakeSmtp.StartAsync(CancellationToken.None);
        return stopped.Port;
    }

    private async Task<WebApplicationFactory<Program>> ReportedAsync(RecapSmtpOptions smtp, params string[] addresses)
    {
        var hub = new HubApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
                options.Recaps = new RecapsOptions { Smtp = smtp })));
        _hubs.Add(hub);
        _reportId = Guid.NewGuid().ToString("N");
        var report = new RecapReportRecord(
            new RecapReportSummary(
                _reportId, "weekly-shop", "week", "2026-10-19", "2026-10-25",
                [RecapViewNames.Pilotage, RecapViewNames.Tech], true, false, null, 10_000),
            JsonSerializer.Serialize(RecapSnapshots.Distinctive(), Wire));
        Assert.True(await hub.Services.GetRequiredService<HubDatabase>().InsertRecapReportAsync(
            report, [.. addresses.Select(address => (RecapChannels.Email, address))], CancellationToken.None));
        return hub;
    }

    private static Task RunAsync(WebApplicationFactory<Program> hub)
    {
        return hub.Services.GetRequiredService<RecapDeliveryWorker>()
            .RunOnceAsync(TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<RecapDeliveryRecord>> DeliveriesAsync(WebApplicationFactory<Program> hub)
    {
        return await hub.Services.GetRequiredService<HubDatabase>()
            .ListRecapDeliveriesAsync([_reportId], CancellationToken.None);
    }
}
