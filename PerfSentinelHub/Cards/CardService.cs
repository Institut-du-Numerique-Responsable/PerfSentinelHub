using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Cards;

/// <summary>
///     Computes a share card over a closed period: the signed scope, the fixes the measure confirms,
///     then the story of one, the waste-ratio curve or the collective count. The rules live in
///     CardRules and CardFigures; this class reads the store and assembles card.json.
/// </summary>
public sealed class CardService(HubDatabase database, IOptions<HubOptions> options, TimeProvider timeProvider)
{
    private const long DayMs = 86_400_000;
    private const int MaxScopeChars = 120;
    private const int CurvePeriods = 6;

    private CardOrg Org => new(options.Value.Cards.Organisation ?? "", options.Value.Cards.Domain);

    /// <summary>What the screen shows before anyone signs, the period's own refusal included.</summary>
    public async Task<CardOptions> OptionsAsync(CardScopeAsk ask, CancellationToken cancellationToken)
    {
        var frame = await FrameAsync(ask, cancellationToken);
        var (previousWhole, countable) = await PreviousAsync(frame, cancellationToken);
        var told = frame.Refusal is null ? await TellAsync(frame, previousWhole, cancellationToken) : [];
        var stories = Stories(told);
        var labels = await database.ReadEndpointLabelsAsync(
            [.. stories.Select(told => (told.Story!.Service, told.Story.Endpoint))], cancellationToken);

        return new CardOptions(
            PeriodOf(frame.Period),
            CardRules.PeriodName(frame.Period),
            frame.Refusal,
            frame.Refusal is { } reason ? CardRules.Explain(reason, frame.Period) : null,
            frame.Scope.RestartDays,
            frame.Scope.SampledPermille,
            frame.Scope.Environment,
            frame.Scope.Sources,
            countable,
            previousWhole,
            told.Count,
            [.. stories.Select(item => FixOf(item, labels))],
            stories.Count == 0 ? null : Default(stories).Fix.Signature,
            Org);
    }

    public async Task<CardOutcome> ComputeAsync(CardAsk ask, CancellationToken cancellationToken)
    {
        var frame = await FrameAsync(ask.Scope, cancellationToken);
        if (frame.Refusal is { } reason)
            return new CardOutcome.Refused(reason);

        return ask.Angle switch
        {
            "story" => await StoryAsync(frame, ask, cancellationToken),
            "curve" => await CurveAsync(frame, ask, cancellationToken),
            "collective" => await CollectiveAsync(frame, ask, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(ask), ask.Angle, "Unknown card angle.")
        };
    }

    // ------------------------------------------------------------ period and scope

    private async Task<Frame> FrameAsync(CardScopeAsk ask, CancellationToken cancellationToken)
    {
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var today = (int)(nowMs / DayMs);
        var period = RecapPeriod.Of(ask.Kind, ask.At);
        // Only the sources that recorded the period are judged, and only they are signed. The periods
        // before keep a source that started during this one: they are compared with it.
        var (judged, _) = RecapCoverage.SplitLate(
            await database.ReadRecapCoverageAsync(ask.SourceIds, period.FromDay, period.ToDay, cancellationToken),
            period);
        var info = RecapCoverage.Judge(period, today, judged, null);
        var scope = ScopeOf(ask, period, info, judged);
        return new Frame(ask, nowMs, period, judged, scope, RefusalOf(period, today, judged, info, scope));
    }

    private static string? RefusalOf(
        RecapPeriod period,
        int today,
        IReadOnlyList<SourceCoverage> judged,
        RecapPeriodInfo info,
        CardScope scope)
    {
        if (period.ToDay >= today)
            return "open_period";
        if (judged.Count == 0)
            return "uncovered_days";
        return CardRules.Refusal(info) ?? ScopeRefusal(scope);
    }

