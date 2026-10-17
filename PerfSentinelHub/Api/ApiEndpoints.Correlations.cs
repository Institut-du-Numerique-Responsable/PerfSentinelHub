using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    // The spec's own bounds, not Hub:MaxReadLimit: a screen reads a range of episodes
    // whole, and a thousand rows of a few hundred bytes each is one cheap answer.
    private const int CorrelationDefaultLimit = 200;
    private const int CorrelationMaxLimit = 1000;

    /// <summary>
    ///     The episodes the daemons pushed, scoped as the incidents are: findings.read, and
    ///     with access on only the episodes whose two services the caller reads at their
    ///     source. Nothing reads a daemon here, the Hub serves what it was pushed.
    /// </summary>
    private static async Task<IResult> GetCorrelationsAsync(
        HttpContext context,
        HubDatabase database,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var scope = access.Scope(context, AccessPermissions.FindingsRead);
        if (scope.IsNothing)
            return NoFindingsRead();
        if (!TryParseCorrelationQuery(context.Request, access.Sources(context, options.Value.Sources),
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), out var query))
            return TypedResults.BadRequest();
        var rows = await database.ListEpisodesAsync(query with { Access = Narrowing(scope) }, cancellationToken);
        // Storage holds ids, the configuration names them, as IncidentWriter does for incidents.
        IReadOnlyList<StoredEpisode> named =
        [
            .. rows.Select(row => row with
            {
                SourceName = options.Value.Sources.FirstOrDefault(source =>
                    string.Equals(source.Id, row.SourceId, StringComparison.Ordinal))?.Name
            })
        ];
        return TypedResults.Ok(named);
    }

    private static bool TryParseCorrelationQuery(
        HttpRequest request,
        IReadOnlyList<SourceOptions> sources,
        long nowMs,
        out CorrelationQuery query)
    {
        query = null!;
        if (!HasValidUtf8(request.QueryString.Value) || request.Query.Any(item => item.Value.Count != 1))
            return false;
        if (!TryReadBounded(request, "limit", CorrelationDefaultLimit, 1, CorrelationMaxLimit, out var limit) ||
            !TryReadBounded(request, "offset", 0, 0, MaxFindingOffset, out var offset) ||
            !TryReadSourceScope(request, sources, out var sourceIds) ||
            !TryReadEpochMs(request, "from", out var fromMs) ||
            !TryReadEpochMs(request, "to", out var toMs) ||
            fromMs > toMs)
            return false;

        // Blank reads as absent, as every filter of this route does through ReadOptional.
        var active = false;
        if (ReadOptional(request, "active") is { } rawActive && !bool.TryParse(rawActive, out active))
            return false;

        query = new CorrelationQuery(
            ReadOptional(request, "service"),
            ReadOptional(request, "pair_key"),
            sourceIds,
            fromMs,
            toMs,
            active,
            nowMs,
            offset,
            limit);
        return true;
    }
}
