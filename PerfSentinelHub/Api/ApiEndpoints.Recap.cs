using System.Globalization;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private const string RecapPilotageView = "pilotage";

    /// <summary>
    ///     The review recap of one period, built from the copies the caller reads under
    ///     findings.read, as the findings list is. A caller holding it nowhere gets the 403 the
    ///     list gives, never an empty recap.
    /// </summary>
    private static async Task<IResult> GetRecapAsync(
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
        if (!TryParseRecapQuery(context.Request, sources, today, out var query, out var pilotage, out var allMoves))
            return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRecapQuery,
                "The recap query is invalid.");

        var snapshot = await recaps.ComputeAsync(query with { Access = Narrowing(scope) }, cancellationToken);
        var view = pilotage ? RecapViews.Pilotage(snapshot) : snapshot;
        return TypedResults.Json(allMoves ? view : RecapViews.Listed(view), HubJsonContext.Default.RecapSnapshot);
    }

    private static bool TryParseRecapQuery(
        HttpRequest request,
        IReadOnlyList<SourceOptions> sources,
        DateOnly today,
        out RecapRequest query,
        out bool pilotage,
        out bool allMoves)
    {
        query = null!;
        pilotage = false;
        allMoves = false;
        if (!HasValidUtf8(request.QueryString.Value) || request.Query.Any(item => item.Value.Count != 1))
            return false;

        var view = ReadOptional(request, "view") ?? "tech";
        // moves=all lifts the cut of Listed, for a reader who opens a whole group.
        var moves = ReadOptional(request, "moves");
        if (moves is not (null or "all") ||
            view is not ("tech" or RecapPilotageView) ||
            !RecapPeriod.TryParseKind(ReadOptional(request, "period") ?? "week", out var kind) ||
            !TryReadRecapDate(request, today, out var at) ||
            !TryReadSourceScope(request, sources, out var sourceIds))
            return false;

        var service = ReadOptional(request, "service");
        var grouping = ReadOptional(request, "grouping");
        pilotage = view == RecapPilotageView;
        allMoves = moves == "all";
        query = new RecapRequest(
            kind, at, sourceIds, service, grouping, null, sources,
            new RecapFilters(
                ReadOptional(request, "environment"), ReadOptional(request, "source_id"), service, grouping));
        return true;
    }

    // Today UTC when absent; a day in the future has nothing to recap.
    private static bool TryReadRecapDate(HttpRequest request, DateOnly today, out DateOnly at)
    {
        at = today;
        return ReadOptional(request, "at") is not { } raw ||
               (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out at) &&
                at <= today);
    }
}
