using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Cards;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private const int MaxCardRevokeBodyBytes = 1024;
    private const int MaxCardBodyBytes = 4096;
    private const string NoCardEngine = "This Hub's engine cannot make cards yet.";
    private const string CardNotRendered = "The engine could not render this card.";

    private const string UndrawableField =
        "The card's font cannot draw a character of this field. Remove it, or write it another way.";

    private const string UndrawableOrganisation =
        "The card's font cannot draw a character of the organisation's name. Ask an administrator to change " +
        "Hub:Cards:Organisation.";

    /// <summary>What the screen shows before anyone signs: the period's refusal, the signed scope, the fixes.</summary>
    private static async Task<IResult> GetCardOptionsAsync(
        HttpRequest request,
        CardService cards,
        EngineProbe engine,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (CardRefusal(request, access, settings, engine, false, out _, out var reads, out var writes) is { } refused)
            return refused;
        if (!HasValidUtf8(request.QueryString.Value) ||
            !CardRequest.TryParseOptionsQuery(request, Today(timeProvider), out var query))
            return Problem(StatusCodes.Status400BadRequest, "The card options query is invalid.");
        if (CardScopeRefusal(request.HttpContext, access, settings, reads, writes, query, out var scope) is { } outside)
            return outside;

        return TypedResults.Json(await cards.OptionsAsync(scope, cancellationToken),
            HubJsonContext.Default.CardOptions);
    }

    /// <summary>The card as a file to download: the PNG, the SVG, the JPEG XL, or the PDF carousel.</summary>
    // Every parameter is a collaborator the router injects.
