using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private static Task<IResult> CreateActionAsync(
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        return WriteActionAsync(true, request, database, access, options.Value, timeProvider, cancellationToken);
    }

    private static Task<IResult> UpdateActionAsync(
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        return WriteActionAsync(false, request, database, access, options.Value, timeProvider, cancellationToken);
    }

    /// <summary>
    ///     Declares or changes the action on a finding, in the name of the caller. Every check
    ///     comes before the write, the caller first, and a finding the caller may not read
    ///     answers the 404 an unknown one does.
    /// </summary>
    // Every parameter is the route's own or a collaborator the router injects.
#pragma warning disable S107
    private static async Task<IResult> WriteActionAsync(
        bool create,
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        HubOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
#pragma warning restore S107
    {
        if (RelayIdentity(request, options) is not { } identity)
            return Problem(
                StatusCodes.Status403Forbidden,
                "The Hub does not know who is asking, and an action is taken in somebody's name.");
        if (RefuseForeignRequest(request) is { } refusal)
            return refusal;
        // From the configuration alone and before the body is read, so it names no finding.
        var writes = access.Scope(request.HttpContext, AccessPermissions.ActionsWrite);
        if (writes.IsNothing)
            return Problem(StatusCodes.Status403Forbidden, "You may not act on findings.");
        var payload = await ReadBodyAsync(request, cancellationToken, MaxAckBodyBytes);
        if (payload is null)
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (ActionWrite.TryParse(payload, create, out var error) is not { } write)
            return Problem(StatusCodes.Status400BadRequest, error ?? "The request is invalid.");

        var reads = access.Scope(request.HttpContext, AccessPermissions.FindingsRead);
        if (RefuseCopies(await database.ReadFindingCopiesAsync(write.Signature, cancellationToken), reads, writes)
            is { } refused)
            return refused;

        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var outcome = create
            ? await database.CreateActionAsync(
                write.Signature,
                write.Take ? ActionStatuses.InProgress : write.Status!,
                write.Take ? identity : write.Owner,
                identity,
                nowMs,
                cancellationToken)
            : await database.UpdateActionAsync(
                write.Signature, write.Status, write.Owner, write.OwnerSet, identity, nowMs, cancellationToken);
        return await ActionAnswerAsync(outcome, create, write.Signature, database, request, cancellationToken);
    }

    // The action is global to the signature, so it is judged on every copy the caller reads and
    // allowed when one of them is writable.
    private static JsonHttpResult<AnalysisProblem>? RefuseCopies(
        IReadOnlyList<AccessCopy> copies,
        AccessScope reads,
        AccessScope writes)
    {
        var readable = copies.Where(reads.Reads).ToList();
        if (readable.Count == 0)
            return Problem(StatusCodes.Status404NotFound, "The Hub holds no such finding.");
        return readable.Any(writes.Writes)
            ? null
            : Problem(StatusCodes.Status403Forbidden, "You can read this finding but not act on it.");
    }

    private static async Task<IResult> ActionAnswerAsync(
        ActionWriteOutcome outcome,
        bool create,
        string signature,
        HubDatabase database,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case ActionWriteOutcome.Exists:
                return Problem(StatusCodes.Status409Conflict,
                    "This finding already has an action; change it with PATCH.");
            case ActionWriteOutcome.Missing:
                return Problem(StatusCodes.Status404NotFound, "This finding has no action yet.");
            case ActionWriteOutcome.Busy:
                request.HttpContext.Response.Headers.RetryAfter = "1";
                return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var action = (await database.ReadActionsAsync([signature], cancellationToken)).Single();
        return TypedResults.Json(
            new ActionResponse(action.Signature, action.Status, action.Owner, action.CreatedBy, action.CreatedAtMs,
                action.UpdatedAtMs, action.DoneAtMs),
            HubJsonContext.Default.ActionResponse,
            statusCode: create ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }
}