    // The engine prints environment and sources on one line, joined with ", ", within the same bound.
    // A curve signs a subset of these sources, so its scope fits whenever this one does.
    private static string? ScopeRefusal(CardScope scope)
    {
        string[] names = scope.Environment is null ? [.. scope.Sources] : [scope.Environment, .. scope.Sources];
        if (!names.All(name => CardRules.Printable(name, MaxScopeChars)))
            return "unprintable_scope";
        return string.Join(", ", names).Length > MaxScopeChars ? "scope_too_long" : null;
    }

    private static CardScope ScopeOf(
        CardScopeAsk ask,
        RecapPeriod period,
        RecapPeriodInfo info,
        IReadOnlyList<SourceCoverage> judged)
    {
        return new CardScope(
            ask.Environment,
            [
                .. judged.Select(source => ask.Sources.FirstOrDefault(item => item.Id == source.SourceId)?.Name ??
                                           source.SourceId).Order(StringComparer.Ordinal)
            ],
            period.ToDay - period.FromDay + 1,
            CardRules.RestartDays(judged, period),
            CardRules.SampledPermille(info.SamplingRate));
    }

    // The period rule at these sources, unprefixed; null when it holds. No ledger there is no day covered.
    private async Task<string?> PeriodRefusalAsync(
        IReadOnlyList<string> sourceIds,
        RecapPeriod period,
        int today,
        CancellationToken cancellationToken)
    {
        var judged = await database.ReadRecapCoverageAsync(sourceIds, period.FromDay, period.ToDay,
            cancellationToken);
        return judged.Count == 0 ? "uncovered_days" : CardRules.Refusal(RecapCoverage.Judge(period, today, judged, null));
    }

    // Whether the previous period holds the period rule, and whether its fixes can be counted:
    // not when one of them was done before the Hub froze done findings.
    private async Task<(bool Whole, bool Countable)> PreviousAsync(Frame frame, CancellationToken cancellationToken)
    {
        var previous = frame.Period.Previous();
        var whole = await PeriodRefusalAsync(frame.SourceIds, previous, frame.Today, cancellationToken) is null;
        return (whole, whole && !await UnkeptAsync(frame, previous, cancellationToken));
    }

    private Task<bool> UnkeptAsync(Frame frame, RecapPeriod period, CancellationToken cancellationToken)
    {
        return database.HasUnkeptDoneActionAsync(frame.SourceIds, period.FromDay, period.ToDay, cancellationToken);
    }

    // ------------------------------------------------------------ fixes

    // The fixes of a period: done with a frozen finding of a known family, counted at a scope source
    // the caller reads that service at, and confirmed there as of now.
    private async Task<List<Fix>> FixesAsync(
        Frame frame,
        RecapPeriod period,
        CancellationToken cancellationToken)
    {
        var done = (await database.ReadDoneActionsAsync(period.FromDay, period.ToDay, cancellationToken))
            .Select(action => (Action: action, Frozen: CardEnvelope.Read(action.FrozenFindingJson)))
            .Where(item => CardTypes.Family(item.Frozen.FindingType) != FindingLabels.Other)
            .ToList();
        if (done.Count == 0)
            return [];

        var counted = (await database.ReadCountedSourcesAsync(
                [.. done.Select(item => item.Action.Signature)], frame.SourceIds, cancellationToken))
            .ToLookup(pair => pair.Signature, pair => pair.SourceId, StringComparer.Ordinal);
        var fixes = done
            .Select(item => new Fix(
                item.Action.Signature,
                item.Action.DoneAtMs,
                item.Frozen,
                counted[item.Action.Signature]
                    .Where(source => frame.Ask.Reads?.Reads(new AccessCopy(source, item.Frozen.Service, [])) ?? true)
                    .ToHashSet(StringComparer.Ordinal)))
            .Where(fix => fix.Sources.Count > 0)
            .ToList();
        return await ConfirmedAsync(fixes, frame.SourceIds, cancellationToken);
    }