#pragma warning disable S107
    private static async Task<IResult> RenderCardAsync(
        HttpRequest request,
        CardService cards,
        CardKeys keys,
        CardRenderer renderer,
        EngineProbe engine,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
#pragma warning restore S107
    {
        var call = new CardCall(request, cards, keys, renderer, engine, access, options.Value, timeProvider);
        var (refused, ask, bytes) = await MakeCardAsync(call, false, cancellationToken);
        if (refused is not null)
            return refused;

        var headers = request.HttpContext.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = "no-store";
        // Set first, so the access policy's TryAdd keeps it: an SVG opened from its URL runs and fetches nothing.
        if (ask!.Format == "svg")
            headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        var period = RecapPeriod.Of(ask.Kind, ask.At);
        var type = ask.Format switch
        {
            "pdf" => "application/pdf",
            "svg" => "image/svg+xml",
            "jxl" => "image/jxl",
            _ => "image/png"
        };
        return TypedResults.File(bytes!, type,
            $"perf-sentinel-card-{period.KindName}-{period.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.{ask.Format}");
    }

    /// <summary>
    ///     Renders the card as a PNG and has the engine check it online: the TXT record through the DoH
    ///     service, then the domain's .well-known file. The only route that reaches the network.
    /// </summary>
    // Every parameter is a collaborator the router injects.
#pragma warning disable S107
    private static async Task<IResult> CheckCardAttachmentAsync(
        HttpRequest request,
        CardService cards,
        CardKeys keys,
        CardRenderer renderer,
        EngineProbe engine,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
#pragma warning restore S107
    {
        var call = new CardCall(request, cards, keys, renderer, engine, access, options.Value, timeProvider);
        var (refused, _, png) = await MakeCardAsync(call, true, cancellationToken);
        if (refused is not null)
            return refused;

        return await renderer.VerifyAsync(png!, cancellationToken) is { } verdict
            ? TypedResults.Json(verdict with { Contacted = [CardRenderer.DohHost, options.Value.Cards.Domain!] },
                HubJsonContext.Default.CardAttachmentResponse)
            : Problem(StatusCodes.Status502BadGateway, "The engine could not check this card's attachment.");
    }

    // The guard, the body, the scope, the figures, the key and the render, in that order; the
    // attachment check always renders a PNG and needs the domain it checks.
    private static async Task<(IResult? Refused, CardRequest? Ask, byte[]? Bytes)> MakeCardAsync(
        CardCall call,
        bool attachment,
        CancellationToken cancellationToken)
    {
        var request = call.Request;
        if (CardRefusal(request, call.Access, call.Options, call.Engine, true, out _, out var reads, out var writes) is
            { } refused)
            return (refused, null, null);
        if (attachment && call.Options.Cards.Domain is null)
            return (Problem(StatusCodes.Status409Conflict, "Set Hub:Cards:Domain to check an attachment."), null, null);
        var payload = await ReadBodyAsync(request, cancellationToken, MaxCardBodyBytes);
        if (payload is null)
            return (TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge), null, null);
        if (CardRequest.TryParse(payload, Today(call.Clock), out var error) is not { } ask)
            return (Problem(StatusCodes.Status400BadRequest, error ?? "The request is invalid."), null, null);
        if (CardScopeRefusal(request.HttpContext, call.Access, call.Options, reads, writes,
                (ask.Kind, ask.At, ask.Environment, ask.SourceId), out var scope) is { } outside)
            return (outside, null, null);

        var outcome = await call.Cards.ComputeAsync(
            new CardAsk(scope, ask.Angle, ask.Lang, ask.Signature, ask.Free), cancellationToken);
        if (outcome is CardOutcome.Refused { Reason: var reason })
            return (Problem(StatusCodes.Status422UnprocessableEntity,
                CardRules.Explain(reason, RecapPeriod.Of(ask.Kind, ask.At))), null, null);

        StoredCardKey key;
        try
        {
            key = await call.Keys.CurrentAsync(cancellationToken);
        }
        catch (CardEngineException exception)
        {
            return (CardEngineProblem(exception), null, null);
        }

        var render = await call.Renderer.RenderAsync(((CardOutcome.Ready)outcome).Input, key, call.Keys.KeyFile(key),
            attachment ? "png" : ask.Format, cancellationToken);
        return render is CardRender.Rendered rendered ? (null, ask, rendered.Bytes) : (CardRenderProblem(render), null, null);
    }

    // The engine names the card.json path of the text it cannot draw; the screen shows the error under
    // that field, or asks an administrator when it is the organisation's name.
    private static IResult CardRenderProblem(CardRender render)
    {
        return render switch
        {
            CardRender.Refused { Reason: "unsupported_characters", Field: "free.title" or "free.team" or "free.label" }
                refused => TypedResults.Json(new CardFieldProblem(UndrawableField, refused.Field["free.".Length..]),
                    HubJsonContext.Default.CardFieldProblem, statusCode: StatusCodes.Status422UnprocessableEntity),
            CardRender.Refused { Reason: "unsupported_characters" } =>
                Problem(StatusCodes.Status422UnprocessableEntity, UndrawableOrganisation),
            CardRender.Refused { Reason: "compact_too_large" } => Problem(StatusCodes.Status422UnprocessableEntity,
                "The card's QR code cannot hold this organisation's domain. Ask an administrator for a shorter " +
                "Hub:Cards:Domain."),
            CardRender.Refused { Reason: "unsupported_language" } => Problem(StatusCodes.Status422UnprocessableEntity,
                "This Hub's engine cannot print cards in this language yet."),
            _ => Problem(StatusCodes.Status502BadGateway, CardNotRendered)
        };
    }

    // Cards off is a 404, so an off Hub tells nothing; a card is made in somebody's name, a POST held to
    // the same-origin rule, by a holder of cards.write who reads findings, on an engine that has card.
    // Every out is a piece of the caller the routes need next.
#pragma warning disable S107
    private static IResult? CardRefusal(
        HttpRequest request,
        AccessPolicy access,
        HubOptions options,
        EngineProbe engine,
        bool write,
        out string identity,
        out AccessScope reads,
        out AccessScope writes)
#pragma warning restore S107
    {
        identity = null!;
        reads = AccessScope.Nothing;
        writes = AccessScope.Nothing;
        if (!options.Cards.Enabled)
            return TypedResults.NotFound();
        if (RelayIdentity(request, options) is not { } caller)
            return Problem(StatusCodes.Status403Forbidden,
                "The Hub does not know who is asking, and a card is made in somebody's name.");
        if (write && RefuseForeignRequest(request) is { } foreign)
            return foreign;
        writes = access.Scope(request.HttpContext, AccessPermissions.CardsWrite);
        if (writes.IsNothing)
            return Problem(StatusCodes.Status403Forbidden, "You may not create cards.");
        reads = access.Scope(request.HttpContext, AccessPermissions.FindingsRead);
        if (reads.IsNothing)
            return NoFindingsRead();
        if (!engine.SupportsCard)
            return Problem(StatusCodes.Status503ServiceUnavailable,
                "This Hub's engine cannot render cards: it needs a perf-sentinel release with the card subcommand.");
        identity = caller;
        return null;
    }

    // The candidate sources are those both scopes reach; the curve signs only those both hold whole.
    private static IResult? CardScopeRefusal(
        HttpContext context,
        AccessPolicy access,
        HubOptions options,
        AccessScope reads,
        AccessScope writes,
        (RecapPeriodKind Kind, DateOnly At, string? Environment, string? SourceId) query,
        out CardScopeAsk scope)
    {
        scope = null!;
        var sources = access.Sources(context, options.Sources);
        if (!RecapSchedule.TryScope(query.Environment, query.SourceId, sources, out var ids))
            return Problem(StatusCodes.Status400BadRequest, "Unknown environment or source_id.");
        List<string> candidates =
        [
            .. (ids ?? [.. sources.Select(source => source.Id)])
            .Where(id => reads.ReachesSource(id) && writes.ReachesSource(id))
        ];
        if (candidates.Count == 0)
            return Problem(StatusCodes.Status403Forbidden, "You may not create cards for these sources.");

        scope = new CardScopeAsk(query.Kind, query.At, candidates,
            [.. candidates.Where(id => reads.HoldsAtSource(id) && writes.HoldsAtSource(id))], query.Environment,
            Narrowing(reads), sources);
        return null;
    }

    private static DateOnly Today(TimeProvider timeProvider)
    {
        return DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>The collaborators one card route hands to the steps it shares with the other.</summary>
    private sealed record CardCall(
        HttpRequest Request,
        CardService Cards,
        CardKeys Keys,
        CardRenderer Renderer,
        EngineProbe Engine,
        AccessPolicy Access,
        HubOptions Options,
        TimeProvider Clock);

    /// <summary>Every signing key and the lines to publish. Never makes a key: the first card does.</summary>
    private static async Task<IResult> GetCardKeysAsync(
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
    {
        if (CardKeyRefusal(request, access, options.Value, false) is { } refused)
            return refused;
        return await CardKeysAsync(database, options.Value, cancellationToken);
    }

    /// <summary>Retires the current key, deletes its private file and has the engine make a new one.</summary>
    private static async Task<IResult> RotateCardKeyAsync(
        HttpRequest request,
        HubDatabase database,
        CardKeys keys,
        EngineProbe engine,
        AccessPolicy access,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
    {
        if (CardKeyRefusal(request, access, options.Value, true) is { } refused)
            return refused;
        if (!engine.SupportsCard)
            return Problem(StatusCodes.Status503ServiceUnavailable, NoCardEngine);
        try
        {
            await keys.RotateAsync(cancellationToken);
        }
        catch (CardEngineException exception)
        {
            return CardEngineProblem(exception);
        }

        return await CardKeysAsync(database, options.Value, cancellationToken);
    }

    /// <summary>
    ///     Revokes a key from the first day it may have leaked. The date has no default: one too late
    ///     would leave cards forged before it valid.
    /// </summary>
    private static async Task<IResult> RevokeCardKeyAsync(
        HttpRequest request,
        HubDatabase database,
        CardKeys keys,
        AccessPolicy access,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
    {
        if (CardKeyRefusal(request, access, options.Value, true) is { } refused)
            return refused;
        var payload = await ReadBodyAsync(request, cancellationToken, MaxCardRevokeBodyBytes);
        if (payload is null)
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (ReadRevoke(payload, out var fingerprint, out var on) is { } invalid)
            return Problem(StatusCodes.Status400BadRequest, invalid);

        return await keys.RevokeAsync(fingerprint, on, cancellationToken) switch
        {
            CardRevokeOutcome.Unknown => Problem(StatusCodes.Status404NotFound,
                "The Hub holds no card key with this fingerprint."),
            CardRevokeOutcome.BadDate => Problem(StatusCodes.Status400BadRequest,
                "The revocation date must lie between the key's creation and today."),
            _ => await CardKeysAsync(database, options.Value, cancellationToken)
        };
    }

    // Cards off is a 404, so an off Hub tells nothing; a key is managed in somebody's name, a write
    // held to the same-origin rule, and only by an administrator.
    private static IResult? CardKeyRefusal(HttpRequest request, AccessPolicy access, HubOptions options, bool write)
    {
        if (!options.Cards.Enabled)
            return TypedResults.NotFound();
        if (RelayIdentity(request, options) is null)
            return Problem(StatusCodes.Status403Forbidden,
                "The Hub does not know who is asking, and a card key is managed in somebody's name.");
        if (write && RefuseForeignRequest(request) is { } foreign)
            return foreign;
        return access.Scope(request.HttpContext, AccessPermissions.AccessAdmin).IsNothing
            ? Problem(StatusCodes.Status403Forbidden, "You may not manage the card signing key.")
            : null;
    }

    private static IResult CardEngineProblem(CardEngineException exception)
    {
        return exception.Reason == "unsupported"
            ? Problem(StatusCodes.Status503ServiceUnavailable, NoCardEngine)
            : Problem(StatusCodes.Status502BadGateway, $"The engine did not make a signing key ({exception.Reason}).");
    }

    private static async Task<IResult> CardKeysAsync(HubDatabase database, HubOptions options,
        CancellationToken cancellationToken)
    {
        var keys = await database.ReadCardKeysAsync(cancellationToken);
        var items = keys.Select(key => new CardKeyItem(
            key.Fingerprint,
            key.PublicKey,
            CardKeyLines.Day(key.CreatedAtMs),
            key.RetiredAtMs is { } to ? CardKeyLines.Day(to) : null,
            key.RevokedOn,
            key.RetiredAtMs is null,
            CardKeyLines.Dns(key))).ToList();
        return TypedResults.Json(
            new CardKeysResponse(CardKeyLines.RecordName(options.Cards.Domain), items, CardKeyLines.WellKnown(keys)),
            HubJsonContext.Default.CardKeysResponse);
    }

    // {"fingerprint": string, "revoked": "yyyy-MM-dd"}; the message of the first missing piece, or null.
    private static string? ReadRevoke(byte[] payload, out string fingerprint, out DateOnly on)
    {
        fingerprint = "";
        on = default;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("fingerprint", out var key) ||
                key.ValueKind != JsonValueKind.String)
                return "Name the key to revoke by its fingerprint.";
            fingerprint = key.GetString()!;
            return root.TryGetProperty("revoked", out var revoked) && revoked.ValueKind == JsonValueKind.String &&
                   DateOnly.TryParseExact(revoked.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                       DateTimeStyles.None, out on)
                ? null
                : "Give the first day the key may have leaked.";
        }
        // The second is an unpaired surrogate, a string the reader cannot hand over.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return "The body is not valid JSON.";
        }
    }
}
