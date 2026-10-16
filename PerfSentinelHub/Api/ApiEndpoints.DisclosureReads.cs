using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Disclosures;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private const int MaxWithdrawReasonChars = 500;
    private const long DayMs = 86_400_000;

    private static async Task<IResult> GetDisclosuresAsync(
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
    {
        if (DisclosureRefusal(context, access, options.Value, AccessPermissions.DisclosuresRead) is { } refused)
            return refused;
        IReadOnlyList<DisclosureItem> items =
            [.. (await database.ListDisclosuresAsync(cancellationToken)).Select(Item)];
        return TypedResults.Json(items, HubJsonContext.Default.IReadOnlyListDisclosureItem);
    }

    private static async Task<IResult> GetDisclosureAsync(
        string id,
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (DisclosureRefusal(context, access, options.Value, AccessPermissions.DisclosuresRead) is { } refused)
            return refused;
        if (await database.GetDisclosureAsync(id, cancellationToken) is not { } summary)
            return TypedResults.NotFound();
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var disclosures = options.Value.Disclosures;
        return TypedResults.Json(
            new DisclosureDetail(
                Item(summary),
                new DisclosureChecks(
                    summary.HashChecked == true ? "checked" : "not_checked",
                    summary.HasAttestation ? summary.BindingChecked == true ? "checked" : "not_checked" : "no_attestation",
                    // What the report says of itself: signed, signed-with-attestation or audited.
                    summary.IntegrityLevel is "signed" or "signed-with-attestation" or "audited" ? "declared" : "absent",
                    summary.EngineVersion),
                [.. (await database.ListShareLinksAsync(id, cancellationToken)).Select(link => LinkView(link, nowMs))],
                [
                    .. (await database.ListDeliveriesAsync(id, cancellationToken)).Select(delivery => new DeliveryView(
                        delivery.Id, delivery.Webhook, delivery.Event, delivery.State, delivery.Attempts,
                        delivery.LastAttemptMs, delivery.NextAttemptMs, delivery.LastStatus))
                ],
                disclosures.ExpectedIdentity,
                disclosures.ExpectedIssuer),
            HubJsonContext.Default.DisclosureDetail);
    }

    private static async Task<IResult> GetDisclosureFileAsync(
        string id,
        string name,
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
    {
        if (DisclosureRefusal(context, access, options.Value, AccessPermissions.DisclosuresRead) is { } refused)
            return refused;
        return await DisclosureFileAsync(database, id, name, context.Response, cancellationToken);
    }

    // Shared with the share route: the same five names, served as attachments the browser never sniffs.
    private static async Task<IResult> DisclosureFileAsync(
        HubDatabase database,
        string id,
        string name,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (!DisclosureDownloads.IsServed(name) ||
            await DisclosureDownloads.ReadAsync(database, id, name, cancellationToken) is not var (bytes, contentType))
            return TypedResults.NotFound();
        response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.File(bytes, contentType, name);
    }

    private static async Task<IResult> CreateShareLinkAsync(
        string id,
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (ManageRefusal(request, access, options.Value) is { } refused)
            return refused;
        if (await database.GetDisclosureAsync(id, cancellationToken) is not { } summary)
            return TypedResults.NotFound();
        // Only public leaves the Hub, whatever a report stored before the envelope checked its level declares.
        if (summary.Confidentiality != "public")
            return TypedResults.Json(new DisclosureRefusal("internal_not_shareable"),
                HubJsonContext.Default.DisclosureRefusal, statusCode: StatusCodes.Status422UnprocessableEntity);
        if (summary.Withdrawn)
            return Problem(StatusCodes.Status409Conflict, "A withdrawn disclosure is not shared.");
        var body = await ReadBodyAsync(request, cancellationToken, 1024);
        if (ShareDays(body, options.Value.Disclosures.ShareLinkDays) is not { } days)
            return Problem(StatusCodes.Status400BadRequest, "days must be a whole number of days from 1 to 30.");

        var created = await IssueShareLinkAsync(
            database, id, RelayIdentity(request, options.Value)!, days, timeProvider, cancellationToken);
        return TypedResults.Json(created, HubJsonContext.Default.ShareLinkCreated, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>A link of its own token, shown once and stored as its SHA-256. Shared with the webhooks.</summary>
    internal static async Task<ShareLinkCreated> IssueShareLinkAsync(
        HubDatabase database,
        string disclosureId,
        string createdBy,
        int days,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var token = "pshare_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var linkId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var expiresAtMs = nowMs + (days * DayMs);
        await database.InsertShareLinkAsync(
            new ShareLinkRecord(linkId, disclosureId, SHA256.HashData(Encoding.UTF8.GetBytes(token)), createdBy, nowMs,
                expiresAtMs),
            cancellationToken);
        return new ShareLinkCreated(linkId, token, $"/share/{token}/", expiresAtMs);
    }

    private static async Task<IResult> RevokeShareLinkAsync(
        string id,
        string linkId,
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (ManageRefusal(request, access, options.Value) is { } refused)
            return refused;
        return await database.RevokeShareLinkAsync(
            id, linkId, RelayIdentity(request, options.Value)!, timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    /// <summary>
    ///     Final: the files stay, the links die, and a withdrawn event goes to each webhook that
    ///     was told of the publication. A wrong disclosure is replaced by a new one.
    /// </summary>
    private static async Task<IResult> WithdrawDisclosureAsync(
        string id,
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (ManageRefusal(request, access, options.Value) is { } refused)
            return refused;
        if (WithdrawReason(await ReadBodyAsync(request, cancellationToken, 8 * 1024)) is not { } reason)
            return Problem(StatusCodes.Status400BadRequest,
                $"reason is required, {MaxWithdrawReasonChars} characters at most and no control character.");
        if (await database.GetDisclosureAsync(id, cancellationToken) is null)
            return TypedResults.NotFound();
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (!await database.WithdrawAsync(id, reason, RelayIdentity(request, options.Value)!, nowMs, cancellationToken))
            return Problem(StatusCodes.Status409Conflict, "This disclosure is already withdrawn.");

        var told = (await database.ListDeliveriesAsync(id, cancellationToken))
            .Where(delivery => delivery.Event == "published")
            .Select(delivery => delivery.Webhook)
            .ToHashSet(StringComparer.Ordinal);
        await database.EnqueueDeliveriesAsync(
            id, "withdrawn",
            [.. options.Value.Disclosures.Webhooks.Select(webhook => webhook.Name).Where(told.Contains)],
            nowMs, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RetryDeliveryAsync(
        string id,
        string deliveryId,
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (ManageRefusal(request, access, options.Value) is { } refused)
            return refused;
        var delivery = (await database.ListDeliveriesAsync(id, cancellationToken))
            .FirstOrDefault(candidate => candidate.Id == deliveryId);
        if (delivery is null)
            return TypedResults.NotFound();
        if (delivery.State == DeliveryStates.Delivered)
            return Problem(StatusCodes.Status409Conflict, "This delivery already went through.");
        await database.RetryDeliveryAsync(deliveryId, timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Runs the engine again on the stored files, for a disclosure stored before it could render.</summary>
    // Every parameter is the route's own or a collaborator the router injects.
#pragma warning disable S107
    private static async Task<IResult> RenderDisclosureAsync(
        string id,
        HttpRequest request,
        HubDatabase database,
        DisclosureRenderer renderer,
        EngineProbe engine,
        AccessPolicy access,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
#pragma warning restore S107
    {
        if (ManageRefusal(request, access, options.Value) is { } refused)
            return refused;
        if (await database.ReadFileAsync(id, DisclosureFile.Report, cancellationToken) is not { } report)
            return TypedResults.NotFound();
        var files = new DisclosureFiles(
            report,
            await database.ReadFileAsync(id, DisclosureFile.Attestation, cancellationToken),
            await database.ReadFileAsync(id, DisclosureFile.Bundle, cancellationToken));
        var outcome = await renderer.RenderAsync(files, cancellationToken);
        switch (outcome)
        {
            case RenderOutcome.Refused or RenderOutcome.Malformed:
                return TypedResults.Json(
                    new DisclosureRefusal(outcome is RenderOutcome.Refused r ? r.Reason : ((RenderOutcome.Malformed)outcome).Reason),
                    HubJsonContext.Default.DisclosureRefusal, statusCode: StatusCodes.Status422UnprocessableEntity);
            case RenderOutcome.Rendered rendered:
                await database.SetPdfAsync(id, rendered.Pdf, null, engine.Version, true,
                    files.Attestation is null ? null : true, cancellationToken);
                break;
            default:
                await database.SetPdfAsync(id, null,
                    outcome is RenderOutcome.Unrenderable u ? u.Reason : UnsupportedEngine, engine.Version, null, null,
                    cancellationToken);
                break;
        }

        return Accepted((await database.GetDisclosureAsync(id, cancellationToken))!, StatusCodes.Status200OK);
    }

    // Disclosures off is a 404, so an off Hub tells nothing; then the Hub-level permission.
    private static IResult? DisclosureRefusal(HttpContext context, AccessPolicy access, HubOptions options, string permission)
    {
        if (!options.Disclosures.Enabled)
            return TypedResults.NotFound();
        return access.Scope(context, permission).IsNothing
            ? Problem(StatusCodes.Status403Forbidden,
                permission == AccessPermissions.DisclosuresRead
                    ? "You may not read disclosures."
                    : "You may not manage disclosures.")
            : null;
    }

    // A write names its author, and is held to the same-origin rule the ack relay follows.
    private static IResult? ManageRefusal(HttpRequest request, AccessPolicy access, HubOptions options)
    {
        if (DisclosureRefusal(request.HttpContext, access, options, AccessPermissions.DisclosuresManage) is { } refused)
            return refused;
        if (RelayIdentity(request, options) is null)
            return Problem(StatusCodes.Status403Forbidden,
                "The Hub does not know who is asking, and a disclosure action is taken in somebody's name.");
        return RefuseForeignRequest(request);
    }

    private static DisclosureItem Item(DisclosureSummary summary)
    {
        JsonElement? figures = null;
        if (summary.FiguresJson is { } json)
        {
            using var document = JsonDocument.Parse(json);
            figures = document.RootElement.Clone();
        }

        return new DisclosureItem(
            summary.Id, summary.ReportUuid, summary.Organisation,
            new DisclosurePeriod(summary.PeriodType, summary.PeriodFrom, summary.PeriodTo), summary.Intent,
            summary.Confidentiality, summary.ContentHash, summary.IntegrityLevel, figures,
            new DisclosureFilesPresent(true, summary.HasAttestation, summary.HasBundle, summary.HasPdf),
            summary.HasPdf ? "rendered" : "not_checked", summary.RenderReason, summary.PublishedAtMs,
            summary.SignatureAddedAtMs,
            summary.WithdrawnAtMs is { } at ? new DisclosureWithdrawal(at, summary.WithdrawnReason, summary.WithdrawnBy) : null);
    }

    private static ShareLinkView LinkView(ShareLinkSummary link, long nowMs)
    {
        var state = link.RevokedAtMs is not null ? "revoked" : link.ExpiresAtMs <= nowMs ? "expired" : "active";
        return new ShareLinkView(link.Id, state, link.CreatedBy, link.CreatedAtMs, link.ExpiresAtMs, link.RevokedAtMs,
            link.RevokedBy, link.Downloads, link.LastUsedAtMs);
    }

    // An empty body takes the configured default; otherwise {"days": n}, 1 to 30.
    private static int? ShareDays(byte[]? body, int defaultDays)
    {
        if (body is null)
            return null;
        if (body.Length == 0)
            return defaultDays;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!document.RootElement.TryGetProperty("days", out var days))
                return defaultDays;
            return days.ValueKind == JsonValueKind.Number && days.TryGetInt32(out var value) && value is >= 1 and <= 30
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? WithdrawReason(byte[]? body)
    {
        if (body is not { Length: > 0 })
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("reason", out var reason) &&
                   reason.ValueKind == JsonValueKind.String &&
                   reason.GetString() is { Length: > 0 and <= MaxWithdrawReasonChars } text &&
                   !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl)
                ? text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
