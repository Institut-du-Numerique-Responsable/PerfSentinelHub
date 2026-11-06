using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    /// <summary>
    ///     The services and namespaces a reader may filter the recap on, under the access of the recap
    ///     itself: no name outside the caller's findings.read scope, the same 403 when held nowhere.
    /// </summary>
    private static async Task<IResult> GetRecapFiltersAsync(
        HttpContext context,
        RecapService recaps,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var scope = access.Scope(context, AccessPermissions.FindingsRead);
        if (scope.IsNothing)
            return NoFindingsRead();

        var sources = access.Sources(context, options.Value.Sources);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (!TryParseRecapQuery(context.Request, sources, today, out var query, out _, out _))
            return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRecapQuery,
                "The recap query is invalid.");

        var filters = await recaps.FilterOptionsAsync(query with { Access = Narrowing(scope) }, cancellationToken);
        return TypedResults.Json(filters, HubJsonContext.Default.RecapFilterOptions);
    }
}
