using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    /// <summary>
    ///     Names or renames an endpoint, in the name of the caller. Every check comes before the
    ///     write, the caller first, and an endpoint the caller may not read answers the 404 an
    ///     unknown one does.
    /// </summary>
    private static async Task<IResult> SetLabelAsync(
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
    {
        if (RelayIdentity(request, options.Value) is not { } identity)
            return Problem(
                StatusCodes.Status403Forbidden,
                "The Hub does not know who is asking, and a label is set in somebody's name.");
        if (RefuseForeignRequest(request) is { } refusal)
            return refusal;
        // From the configuration alone and before the body is read, so it names no endpoint.
        var writes = access.Scope(request.HttpContext, AccessPermissions.LabelsWrite);
        if (writes.IsNothing)
            return Problem(StatusCodes.Status403Forbidden, "You may not name endpoints.");
        var payload = await ReadBodyAsync(request, cancellationToken, MaxAckBodyBytes);
        if (payload is null)
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (LabelWrite.TryParse(payload, out var error) is not { } write)
            return Problem(StatusCodes.Status400BadRequest, error ?? "The request is invalid.");

        var reads = access.Scope(request.HttpContext, AccessPermissions.FindingsRead);
        var copies = await database.ReadEndpointCopiesAsync(write.Service, write.Endpoint, cancellationToken);
        if (RefuseEndpoint(copies, reads, writes) is { } refused)
            return refused;

        if (await database.SetEndpointLabelAsync(write.Service, write.Endpoint, write.Label, identity,
                cancellationToken))
            return TypedResults.NoContent();
        request.HttpContext.Response.Headers.RetryAfter = "1";
        return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    // A label is global to the endpoint, so it is judged on every copy of every finding there the
    // caller reads and allowed when one of them is writable.
    private static JsonHttpResult<AnalysisProblem>? RefuseEndpoint(
        IReadOnlyList<AccessCopy> copies,
        AccessScope reads,
        AccessScope writes)
    {
        var readable = copies.Where(reads.Reads).ToList();
        if (readable.Count == 0)
            return Problem(StatusCodes.Status404NotFound, "The Hub holds no finding at this endpoint.");
        return readable.Any(writes.Writes)
            ? null
            : Problem(StatusCodes.Status403Forbidden, "You can read this endpoint but not name it.");
    }
}
