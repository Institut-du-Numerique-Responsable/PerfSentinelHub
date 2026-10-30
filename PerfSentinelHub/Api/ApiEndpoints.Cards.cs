using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Cards;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private const int MaxCardRevokeBodyBytes = 1024;
    private const string NoCardEngine = "This Hub's engine cannot make cards yet.";

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