    private async Task<List<Fix>> ConfirmedAsync(
        List<Fix> fixes,
        IReadOnlyList<string> sourceIds,
        CancellationToken cancellationToken)
    {
        if (fixes.Count == 0)
            return [];

        var (detected, sealedReachable) = await database.ReadActionEvidenceAsync(
            [.. fixes.Select(fix => fix.Signature)], sourceIds, fixes.Min(fix => fix.DoneDay), cancellationToken);
        return [.. fixes.Where(fix => Confirmed(fix, detected, sealedReachable))];
    }

    // As the recap judges an action, on the fix's own sources only.
    private static bool Confirmed(
        Fix fix,
        IReadOnlyList<(string Signature, string SourceId, int Day)> detected,
        IReadOnlyList<(string SourceId, int Day)> sealedReachable)
    {
        var detectedDays = detected
            .Where(item => item.Signature == fix.Signature && fix.Sources.Contains(item.SourceId))
            .Select(item => item.Day)
            .ToHashSet();
        var cleanDays = sealedReachable
            .Where(item => fix.Sources.Contains(item.SourceId) && !detectedDays.Contains(item.Day))
            .Select(item => item.Day);
        return ActionConfirmation.Judge(fix.DoneDay, detectedDays, cleanDays) == "confirmed";
    }

    // Each fix with its story, its volume before and its compared threshold. Every fix of the period
    // shares one before-fix period, RecapPeriod.Of(kind, done day).Previous() being the card's previous.
    private async Task<IReadOnlyList<Told>> TellAsync(Frame frame, bool previousWhole, CancellationToken cancellationToken)
    {
        var fixes = await FixesAsync(frame, frame.Period, cancellationToken);
        var previous = frame.Period.Previous();
        var carbon = previousWhole ? await CarbonAsync(fixes, previous, cancellationToken) : [];
        var signals = await SignalsAsync(fixes, cancellationToken);
        return [.. fixes.Select(fix => Tell(fix, previous, carbon, signals))];
    }

    private async Task<IReadOnlyList<(string Signature, CarbonDay Day)>> CarbonAsync(
        List<Fix> fixes,
        RecapPeriod previous,
        CancellationToken cancellationToken)
    {
        var volume = fixes.Where(fix => CardTypes.HasAvoidableVolume(fix.Frozen.FindingType)).ToList();
        return volume.Count == 0
            ? []
            : await database.ReadCarbonDaysAsync(
                [.. volume.Select(fix => fix.Signature)], [.. volume.SelectMany(fix => fix.Sources).Distinct()],
                previous.FromDay, previous.ToDay, cancellationToken);
    }

    private async Task<IReadOnlyList<SignalDay>> SignalsAsync(List<Fix> fixes, CancellationToken cancellationToken)
    {
        var watched = fixes.Where(fix => CardTypes.ThresholdTypes.Contains(fix.Frozen.FindingType)).ToList();
        return watched.Count == 0
            ? []
            : await database.ReadSignalDaysAsync(
                [.. watched.Select(fix => fix.Signature)], [.. watched.SelectMany(fix => fix.Sources).Distinct()],
                watched.Min(fix => fix.DoneDay), cancellationToken);
    }

    private static Told Tell(
        Fix fix,
        RecapPeriod previous,
        IReadOnlyList<(string Signature, CarbonDay Day)> carbon,
        IReadOnlyList<SignalDay> signals)
    {
        var story = CardFigures.Story(fix.Frozen, fix.DoneAtMs);
        var rows = carbon
            .Where(row => row.Signature == fix.Signature && fix.Sources.Contains(row.Day.SourceId))
            .Select(row => row.Day)
            .ToList();
        var ops = rows.Sum(row => row.ExtraIoOps);
        var before = ops > 0 ? new CardBefore(previous, ops, CardFigures.Micrograms(RecapCo2.LowGrams(rows))) : null;
        // No threshold without the trace date the card names its period by (a skewed one reads undated).
        var threshold = story?.ObservedAt is null
            ? null
            : CardFigures.ComparedThreshold(fix.Frozen.FindingType, fix.DoneDay,
                signals.Where(day => day.Signature == fix.Signature && fix.Sources.Contains(day.SourceId)));
        return new Told(fix, story, before, threshold);
    }

