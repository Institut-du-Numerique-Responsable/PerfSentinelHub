using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private const string RecapScreenHeader = "X-Recap-Screen";

    /// <summary>
    ///     Starts the recap GET /api/recap computes, on the Hub rather than inside the request, or
    ///     joins the one already running or kept for the same audience and query. The screen polls
    ///     the job for its progress, then reads the result in the view it shows.
    /// </summary>
    private static IResult StartRecapJob(
        HttpContext context,
        RecapJobs jobs,
        AccessPolicy access,
        IOptions<HubOptions> options,
        TimeProvider timeProvider)
    {
        if (RefuseForeignRequest(context.Request) is { } refusal)
            return refusal;
        var scope = access.Scope(context, AccessPermissions.FindingsRead);
        if (scope.IsNothing)
            return NoFindingsRead();

        var sources = access.Sources(context, options.Value.Sources);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var refresh = ReadOptional(context.Request, "refresh");
        if (refresh is not (null or "true") ||
            !TryParseRecapQuery(context.Request, sources, today, out var query, out _, out _))
            return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRecapQuery,
                "The recap query is invalid.");
        if (RecapAudience(context, scope, sources) is not { } audience)
            return NoFindingsRead();

        // Keyed on the period's first day, not the day named: every day of one period computes the same recap.
        var key = new RecapJobKey(audience, query.Kind, RecapPeriod.Of(query.Kind, query.At).From,
            query.Filters.Environment, query.Filters.SourceId, query.Service, query.Grouping);
        var start = jobs.Start(key, query with { Access = Narrowing(scope) }, refresh is not null,
            RecapFollowerOf(context));
        if (start.Job is not { } job)
        {
            context.Response.Headers.RetryAfter = "5";
            return Problem(StatusCodes.Status503ServiceUnavailable, ProblemCodes.RecapBusy,
                "The Hub is computing as many recaps as it runs at once, or one you follow is still running. "
                + "Try again in a few seconds.");
        }

        return TypedResults.Accepted($"/api/recap/jobs/{job.Id}", job);
    }

    private static IResult GetRecapJob(
        string id,
        HttpContext context,
        RecapJobs jobs,
        AccessPolicy access,
        IOptions<HubOptions> options)
    {
        if (RecapJobAudienceOf(context, access, options) is not { } audience)
            return NoFindingsRead();
        return jobs.Find(id, audience, RecapFollowerOf(context)) is { } job
            ? TypedResults.Json(job, HubJsonContext.Default.RecapJobState)
            : TypedResults.NotFound();
    }

    /// <summary>The finished recap in the view asked, cut as GET /api/recap cuts it unless moves=all.</summary>
    private static IResult GetRecapJobResult(
        string id,
        HttpContext context,
        RecapJobs jobs,
        AccessPolicy access,
        IOptions<HubOptions> options)
    {
        if (RecapJobAudienceOf(context, access, options) is not { } audience)
            return NoFindingsRead();
        if (!TryReadRecapView(context.Request, out var pilotage, out var allMoves))
            return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRecapQuery,
                "The recap query is invalid.");

        if (jobs.Result(id, audience) is not { } snapshot)
            return jobs.Find(id, audience) switch
            {
                null => TypedResults.NotFound(),
                // Succeeded between the two reads: it is ready on the next one, not failed.
                { Status: "running" or "succeeded" } => Problem(StatusCodes.Status409Conflict,
                    ProblemCodes.RecapNotReady, "This recap is not computed yet."),
                // Failed, or stopped with the Hub: it will never be, and waiting on it would wait forever.
                _ => Problem(StatusCodes.Status409Conflict, ProblemCodes.RecapFailed,
                    "This recap was not computed. Start it again.")
            };
        var shown = pilotage ? RecapViews.Pilotage(snapshot) : snapshot;
        return TypedResults.Json(allMoves ? shown : RecapViews.Listed(shown), HubJsonContext.Default.RecapSnapshot);
    }

    /// <summary>
    ///     Lets go of a recap, as the screen does when its filters change: it stops once no screen that
    ///     started or joined it follows it.
    /// </summary>
    private static IResult CancelRecapJob(
        string id,
        HttpContext context,
        RecapJobs jobs,
        AccessPolicy access,
        IOptions<HubOptions> options)
    {
        if (RefuseForeignRequest(context.Request) is { } refusal)
            return refusal;
        if (RecapJobAudienceOf(context, access, options) is not { } audience)
            return NoFindingsRead();
        return jobs.Cancel(id, audience, RecapFollowerOf(context))
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    private static RecapJobAudience? RecapJobAudienceOf(HttpContext context, AccessPolicy access,
        IOptions<HubOptions> options)
    {
        var scope = access.Scope(context, AccessPermissions.FindingsRead);
        return scope.IsNothing ? null : RecapAudience(context, scope, access.Sources(context, options.Value.Sources));
    }

    // Who else would be computed the same recap: everyone with the same sources when the scope is
    // whole, the caller alone under that same scope when it is narrowed, since one person's sessions
    // can hold different groups. Null for a narrowed scope with nobody behind it.
    private static RecapJobAudience? RecapAudience(HttpContext context, AccessScope scope,
        IReadOnlyList<SourceOptions> sources)
    {
        string? owner = null;
        if (Narrowing(scope) is not null)
        {
            if (AccessCaller.Of(context)?.Subject is not { } subject)
                return null;
            owner = subject + "\n" + scope.ToJson();
        }

        return new RecapJobAudience(owner, string.Join('\n', sources.Select(source => source.Id)));
    }

    // The caller, and the screen a launcher page names in X-Recap-Screen: an id it draws once per load,
    // so two tabs of one person, or two people on a Hub without sign-in, follow a job apart. A value
    // that is not a short token is ignored, as if no screen were named.
    private static RecapFollower RecapFollowerOf(HttpContext context)
    {
        var screen = context.Request.Headers[RecapScreenHeader].ToString();
        var valid = screen.Length is > 0 and <= 64 && screen.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
        return new RecapFollower(AccessCaller.Of(context)?.Subject, valid ? screen : null);
    }
}
