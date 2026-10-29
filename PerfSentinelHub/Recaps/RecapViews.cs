namespace PerfSentinelHub.Recaps;

public static class RecapViews
{
    public const int PilotagePriorities = 5;

    /// <summary>
    ///     The management view of a tech snapshot: top five, the plain sentence, no technical key. The
    ///     endpoint stays only where no label names it, so two rows of one type stay apart.
    /// </summary>
    public static RecapSnapshot Pilotage(RecapSnapshot tech)
    {
        return tech with
        {
            View = "pilotage",
            Priorities = [.. tech.Priorities.Take(PilotagePriorities).Select(Plain)],
            MorePriorities = Math.Max(0, tech.Priorities.Count - PilotagePriorities),
            Movements = new RecapMovements(
                Plain(tech.Movements.New),
                Plain(tech.Movements.Gone),
                tech.Movements.GoneWithConfirmedAction,
                Plain(tech.Movements.Up),
                Plain(tech.Movements.Down),
                Plain(tech.Movements.CameBack),
                Plain(tech.Movements.DoneStillDetected))
        };
    }

    private static RecapPriority Plain(RecapPriority priority)
    {
        return priority with
        {
            Signature = null,
            Endpoint = priority.Label is null ? priority.Endpoint : null,
            Template = null,
            Suggestion = null,
            SuggestedFix = null,
            CodeLocation = null,
            Confidence = null,
            Action = priority.Action is null ? null : priority.Action with { Events = null }
        };
    }

    private static List<RecapMove> Plain(IReadOnlyList<RecapMove> moves)
    {
        return
        [
            .. moves.Select(move => move with
            {
                Signature = null,
                Endpoint = move.Label is null ? move.Endpoint : null
            })
        ];
    }
}
