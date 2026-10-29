using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Recaps;

/// <summary>
///     Freezes, for each subscription and cadence, the report of the last closed period once its
///     ledgers have sealed the last day or the max delay has passed. One report per subscription,
///     cadence and period: the table refuses a second one, so a restart or a second tick adds
///     nothing. A period missed while the Hub was down is not made up for.
/// </summary>
public sealed partial class RecapGenerator(
    HubDatabase database,
    RecapService recaps,
    IOptions<HubOptions> options,
    TimeProvider timeProvider,
    ILogger<RecapGenerator> logger)
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in options.Value.Recaps.Subscriptions)
        {
            foreach (var cadence in subscription.Cadences)
                try
                {
                    await GenerateAsync(subscription, cadence, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogFailed(logger, subscription.Name, cadence, exception);
                }
        }
    }

    private async Task GenerateAsync(
        RecapSubscriptionOptions subscription,
        string cadence,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        if (!RecapPeriod.TryParseKind(cadence, out var kind) ||
            !RecapSchedule.TryScope(subscription.Environment, subscription.SourceId, settings.Sources, out var sourceIds))
            throw new InvalidOperationException("The subscription names a cadence or a scope the Hub does not know.");

        var period = RecapPeriod.Of(kind, DateOnly.FromDateTime(now.UtcDateTime)).Previous();
        var from = period.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (await database.RecapReportExistsAsync(subscription.Name, period.KindName, from, cancellationToken))
            return;

        var coverage = await database.ReadRecapCoverageAsync(
            sourceIds ?? [.. settings.Sources.Select(source => source.Id)], period.FromDay, period.ToDay,
            cancellationToken);
        if (!RecapSchedule.IsDue(period, coverage, now.ToUnixTimeMilliseconds(), settings.Recaps.MaxDelay))
            return;

        var snapshot = await recaps.ComputeAsync(
            new RecapRequest(
                kind, period.From, sourceIds, subscription.Service, subscription.Grouping, null, settings.Sources,
                new RecapFilters(
                    subscription.Environment, subscription.SourceId, subscription.Service, subscription.Grouping)),
            cancellationToken);
        var report = new RecapReportRecord(
            new RecapReportSummary(
                Guid.NewGuid().ToString("N"),
                subscription.Name,
                period.KindName,
                from,
                period.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                [.. RecapViewNames.All.Where(subscription.Views.Contains)],
                snapshot.Period.Incomplete.Count == 0,
                snapshot.NothingMoved,
                null,
                now.ToUnixTimeMilliseconds()),
            JsonSerializer.Serialize(snapshot, HubJsonContext.Default.RecapSnapshot));

        if (await database.InsertRecapReportAsync(report, DeliveriesOf(subscription, snapshot), cancellationToken))
            LogGenerated(logger, subscription.Name, cadence, from, report.Summary.Complete, report.Summary.NothingMoved);
    }

    // A report that moved nothing is archived and sent nowhere.
    private static List<(string Channel, string Target)> DeliveriesOf(
        RecapSubscriptionOptions subscription,
        RecapSnapshot snapshot)
    {
        if (snapshot.NothingMoved)
            return [];
        return
        [
            .. subscription.Webhooks.Distinct(StringComparer.Ordinal).Select(name => (RecapChannels.Webhook, name)),
            .. subscription.Emails.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(address => (RecapChannels.Email, address))
        ];
    }

    [LoggerMessage(2501, LogLevel.Information,
        "Recap {Subscription} ({Cadence}) from {From} frozen: complete {Complete}, nothing moved {NothingMoved}.")]
    private static partial void LogGenerated(
        ILogger logger, string subscription, string cadence, string from, bool complete, bool nothingMoved);

    [LoggerMessage(2506, LogLevel.Warning, "Recap {Subscription} ({Cadence}) could not be generated; the next pass retries.")]
    private static partial void LogFailed(ILogger logger, string subscription, string cadence, Exception exception);
}
