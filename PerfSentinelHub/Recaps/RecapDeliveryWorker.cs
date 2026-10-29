using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Disclosures;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Recaps;

/// <summary>
///     Sends the webhook deliveries of the recap_deliveries rows, so a restart resumes what was due.
///     A 2xx settles one; anything else, a redirect included, is retried with the disclosures' backoff
///     and given up after the same number of attempts. A webhook no longer configured or a report
///     purged meanwhile fails without a request. An e-mail row is not this worker's.
/// </summary>
public sealed partial class RecapDeliveryWorker(
    HubDatabase database,
    IHttpClientFactory clients,
    IOptions<HubOptions> options,
    TimeProvider timeProvider,
    ILogger<RecapDeliveryWorker> logger)
{
    /// <summary>Sends every webhook delivery due now, one after the other.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var delivery in await database.DueRecapDeliveriesAsync(Now(), cancellationToken))
            if (delivery.Channel == RecapChannels.Webhook)
                await DeliverAsync(delivery, cancellationToken);
    }

    private async Task DeliverAsync(RecapDeliveryRecord delivery, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var webhook = settings.Recaps.Webhooks.FirstOrDefault(candidate => candidate.Name == delivery.Target);
        var report = await database.GetRecapReportAsync(delivery.ReportId, cancellationToken);
        if (webhook is null || report is null)
        {
            await database.RecordRecapDeliveryAttemptAsync(
                delivery.Id, null, DeliveryStates.Failed, Now(), null, cancellationToken);
            return;
        }

        var pilotage = RecapViews.Pilotage(
            JsonSerializer.Deserialize(report.SnapshotJson, HubJsonContext.Default.RecapSnapshot)!);
        var status = await WebhookDelivery.PostAsync(
            clients.CreateClient(DeliveryWorker.ClientName), webhook.Url!, webhook.Secret!,
            RecapWebhook.Body(report.Summary, pilotage, settings.RecapPublicUrl), timeProvider,
            settings.HttpTimeout, cancellationToken);
        if (status is null)
            LogUnreachable(logger, webhook.Name);

        if (status is >= 200 and < 300)
        {
            // Past the stopping token: the receiver has it, and a restart must not send it again.
            await database.RecordRecapDeliveryAttemptAsync(
                delivery.Id, status, DeliveryStates.Delivered, Now(), null, CancellationToken.None);
            return;
        }

        var attempts = delivery.Attempts + 1;
        if (attempts >= DeliveryWorker.MaxAttempts)
        {
            await database.RecordRecapDeliveryAttemptAsync(
                delivery.Id, status, DeliveryStates.Failed, Now(), null, cancellationToken);
            LogGaveUp(logger, delivery.Id, webhook.Name, attempts);
            return;
        }

        await database.RecordRecapDeliveryAttemptAsync(
            delivery.Id, status, DeliveryStates.Pending, Now(),
            Now() + (long)DeliveryWorker.Backoff(attempts).TotalMilliseconds, cancellationToken);
    }

    private long Now()
    {
        return timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    [LoggerMessage(2502, LogLevel.Warning, "Recap webhook {Webhook} could not be reached; the delivery is retried.")]
    private static partial void LogUnreachable(ILogger logger, string webhook);

    [LoggerMessage(2504, LogLevel.Warning,
        "Recap delivery {DeliveryId} to webhook {Webhook} gave up after {Attempts} attempts.")]
    private static partial void LogGaveUp(ILogger logger, string deliveryId, string webhook, int attempts);
}
