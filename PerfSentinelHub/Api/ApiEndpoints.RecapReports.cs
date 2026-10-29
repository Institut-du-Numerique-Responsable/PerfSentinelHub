using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private const int RecapReportListLimit = 200;
    private const int MaxRecapBodyBytes = 4 * 1024;

    /// <summary>
    ///     Every report to a recaps.read holder, else the ones the caller generated while findings.read
    ///     is held somewhere. A caller holding neither gets the 403, not an empty list.
    /// </summary>
    private static async Task<IResult> GetRecapReportsAsync(
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        CancellationToken cancellationToken)
    {
        if (!TryRecapReader(request, access, out var creator))
            return Problem(StatusCodes.Status403Forbidden, "You may not read recap reports.");

        var reports = await database.ListRecapReportsAsync(creator, RecapReportListLimit, cancellationToken);
        var deliveries = (await database.ListRecapDeliveriesAsync([.. reports.Select(report => report.Id)],
            cancellationToken)).ToLookup(delivery => delivery.ReportId);
        IReadOnlyList<RecapReportItem> items = [.. reports.Select(report => RecapItem(report, deliveries[report.Id]))];
        return TypedResults.Json(items, HubJsonContext.Default.IReadOnlyListRecapReportItem);
    }

    /// <summary>
    ///     Generates a report on demand, from the copies the caller reads, in the caller's name. It is
    ///     stored beside the scheduled ones, with both views and no delivery.
    /// </summary>
    private static async Task<IResult> CreateRecapReportAsync(
        HttpRequest request,
        HubDatabase database,
        RecapService recaps,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (RecapReportRefusal(request, access, settings, out var identity, out var scope) is { } refused)
            return refused;
        var payload = await ReadBodyAsync(request, cancellationToken, MaxRecapBodyBytes);
        if (payload is null)
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var now = timeProvider.GetUtcNow();
        if (RecapOnDemand.TryParse(payload, DateOnly.FromDateTime(now.UtcDateTime), out var error) is not { } ask)
            return Problem(StatusCodes.Status400BadRequest, error ?? "The request is invalid.");

        var sources = access.Sources(request.HttpContext, settings.Sources);
        if (!RecapSchedule.TryScope(ask.Environment, ask.SourceId, sources, out var sourceIds))
            return Problem(StatusCodes.Status400BadRequest, "Unknown environment or source_id.");

        var snapshot = await recaps.ComputeAsync(
            new RecapRequest(
                ask.Kind, ask.At, sourceIds, ask.Service, ask.Grouping, Narrowing(scope), sources,
                new RecapFilters(ask.Environment, ask.SourceId, ask.Service, ask.Grouping)),
            cancellationToken);
        var period = RecapPeriod.Of(ask.Kind, ask.At);
        var report = new RecapReportRecord(
            new RecapReportSummary(
                Guid.NewGuid().ToString("N"),
                null,
                period.KindName,
                period.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                period.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                [.. RecapViewNames.All],
                snapshot.Period.Incomplete.Count == 0,
                snapshot.NothingMoved,
                identity,
                now.ToUnixTimeMilliseconds()),
            JsonSerializer.Serialize(snapshot, HubJsonContext.Default.RecapSnapshot),
            AccessCaller.Of(request.HttpContext)?.Subject);
        // No subscription, so the unique period index does not apply: only an id collision could refuse.
        if (!await database.InsertRecapReportAsync(report, [], cancellationToken))
            throw new InvalidOperationException("The Hub could not store the recap report.");

        return TypedResults.Json(
            RecapItem(report.Summary, []), HubJsonContext.Default.RecapReportItem,
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> GetRecapReportAsync(
        string id,
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        CancellationToken cancellationToken)
    {
        if (await VisibleReportAsync(id, request, database, access, cancellationToken) is not { } report)
            return TypedResults.NotFound();

        var deliveries = await database.ListRecapDeliveriesAsync([id], cancellationToken);
        return TypedResults.Json(
            new RecapReportDetail(RecapItem(report.Summary, deliveries), JsonRead.Literal(report.SnapshotJson)),
            HubJsonContext.Default.RecapReportDetail);
    }

    /// <summary>
    ///     The frozen page of one view, which is the link a webhook or a mail carries. Outside /api so a
    ///     browser is sent to sign in; the page runs only the two scripts its policy hashes.
    /// </summary>
    private static async Task<IResult> GetRecapReportHtmlAsync(
        string id,
        string view,
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        CancellationToken cancellationToken)
    {
        if (await VisibleReportAsync(id, context.Request, database, access, cancellationToken) is not { } report ||
            !report.Summary.Views.Contains(view))
            return TypedResults.NotFound();

        var snapshot = JsonSerializer.Deserialize(report.SnapshotJson, HubJsonContext.Default.RecapSnapshot)!;
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = RecapReportRenderer.ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = "no-store";
        headers.ContentDisposition = $"inline; filename=\"perf-sentinel-recap-{report.Summary.PeriodFrom}-{view}.html\"";
        return TypedResults.Bytes(
            RecapReportRenderer.Render(view == RecapViewNames.Pilotage ? RecapViews.Pilotage(snapshot) : snapshot),
            "text/html; charset=utf-8");
    }

    // Every refusal of a generate, in the order a caller learns them: who, from where, then what it may read.
    private static IResult? RecapReportRefusal(
        HttpRequest request,
        AccessPolicy access,
        HubOptions options,
        out string identity,
        out AccessScope reads)
    {
        identity = null!;
        reads = AccessScope.Nothing;
        if (RelayIdentity(request, options) is not { } caller)
            return Problem(
                StatusCodes.Status403Forbidden,
                "The Hub does not know who is asking, and a report is generated in somebody's name.");
        if (RefuseForeignRequest(request) is { } refusal)
            return refusal;
        reads = access.Scope(request.HttpContext, AccessPermissions.FindingsRead);
        if (reads.IsNothing)
            return NoFindingsRead();
        identity = caller;
        return null;
    }

    // The one read rule. recaps.read reads every report (creator null); findings.read alone reads the
    // ones the caller's subject generated, and a caller with no subject reads none: a scheduled report
    // has no creator, and null must not match null. The display name never decides: two accounts can
    // share one, and an address can be reassigned.
    private static bool TryRecapReader(HttpRequest request, AccessPolicy access, out string? creator)
    {
        creator = null;
        if (!access.Scope(request.HttpContext, AccessPermissions.RecapsRead).IsNothing)
            return true;
        creator = AccessCaller.Of(request.HttpContext)?.Subject;
        return creator is not null && !access.Scope(request.HttpContext, AccessPermissions.FindingsRead).IsNothing;
    }

    // A report the caller may not read answers as an unknown one does.
    private static async Task<RecapReportRecord?> VisibleReportAsync(
        string id,
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        CancellationToken cancellationToken)
    {
        if (!IsRecapReportId(id) || !TryRecapReader(request, access, out var creator))
            return null;
        var report = await database.GetRecapReportAsync(id, cancellationToken);
        return report is not null && (creator is null || report.CreatorSubject == creator) ? report : null;
    }

    // 32 lowercase hex, the shape the Hub gives every report.
    private static bool IsRecapReportId(string id)
    {
        return id.Length == 32 && id.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    }

    private static RecapReportItem RecapItem(RecapReportSummary report, IEnumerable<RecapDeliveryRecord> deliveries)
    {
        return new RecapReportItem(
            report.Id,
            report.Subscription,
            new RecapPeriodRef(report.PeriodKind, report.PeriodFrom, report.PeriodTo),
            report.Views,
            report.Complete,
            report.NothingMoved,
            report.CreatedBy,
            report.GeneratedAtMs,
            [
                .. deliveries.Select(delivery => new RecapDeliveryView(
                    delivery.Channel, delivery.Target, delivery.State, delivery.Attempts, delivery.LastAttemptMs,
                    delivery.NextAttemptMs, delivery.LastStatus))
            ]);
    }
}
