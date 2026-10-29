using System.Net.Mail;
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
///     purged meanwhile fails without a request. An e-mail row goes through the relay: a 250 settles it,
///     a 5xx fails it at once, and a 4xx or silence is retried the same way. After the first silence of a
///     pass the other e-mails wait for the next one, so a dead relay costs one timeout, not one per address.
/// </summary>
public sealed partial class RecapDeliveryWorker(
    HubDatabase database,
    IHttpClientFactory clients,
    IOptions<HubOptions> options,
    RecapMailer mailer,
    TimeProvider timeProvider,
    ILogger<RecapDeliveryWorker> logger)
{
    private const string EmailTarget = "an e-mail address";

    /// <summary>Sends every delivery due now, one after the other.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var relaySilent = false;
        foreach (var delivery in await database.DueRecapDeliveriesAsync(Now(), cancellationToken))
            if (delivery.Channel == RecapChannels.Webhook)
                await DeliverAsync(delivery, cancellationToken);
            else if (relaySilent)
                await RetryAsync(delivery, null, EmailTarget, cancellationToken);
            else
                relaySilent = await DeliverEmailAsync(delivery, cancellationToken);
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

        var pilotage = RecapViews.Frozen(Snapshot(report));
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

        await RetryAsync(delivery, status, webhook.Name, cancellationToken);
    }

    // True when the relay did not answer, so the pass stops knocking on it.
    private async Task<bool> DeliverEmailAsync(RecapDeliveryRecord delivery, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var report = await database.GetRecapReportAsync(delivery.ReportId, cancellationToken);
        if (!settings.Recaps.Smtp.Enabled || report is null)
        {
            await database.RecordRecapDeliveryAttemptAsync(
                delivery.Id, null, DeliveryStates.Failed, Now(), null, cancellationToken);
            return false;
        }

        using var message = RecapMail.Build(
            report.Summary, Snapshot(report), delivery.Target, new MailAddress(settings.Recaps.Smtp.From!),
            settings.RecapPublicUrl);
        var status = await mailer.SendAsync(message, cancellationToken);
        if (status == 250)
        {
            // Past the stopping token: the relay has it, and a restart must not send it again.
            await database.RecordRecapDeliveryAttemptAsync(
                delivery.Id, status, DeliveryStates.Delivered, Now(), null, CancellationToken.None);
        }
        else if (status >= 500)
        {
            await database.RecordRecapDeliveryAttemptAsync(
                delivery.Id, status, DeliveryStates.Failed, Now(), null, cancellationToken);
            LogGaveUp(logger, delivery.Id, EmailTarget, delivery.Attempts + 1);
        }
        else
        {
            if (status is null)
                LogRelayUnreachable(logger);
            await RetryAsync(delivery, status, EmailTarget, cancellationToken);
        }

        return status is null;
    }

    // Another attempt after the backoff, or the end of the attempts. The destination is a webhook name or a
    // description: an e-mail address never reaches a log line.
    private async Task RetryAsync(
        RecapDeliveryRecord delivery, int? status, string destination, CancellationToken cancellationToken)
    {
        var attempts = delivery.Attempts + 1;
        if (attempts >= DeliveryWorker.MaxAttempts)
        {
            await database.RecordRecapDeliveryAttemptAsync(
                delivery.Id, status, DeliveryStates.Failed, Now(), null, cancellationToken);
            LogGaveUp(logger, delivery.Id, destination, attempts);
            return;
        }

        await database.RecordRecapDeliveryAttemptAsync(
            delivery.Id, status, DeliveryStates.Pending, Now(),
            Now() + (long)DeliveryWorker.Backoff(attempts).TotalMilliseconds, cancellationToken);
    }

    private static RecapSnapshot Snapshot(RecapReportRecord report)
    {
        return JsonSerializer.Deserialize(report.SnapshotJson, HubJsonContext.Default.RecapSnapshot)!;
    }

    private long Now()
    {
        return timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    [LoggerMessage(2502, LogLevel.Warning, "Recap webhook {Webhook} could not be reached; the delivery is retried.")]
    private static partial void LogUnreachable(ILogger logger, string webhook);

    [LoggerMessage(2503, LogLevel.Warning, "The recap mail relay could not be reached; the e-mails are retried.")]
    private static partial void LogRelayUnreachable(ILogger logger);

    [LoggerMessage(2504, LogLevel.Warning,
        "Recap delivery {DeliveryId} to {Destination} gave up after {Attempts} attempts.")]
    private static partial void LogGaveUp(ILogger logger, string deliveryId, string destination, int attempts);
}
