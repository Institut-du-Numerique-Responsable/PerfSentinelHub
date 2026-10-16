using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Disclosures;

/// <summary>
///     Sends the published and withdrawn events from the disclosure_deliveries rows, so a
///     restart resumes what was due. A 2xx settles a delivery. Anything else, a redirect
///     included, is retried with a doubling backoff and given up after MaxAttempts.
/// </summary>
public sealed partial class DeliveryWorker(
    HubDatabase database,
    IHttpClientFactory clients,
    DisclosureMetrics metrics,
    IOptions<HubOptions> options,
    TimeProvider timeProvider,
    ILogger<DeliveryWorker> logger)
{
    public const string ClientName = "disclosure-webhooks";
    public const int MaxAttempts = 8;
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    /// <summary>Sends every delivery due now, one after the other.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var disclosures = options.Value.Disclosures;
        foreach (var delivery in await database.DueDeliveriesAsync(Now(), cancellationToken))
        {
            var webhook = disclosures.Webhooks.FirstOrDefault(candidate => candidate.Name == delivery.Webhook);
            var disclosure = await database.GetDisclosureAsync(delivery.DisclosureId, cancellationToken);
            // A webhook removed from the configuration, an internal report, or a publication
            // withdrawn while its event waited out a backoff, is never sent.
            if (webhook is null || disclosure is null || disclosure.Confidentiality != "public" ||
                (disclosure.Withdrawn && delivery.Event == "published"))
            {
                await database.RecordDeliveryAttemptAsync(
                    delivery.Id, delivery.Attempts, null, DeliveryStates.Failed, Now(), null, cancellationToken);
                continue;
            }

            await DeliverAsync(delivery, webhook, disclosure, disclosures.PublicUrl, cancellationToken);
        }
    }

    private async Task DeliverAsync(
        DeliveryRecord delivery,
        WebhookOptions webhook,
        DisclosureSummary disclosure,
        Uri? publicUrl,
        CancellationToken cancellationToken)
    {
        var link = webhook.IncludeShareLink && delivery.Event == "published" && publicUrl is not null &&
                   !disclosure.Withdrawn
            ? await ApiEndpoints.IssueShareLinkAsync(
                database, disclosure.Id, "webhook:" + webhook.Name, webhook.ShareLinkDays, timeProvider,
                cancellationToken)
            : null;
        var file = disclosure.HasPdf ? "report.pdf" : "report.json";
        var shareUrl = link is null ? null : new Uri(publicUrl!, link.Path + file).ToString();
        var status = await SendAsync(webhook, WebhookDelivery.Body(disclosure, delivery.Event, shareUrl),
            cancellationToken);

        if (status is >= 200 and < 300)
        {
            // Past the stopping token: the receiver has it, and a restart must not send it again.
            await database.RecordDeliveryAttemptAsync(delivery.Id, delivery.Attempts, status, DeliveryStates.Delivered,
                Now(),
                null, CancellationToken.None);
            return;
        }

        // The link went nowhere: it dies with the attempt, and the next one gets its own.
        if (link is not null)
            await database.RevokeShareLinkAsync(disclosure.Id, link.Id, "webhook:" + webhook.Name, Now(),
                cancellationToken);
        var attempts = delivery.Attempts + 1;
        if (attempts >= MaxAttempts)
        {
            // False when an operator's retry reset the row meanwhile: that run of attempts is theirs.
            if (await database.RecordDeliveryAttemptAsync(delivery.Id, delivery.Attempts, status, DeliveryStates.Failed,
                    Now(), null, cancellationToken))
            {
                metrics.DeliveryFailed();
                LogGaveUp(logger, delivery.Id, webhook.Name, attempts);
            }

            return;
        }

        var backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, FirstBackoff.Ticks << (attempts - 1)));
        await database.RecordDeliveryAttemptAsync(delivery.Id, delivery.Attempts, status, DeliveryStates.Pending, Now(),
            Now() + (long)backoff.TotalMilliseconds, cancellationToken);
    }

    // The status, or null when nothing answered. The URL never reaches a log line.
    private async Task<int?> SendAsync(WebhookOptions webhook, byte[] body, CancellationToken cancellationToken)
    {
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(WebhookDelivery.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookDelivery.SignatureHeader, WebhookDelivery.Sign(webhook.Secret!, timestamp, body));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.HttpTimeout);
        try
        {
            using var response = await clients.CreateClient(ClientName).SendAsync(request, timeout.Token);
            return (int)response.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException ||
                                          (exception is OperationCanceledException &&
                                           !cancellationToken.IsCancellationRequested))
        {
            LogUnreachable(logger, webhook.Name);
            return null;
        }
    }

    private long Now()
    {
        return timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    [LoggerMessage(2403, LogLevel.Warning, "Webhook {Webhook} could not be reached; the delivery is retried.")]
    private static partial void LogUnreachable(ILogger logger, string webhook);

    [LoggerMessage(2404, LogLevel.Warning,
        "Delivery {DeliveryId} to webhook {Webhook} gave up after {Attempts} attempts.")]
    private static partial void LogGaveUp(ILogger logger, string deliveryId, string webhook, int attempts);
}

/// <summary>Runs the deliveries every 15 seconds while disclosures are on.</summary>
public sealed partial class DeliveryLoop(
    DeliveryWorker worker,
    IOptions<HubOptions> options,
    TimeProvider timeProvider,
    ILogger<DeliveryLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Disclosures.Enabled)
            return;
        using var timer = new PeriodicTimer(Tick, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                try
                {
                    await worker.RunOnceAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogPassFailed(logger, exception);
                }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: what is due stays in the table for the next start.
        }
    }

    [LoggerMessage(2405, LogLevel.Warning, "A webhook delivery pass failed; the next one retries.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
