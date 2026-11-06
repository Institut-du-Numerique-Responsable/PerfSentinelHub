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
        if (!TryReadRecapView(request, out pilotage, out allMoves) ||
            !RecapPeriod.TryParseKind(ReadOptional(request, "period") ?? "week", out var kind) ||
            !TryReadRecapDate(request, today, out var at) ||
            !TryReadSourceScope(request, sources, out var sourceIds) ||
            !ReadRecapValues(request, "service", out var services) ||
            !ReadRecapValues(request, "grouping", out var groupings))
            return false;

        query = new RecapRequest(
            kind, at, sourceIds, services, groupings, null, sources,
            RecapFilters.Of(ReadOptional(request, "environment"), ReadOptional(request, "source_id"), services,
                groupings));
        return true;
    }

    // Every value of a repeated key (service=a&service=b), blanks left out as ReadOptional leaves them,
    // in the canonical order. False past the caps of RecapFilterValues.
    private static bool ReadRecapValues(HttpRequest request, string name, out IReadOnlyList<string>? values)
    {
        string[] given = request.Query.TryGetValue(name, out var raw)
            ? [.. raw.OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value))]
            : [];
        return RecapFilterValues.TryCanonical(given, out values);
    }

    private static bool IsRecapFilterList(string key)
    {
        return key.Equals("service", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("grouping", StringComparison.OrdinalIgnoreCase);
    }

    // The view and the cut of a recap read, a job's result included, on a query with one value per key,
    // service and grouping excepted, which may repeat. moves=all lifts the cut of Listed, for a reader
    // who opens a whole group.
    private static bool TryReadRecapView(HttpRequest request, out bool pilotage, out bool allMoves)
    {
        var view = ReadOptional(request, "view") ?? "tech";
        var moves = ReadOptional(request, "moves");
        pilotage = view == RecapPilotageView;
        allMoves = moves == "all";
        return HasValidUtf8(request.QueryString.Value) &&
               request.Query.All(item => item.Value.Count == 1 || IsRecapFilterList(item.Key)) &&
               view is "tech" or RecapPilotageView && moves is null or "all";
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
