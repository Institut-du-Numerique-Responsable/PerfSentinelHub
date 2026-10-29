using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private static readonly LabelTarget EndpointLabels = new(
        true,
        "You may not name endpoints.",
        "The Hub holds no finding at this endpoint.",
        "You can read this endpoint but not name it.");

    private static readonly LabelTarget ServiceLabels = new(
        false,
        "You may not name services.",
        "The Hub holds no finding of this service.",
        "You can read this service but not name it.");

    /// <summary>Names or renames an endpoint, in the name of the caller.</summary>
    private static Task<IResult> SetLabelAsync(
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
    {
        return WriteLabelAsync(request, database, access, options.Value, EndpointLabels, cancellationToken);
    }

    /// <summary>Names or renames a whole service, in the name of the caller.</summary>
    private static Task<IResult> SetServiceLabelAsync(
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        CancellationToken cancellationToken)
    {
        return WriteLabelAsync(request, database, access, options.Value, ServiceLabels, cancellationToken);
    }

    /// <summary>
    ///     Every check comes before the write, the caller first, and a target the caller may not
    ///     read answers the 404 an unknown one does.
    /// </summary>
    private static async Task<IResult> WriteLabelAsync(
        HttpRequest request,
        HubDatabase database,
        AccessPolicy access,
        HubOptions options,
        LabelTarget target,
        CancellationToken cancellationToken)
    {
        if (RelayIdentity(request, options) is not { } identity)
            return Problem(
                StatusCodes.Status403Forbidden,
                "The Hub does not know who is asking, and a label is set in somebody's name.");
        if (RefuseForeignRequest(request) is { } refusal)
            return refusal;
        // From the configuration alone and before the body is read, so it names nothing.
        var writes = access.Scope(request.HttpContext, AccessPermissions.LabelsWrite);
        if (writes.IsNothing)
            return Problem(StatusCodes.Status403Forbidden, target.MayNot);
        var payload = await ReadBodyAsync(request, cancellationToken, MaxAckBodyBytes);
        if (payload is null)
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var write = target.WithEndpoint
            ? LabelWrite.TryParse(payload, out var error)
            : LabelWrite.TryParseService(payload, out error);
        if (write is null)
            return Problem(StatusCodes.Status400BadRequest, error ?? "The request is invalid.");

        var reads = access.Scope(request.HttpContext, AccessPermissions.FindingsRead);
        var copies = await database.ReadLabelCopiesAsync(write.Service, write.Endpoint,
            copy => reads.Reads(copy) && writes.Writes(copy), cancellationToken);
        if (RefuseLabel(copies, reads, writes, target) is { } refused)
            return refused;

        if (await SaveLabelAsync(database, write, identity, cancellationToken))
            return TypedResults.NoContent();
        request.HttpContext.Response.Headers.RetryAfter = "1";
        return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static Task<bool> SaveLabelAsync(
        HubDatabase database,
        LabelWrite write,
        string identity,
        CancellationToken cancellationToken)
    {
        return write.Endpoint is { } endpoint
            ? database.SetEndpointLabelAsync(write.Service, endpoint, write.Label, identity, cancellationToken)
            : database.SetServiceLabelAsync(write.Service, write.Label, identity, cancellationToken);
    }

    // A label is global to its endpoint or service, so it is judged on the copies of the findings
    // there the caller reads and allowed when one of them is writable. The copies stop at the first
    // that is both, which is all this needs.
    private static JsonHttpResult<AnalysisProblem>? RefuseLabel(
        IReadOnlyList<AccessCopy> copies,
        AccessScope reads,
        AccessScope writes,
        LabelTarget target)
    {
        var readable = copies.Where(reads.Reads).ToList();
        if (readable.Count == 0)
            return Problem(StatusCodes.Status404NotFound, target.Unknown);
        return readable.Any(writes.Writes)
            ? null
            : Problem(StatusCodes.Status403Forbidden, target.ReadOnly);
    }

    // What one kind of label is named on, and the sentences it refuses with.
    private sealed record LabelTarget(bool WithEndpoint, string MayNot, string Unknown, string ReadOnly);
}