    private static List<Told> Stories(IReadOnlyList<Told> told)
    {
        return [.. told.Where(item => item.Story is not null)];
    }

    // A fix the card cannot name says so, rather than reading as unconfirmed or unknown.
    private static string Missing(IReadOnlyList<Told> all, string? signature, string otherwise)
    {
        return all.Any(item => (signature is null || item.Fix.Signature == signature) &&
                               CardFigures.Unprintable(item.Fix.Frozen))
            ? "unprintable_fix"
            : otherwise;
    }

    // The largest volume before, then the latest done, then the signature.
    private static Told Default(List<Told> stories)
    {
        return stories
            .OrderByDescending(told => told.Before?.Ops ?? 0)
            .ThenByDescending(told => told.Fix.DoneAtMs)
            .ThenBy(told => told.Fix.Signature, StringComparer.Ordinal)
            .First();
    }

    private static CardFix FixOf(Told told, IReadOnlyDictionary<(string Service, string Endpoint), string> labels)
    {
        var story = told.Story!;
        return new CardFix(
            told.Fix.Signature,
            story.FindingType,
            story.Service,
            story.Endpoint,
            RecapSentences.For(story.FindingType),
            told.Fix.DoneAtMs,
            told.Before?.Ops,
            told.Before is { } before ? CardRules.PeriodName(before.Period) : null,
            labels.GetValueOrDefault((story.Service, story.Endpoint)));
    }

    // ------------------------------------------------------------ angles

    private async Task<CardOutcome> StoryAsync(Frame frame, CardAsk ask, CancellationToken cancellationToken)
    {
        var (previousWhole, _) = await PreviousAsync(frame, cancellationToken);
        var all = await TellAsync(frame, previousWhole, cancellationToken);
        var stories = Stories(all);
        if (stories.Count == 0)
            return new CardOutcome.Refused(Missing(all, ask.Signature, "no_confirmed_fix"));

        var told = ask.Signature is null
            ? Default(stories)
            : stories.FirstOrDefault(item => item.Fix.Signature == ask.Signature);
        if (told is null)
            return new CardOutcome.Refused(Missing(all, ask.Signature, "unknown_fix"));

        var (figures, beforeFixPeriod) =
            CardFigures.StoryFigures(told.Fix.Frozen, frame.Period.Kind, told.Before, told.Threshold);
        return Ready(frame, ask, frame.Scope, figures, told.Story, beforeFixPeriod);
    }

    // The curve is daemon-wide, so it signs only the sources both scopes hold whole.
    private async Task<CardOutcome> CurveAsync(Frame frame, CardAsk ask, CancellationToken cancellationToken)
    {
        var whole = frame.Judged.Where(source => ask.Scope.WholeSourceIds.Contains(source.SourceId)).ToList();
        if (whole.Count == 0)
            return new CardOutcome.Refused("curve_needs_whole_sources");

        var ids = whole.Select(source => source.SourceId).ToList();
        var rows = await database.ReadDayTotalsAsync(ids, frame.Period.FromDay, frame.Period.ToDay,
            cancellationToken);
        if (CardFigures.Point(frame.Period, rows) is not { } point)
            return new CardOutcome.Refused("no_total_io");
        var (older, refusal) = await OlderPointsAsync(ids, frame, cancellationToken);
        if (refusal is not null)
            return new CardOutcome.Refused(refusal);

        var lows = rows.Where(row => row.AvoidableGco2Low is not null).ToList();
        var figures = new CardFiguresBlock(
            WastePermille: [.. older.Select(item => item.WastePermille).Reverse(), point.WastePermille],
            AvoidableOps: point.AvoidableOps,
            Co2LowUg: lows.Count == 0 ? null : CardFigures.Micrograms(lows.Sum(row => row.AvoidableGco2Low!.Value)));
        var info = RecapCoverage.Judge(frame.Period, frame.Today, whole, null);
        return Ready(frame, ask, ScopeOf(ask.Scope, frame.Period, info, whole), figures, null, null);
    }

