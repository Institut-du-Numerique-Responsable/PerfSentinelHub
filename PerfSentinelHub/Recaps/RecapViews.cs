using PerfSentinelHub.Localization;

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

    /// <summary>
    ///     The management view a frozen report and its webhook carry: as <see cref="Pilotage" />, but a
    ///     row no label names loses its endpoint too, so the report reads "an unnamed step" there.
    ///     An action owner is the name a person reads, never the identity behind it.
    /// </summary>
    public static RecapSnapshot Frozen(RecapSnapshot tech)
    {
        var view = Pilotage(tech);
        var moves = view.Movements;
        return view with
        {
            Priorities = [.. view.Priorities.Select(priority => ByName(priority with { Endpoint = null }))],
            Movements = moves with
            {
                New = Unnamed(moves.New),
                Gone = Unnamed(moves.Gone),
                Up = Unnamed(moves.Up),
                Down = Unnamed(moves.Down),
                CameBack = Unnamed(moves.CameBack),
                DoneStillDetected = Unnamed(moves.DoneStillDetected)
            }
        };
    }

    /// <summary>
    ///     <see cref="Frozen(RecapSnapshot)" /> with each plain sentence in <paramref name="lang" />, for the webhook:
    ///     the snapshot stores English, and a type this Hub has no sentence for keeps its own.
    /// </summary>
    public static RecapSnapshot Frozen(RecapSnapshot tech, string lang)
    {
        var view = Frozen(tech);
        if (lang == HubLang.En)
            return view;
        var moves = view.Movements;
        return view with
        {
            Priorities =
            [
                .. view.Priorities.Select(priority =>
                    priority with { Plain = In(lang, priority.FindingType, priority.Plain) })
            ],
            Movements = moves with
            {
                New = In(lang, moves.New),
                Gone = In(lang, moves.Gone),
                Up = In(lang, moves.Up),
                Down = In(lang, moves.Down),
                CameBack = In(lang, moves.CameBack),
                DoneStillDetected = In(lang, moves.DoneStillDetected)
            }
        };
    }

    private static string In(string lang, string findingType, string stored)
    {
        return RecapSentences.Knows(findingType) ? RecapSentences.For(findingType, lang) : stored;
    }

    private static List<RecapMove> In(string lang, IReadOnlyList<RecapMove> moves)
    {
        return [.. moves.Select(move => move with { Plain = In(lang, move.FindingType, move.Plain) })];
    }

    // A frozen report and its webhook leave the Hub: an owner is the name a person reads, never the
    // identity behind it, not in a title, the data island or the webhook body.
    private static RecapPriority ByName(RecapPriority priority)
    {
        if (priority.Action is not { } action)
            return priority;
        var name = NameOnly(RecapText.Owner(action.Owner, action.OwnerDisplay).Text);
        return priority with
        {
            Action = action with { Owner = null, OwnerDisplay = name.Length > 0 ? name : null, Events = null }
        };
    }

    // Owner keeps a mixed-case or hand-typed identity as it is. Here a name beside an e-mail stays, and
    // a bare e-mail or local: identity becomes the name built from it, else no owner shows.
    private static string NameOnly(string text)
    {
        var local = text.StartsWith("local:", StringComparison.OrdinalIgnoreCase);
        if (!local && !text.Contains('@'))
            return text;
        var words = (local ? text["local:".Length..] : text).Split([' ', '<'], StringSplitOptions.RemoveEmptyEntries);
        var kept = words.Where(word => !word.Contains('@')).ToList();
        if (words.Length > 1 && kept.Count > 0)
            return string.Join(' ', kept);
        var identity = words.FirstOrDefault(word => word.Contains('@'), words.FirstOrDefault(""))
            .Trim('<', '>', '(', ')', '"').ToLowerInvariant();
        var name = RecapText.Owner(identity, null).Text;
        return name.Contains('@') ? "" : name;
    }

    private static List<RecapMove> Unnamed(IReadOnlyList<RecapMove> moves)
    {
        return [.. moves.Select(move => move with { Endpoint = null })];
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
