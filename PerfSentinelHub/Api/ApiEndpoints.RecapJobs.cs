using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
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

        var key = new RecapJobKey(audience, query.Kind, query.At, query.Filters.Environment, query.Filters.SourceId,
            query.Service, query.Grouping);
        var start = jobs.Start(key, query with { Access = Narrowing(scope) }, refresh is not null);
        if (start.Job is not { } job)
        {
            context.Response.Headers.RetryAfter = "5";
            return Problem(StatusCodes.Status503ServiceUnavailable, ProblemCodes.RecapBusy,
                "The Hub is computing as many recaps as it runs at once. Try again in a few seconds.");
        }

        return TypedResults.Accepted($"/api/recap/jobs/{job.Id}", RecapJobStateOf(job));
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
        return jobs.Find(id, audience) is { } job
            ? TypedResults.Json(RecapJobStateOf(job), HubJsonContext.Default.RecapJobState)
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
        var view = ReadOptional(context.Request, "view") ?? "tech";
        var moves = ReadOptional(context.Request, "moves");
        if (!HasValidUtf8(context.Request.QueryString.Value) ||
            context.Request.Query.Any(item => item.Value.Count != 1) ||
            view is not ("tech" or RecapPilotageView) || moves is not (null or "all"))
            return Problem(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRecapQuery,
                "The recap query is invalid.");

        if (jobs.Result(id, audience) is not { } snapshot)
            return jobs.Find(id, audience) is null
                ? TypedResults.NotFound()
                : Problem(StatusCodes.Status409Conflict, ProblemCodes.RecapNotReady, "This recap is not computed yet.");
        var shown = view == RecapPilotageView ? RecapViews.Pilotage(snapshot) : snapshot;
        return TypedResults.Json(moves == "all" ? shown : RecapViews.Listed(shown), HubJsonContext.Default.RecapSnapshot);
    }

    /// <summary>Stops a recap nobody waits for any more, as the screen does when its filters change.</summary>
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
        return jobs.Cancel(id, audience) ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static RecapJobAudience? RecapJobAudienceOf(HttpContext context, AccessPolicy access,
        IOptions<HubOptions> options)
    {
        var scope = access.Scope(context, AccessPermissions.FindingsRead);
        return scope.IsNothing ? null : RecapAudience(context, scope, access.Sources(context, options.Value.Sources));
    }

    // Who else would be computed the same recap: everyone with the same sources when the scope is
    // whole, the caller alone when it is narrowed. Null for a narrowed scope with nobody behind it.
    private static RecapJobAudience? RecapAudience(HttpContext context, AccessScope scope,
        IReadOnlyList<SourceOptions> sources)
    {
        string? owner = null;
        if (Narrowing(scope) is not null)
        {
            if (AccessCaller.Of(context)?.Subject is not { } subject)
                return null;
            owner = subject;
        }

        return new RecapJobAudience(owner, string.Join('\n', sources.Select(source => source.Id)));
    }

    private static RecapJobState RecapJobStateOf(RecapJobView job)
    {
        return new RecapJobState(
            job.Id,
            job.Status switch
            {
                RecapJobStatus.Running => "running",
                RecapJobStatus.Succeeded => "succeeded",
                RecapJobStatus.Failed => "failed",
                RecapJobStatus.Cancelled => "cancelled",
                _ => throw new ArgumentOutOfRangeException(nameof(job), job.Status, null)
            },
            job.Stage is { } stage ? RecapStages.Name(stage) : null,
            [
                .. job.Stages.Select(item => new RecapJobStageItem(
                    RecapStages.Name(item.Stage),
                    item.State switch
                    {
                        RecapStageState.Pending => "pending",
                        RecapStageState.Running => "running",
                        RecapStageState.Done => "done",
                        RecapStageState.Skipped => "skipped",
                        _ => throw new ArgumentOutOfRangeException(nameof(job), item.State, null)
                    },
                    item.Ms))
            ],
            Math.Round(job.Progress, 4),
            job.StartedAtMs,
            job.FinishedAtMs,
            job.ExpiresAtMs,
            job.ErrorCode);
    }
}