    // The periods before the card's, newest first, up to the first that fails the period rule or
    // holds no total; the previous one is required.
    private async Task<(List<CardPoint> Points, string? Refusal)> OlderPointsAsync(
        IReadOnlyList<string> ids,
        Frame frame,
        CancellationToken cancellationToken)
    {
        var points = new List<CardPoint>();
        var period = frame.Period.Previous();
        while (points.Count < CurvePeriods - 1)
        {
            var reason = await PeriodRefusalAsync(ids, period, frame.Today, cancellationToken);
            var point = reason is null
                ? CardFigures.Point(period,
                    await database.ReadDayTotalsAsync(ids, period.FromDay, period.ToDay, cancellationToken))
                : null;
            if (point is null)
            {
                if (points.Count > 0)
                    return (points, null);
                return ([], reason is null ? "no_total_io" : "previous_" + reason);
            }

            points.Add(point);
            period = period.Previous();
        }

        return (points, null);
    }

    private async Task<CardOutcome> CollectiveAsync(Frame frame, CardAsk ask, CancellationToken cancellationToken)
    {
        // A fix the Hub kept no finding of would leave the count short, and the card would sign it.
        if (await UnkeptAsync(frame, frame.Period, cancellationToken))
            return new CardOutcome.Refused("unkept_fixes");
        var (previousWhole, countable) = await PreviousAsync(frame, cancellationToken);
        var told = await TellAsync(frame, previousWhole, cancellationToken);
        if (told.Count == 0)
            return new CardOutcome.Refused("no_confirmed_fix");

        var previous = frame.Period.Previous();
        var befores = told.Select(item => item.Before).OfType<CardBefore>().ToList();
        var lows = befores.Where(before => before.Co2LowUg is not null).ToList();
        var figures = new CardFiguresBlock(
            ConfirmedFixesCount: told.Count,
            PreviousFixesCount: countable ? (await FixesAsync(frame, previous, cancellationToken)).Count : null,
            ServicesCount: told.Select(item => item.Fix.Frozen.Service).Distinct(StringComparer.Ordinal).Count(),
            FixesByFamilyCount: CardFigures.Families(told.Select(item => item.Fix.Frozen.FindingType)),
            BeforeFixOps: befores.Count == 0 ? null : befores.Sum(before => before.Ops),
            Co2LowUg: lows.Count == 0 ? null : lows.Sum(before => before.Co2LowUg));
        return Ready(frame, ask, frame.Scope, figures, null, befores.Count == 0 ? null : CardFigures.Range(previous));
    }

    private CardOutcome.Ready Ready(
        Frame frame,
        CardAsk ask,
        CardScope scope,
        CardFiguresBlock figures,
        CardStory? story,
        CardDayRange? beforeFixPeriod)
    {
        return new CardOutcome.Ready(new CardInput(
            ask.Angle, ask.Lang, PeriodOf(frame.Period), scope, figures, story, beforeFixPeriod, ask.Free, Org,
            frame.NowMs));
    }

    private static CardPeriod PeriodOf(RecapPeriod period)
    {
        var range = CardFigures.Range(period);
        return new CardPeriod(period.KindName, range.From, range.To);
    }

    // ------------------------------------------------------------ working types

    private sealed record Frame(
        CardScopeAsk Ask,
        long NowMs,
        RecapPeriod Period,
        IReadOnlyList<SourceCoverage> Judged,
        CardScope Scope,
        string? Refusal)
    {
        public int Today => (int)(NowMs / DayMs);

        public IReadOnlyList<string> SourceIds { get; } = [.. Judged.Select(source => source.SourceId)];
    }

    private sealed record Fix(string Signature, long DoneAtMs, FrozenFinding Frozen, IReadOnlySet<string> Sources)
    {
        public int DoneDay => (int)(DoneAtMs / DayMs);
    }

    private sealed record Told(Fix Fix, CardStory? Story, CardBefore? Before, long? Threshold);
}
