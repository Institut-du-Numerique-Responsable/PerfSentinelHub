using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Localization;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Recaps;

/// <summary>What one recap is asked: the period, the filters and the sources and scope the caller reads.</summary>
public sealed record RecapRequest(
    RecapPeriodKind Kind,
    DateOnly At,
    IReadOnlyList<string>? SourceIds, // resolved environment/source_id filter, null = every source in Sources
    string? Service,
    string? Grouping,
    AccessScope? Access, // null = unrestricted (a worker); else the narrowed findings.read scope
    IReadOnlyList<SourceOptions> Sources, // the sources the caller may see
    RecapFilters Filters); // echoed as asked

/// <summary>
///     Computes the review recap from three windowed findings reads, so scope, filters, access and
///     the per-finding volume are the ones of GET /api/findings. Always the tech view; the
///     management view is a projection of it.
/// </summary>
public sealed class RecapService(HubDatabase database, IOptions<HubOptions> options, TimeProvider timeProvider)
{
    public const int MaxFindings = 5000;
    private const long DayMs = 86_400_000;
    private const string Unknown = "unknown";

    public async Task<RecapSnapshot> ComputeAsync(RecapRequest request, CancellationToken cancellationToken)
    {
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var span = Span.Of(request, (int)(nowMs / DayMs));
        var scope = ScopeSourceIds(request);
        var reads = await ReadFindingsAsync(request, span, cancellationToken);
        var coverage = await JudgeCoverageAsync(span, scope, cancellationToken);
        var current = await CarbonAsync(reads.Ranked, span.Period, scope, cancellationToken);
        var previous = await CarbonAsync(reads.Previous, span.PreviousWindow, scope, cancellationToken);
        var actions = await ReadActionsAsync(reads, scope, cancellationToken);
        var labels = await ReadLabelsAsync(reads, cancellationToken);

        var priorities = PrioritiesOf(reads, current, actions, labels);
        var movements = MovementsOf(reads, priorities, actions, labels);
        return new RecapSnapshot(
            1,
            "tech",
            nowMs,
            coverage.Current,
            coverage.Previous,
            request.Filters with { ServiceLabel = labels.OfService(request.Filters.Service) },
            coverage.Current.Incomplete.Count == 0 && coverage.Previous.Incomplete.Count == 0,
            TilesOf(reads, current, previous, actions),
            priorities,
            0,
            movements,
            RecapMoves.NothingMoved(movements, priorities),
            SourcesWithoutVolume(request, reads, coverage.Covered),
            reads.Truncated,
            NamesOf(request, coverage.Late));
    }

    // ------------------------------------------------------------ reads

    private async Task<Reads> ReadFindingsAsync(RecapRequest request, Span span, CancellationToken cancellationToken)
    {
        var all = await ReadAsync(request, span.Period, true, cancellationToken);
        var unacked = await ReadAsync(request, span.Period, false, cancellationToken);
        // A closed period compares as it stands; one in progress compares its closed days only.
        var closed = unacked;
        if (span.InProgress)
            closed = span.ClosedWindow is { } window
                ? await ReadAsync(request, window, false, cancellationToken)
                : Read.None;
        var previous = span.PreviousWindow is { } before
            ? await ReadAsync(request, before, false, cancellationToken)
            : null;

        return new Reads(
            all.Findings,
            RecapRanking.Order(unacked.Findings),
            closed.Findings.ToDictionary(finding => finding.Signature, StringComparer.Ordinal),
            previous?.Findings,
            all.Over || unacked.Over || closed.Over || previous?.Over == true);
    }

    private async Task<Read> ReadAsync(
        RecapRequest request,
        RecapPeriod window,
        bool includeAcked,
        CancellationToken cancellationToken)
    {
        var rows = await database.QueryFindingsAsync(
            new FindingQuery(
                request.Service, null, null, MaxFindings + 1, includeAcked, null, 0, request.SourceIds,
                window.FromMs, window.ToMs, null, request.Grouping, request.Access),
            cancellationToken);
        return new Read([.. rows.Take(MaxFindings).Select(RecapEnvelope.Read)], rows.Count > MaxFindings);
    }

    private static IReadOnlyList<string> ScopeSourceIds(RecapRequest request)
    {
        var ids = request.SourceIds ?? [.. request.Sources.Select(source => source.Id)];
        return [.. ids.Where(id => request.Access is null || request.Access.ReachesSource(id))];
    }

    // ------------------------------------------------------------ coverage

    private async Task<Coverage> JudgeCoverageAsync(
        Span span,
        IReadOnlyList<string> scope,
        CancellationToken cancellationToken)
    {
        var (now, late) = RecapCoverage.SplitLate(
            await database.ReadRecapCoverageAsync(scope, span.Period.FromDay, span.Period.ToDay, cancellationToken),
            span.Period);
        // A source late for the period is out of both sides; one that started during it leaves the
        // period before uncovered, since the comparison would set it against nothing.
        var before = (await database.ReadRecapCoverageAsync(scope, span.PreviousFull.FromDay,
                span.PreviousFull.ToDay, cancellationToken))
            .Where(source => !late.Contains(source.SourceId, StringComparer.Ordinal)).ToList();
        return new Coverage(
            RecapCoverage.Judge(span.Period, span.Today, now, null),
            RecapCoverage.Judge(span.PreviousFull, span.Today, before, span.TruncatedToDays),
            [.. now.Concat(before).Select(source => source.SourceId)],
            late);
    }

    private static List<string> NamesOf(RecapRequest request, IEnumerable<string> ids)
    {
        var names = request.Sources.ToDictionary(source => source.Id, source => source.Name, StringComparer.Ordinal);
        return [.. ids.Select(id => names.GetValueOrDefault(id) ?? id).Order(StringComparer.Ordinal)];
    }

    // A source that holds findings in the recap and no ledger: its volume is unknown, not zero.
    private static List<string> SourcesWithoutVolume(RecapRequest request, Reads reads, HashSet<string> covered)
    {
        var names = request.Sources.ToDictionary(source => source.Id, source => source.Name, StringComparer.Ordinal);
        return
        [
            .. reads.All.Concat(reads.Previous ?? [])
                .SelectMany(finding => finding.SourceIds)
                .Distinct(StringComparer.Ordinal)
                .Where(id => !covered.Contains(id) && names.ContainsKey(id))
                .Select(id => names[id])
                .Order(StringComparer.Ordinal)
        ];
    }

    // ------------------------------------------------------------ carbon

    private async Task<Carbon?> CarbonAsync(
        IReadOnlyList<RecapFinding>? findings,
        RecapPeriod? window,
        IReadOnlyList<string> scope,
        CancellationToken cancellationToken)
    {
        if (findings is null || window is not { } period)
            return null;

        var rows = await database.ReadCarbonDaysAsync(
            [.. findings.Select(finding => finding.Signature)], scope, period.FromDay, period.ToDay,
            cancellationToken);
        var bySignature = rows.ToLookup(row => row.Signature, row => row.Day, StringComparer.Ordinal);
        // A finding is priced on its own copies' sources only, as its volume is.
        var perFinding = findings.ToDictionary(
            finding => finding.Signature,
            finding => bySignature[finding.Signature].Where(day => finding.SourceIds.Contains(day.SourceId)).ToList(),
            StringComparer.Ordinal);
        return new Carbon(
            perFinding.ToDictionary(item => item.Key, item => RecapCo2.Estimate(item.Value), StringComparer.Ordinal),
            RecapCo2.Estimate([.. perFinding.Values.SelectMany(days => days)]));
    }

    // ------------------------------------------------------------ actions

    private async Task<IReadOnlyDictionary<string, RecapAction>> ReadActionsAsync(
        Reads reads,
        IReadOnlyList<string> scope,
        CancellationToken cancellationToken)
    {
        var sources = reads.All.Concat(reads.Previous ?? [])
            .GroupBy(finding => finding.Signature, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(finding => finding.SourceIds).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var stored = await database.ReadActionsAsync([.. sources.Keys], cancellationToken);
        var confirmations = await ConfirmAsync(stored, sources, scope, cancellationToken);
        return stored.ToDictionary(
            action => action.Signature,
            action => ActionOf(action, confirmations.GetValueOrDefault(action.Signature)),
            StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, string>> ConfirmAsync(
        IReadOnlyList<StoredAction> stored,
        Dictionary<string, HashSet<string>> sources,
        IReadOnlyList<string> scope,
        CancellationToken cancellationToken)
    {
        var done = stored.Where(action => action.DoneAtMs is not null).ToList();
        if (done.Count == 0)
            return [];

        var afterDay = (int)(done.Min(action => action.DoneAtMs!.Value) / DayMs);
        var (detected, sealedReachable) = await database.ReadActionEvidenceAsync(
            [.. done.Select(action => action.Signature)], scope, afterDay, cancellationToken);
        return done.ToDictionary(
            action => action.Signature,
            action => Judge(action, sources[action.Signature], detected, sealedReachable),
            StringComparer.Ordinal);
    }

    // A clean day is sealed at a reachable source of the finding and has no detection at any of them.
    private static string Judge(
        StoredAction action,
        HashSet<string> sources,
        IReadOnlyList<(string Signature, string SourceId, int Day)> detected,
        IReadOnlyList<(string SourceId, int Day)> sealedReachable)
    {
        var detectedDays = detected
            .Where(item => item.Signature == action.Signature && sources.Contains(item.SourceId))
            .Select(item => item.Day)
            .ToHashSet();
        var cleanDays = sealedReachable
            .Where(item => sources.Contains(item.SourceId) && !detectedDays.Contains(item.Day))
            .Select(item => item.Day)
            .Distinct();
        return ActionConfirmation.Judge((int)(action.DoneAtMs!.Value / DayMs), detectedDays, cleanDays);
    }

    private static RecapAction ActionOf(StoredAction action, string? confirmation)
    {
        return new RecapAction(
            action.Status,
            action.Owner,
            action.OwnerDisplay,
            action.UpdatedAtMs,
            action.DoneAtMs,
            confirmation,
            [
                .. action.Events.Select(item =>
                    new RecapActionEvent(item.FromStatus, item.ToStatus, item.Owner, item.OwnerDisplay, item.By,
                        item.AtMs))
            ]);
    }

    // ------------------------------------------------------------ labels

    // One read per table over everything the snapshot names, gone included: the (service, endpoint)
    // pairs, then the services. Only services of findings the caller reads: a typed service filter
    // never fetches a name, since names are Hub-wide and would reveal a service outside the scope.
    private async Task<Labels> ReadLabelsAsync(Reads reads, CancellationToken cancellationToken)
    {
        var named = reads.All.Concat(reads.Previous ?? []).ToList();
        var pairs = named.Select(finding => (finding.Service, finding.Endpoint)).Distinct().ToList();
        var services = named.Select(finding => finding.Service).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToList();
        return new Labels(
            await database.ReadEndpointLabelsAsync(pairs, cancellationToken),
            await database.ReadServiceLabelsAsync(services, cancellationToken));
    }

    // ------------------------------------------------------------ priorities and movements

    private IReadOnlyList<RecapPriority> PrioritiesOf(
        Reads reads,
        Carbon? carbon,
        IReadOnlyDictionary<string, RecapAction> actions,
        Labels labels)
    {
        return
        [
            .. reads.Ranked.Select((finding, index) =>
            {
                var (trend, change) = TrendOf(finding, reads);
                var co2 = carbon?.PerFinding.GetValueOrDefault(finding.Signature);
                return new RecapPriority(
                    index + 1,
                    finding.FindingType,
                    finding.Severity,
                    finding.Service,
                    // The API's plain stays English; a page reads its own language from the type.
                    RecapSentences.For(finding.FindingType, HubLang.En),
                    finding.Detections,
                    finding.AvoidableIoOps,
                    co2?.Grams,
                    co2 is { SourcesWithoutCoefficient: > 0 },
                    finding.VolumeKnown,
                    trend,
                    change,
                    labels.Of(finding),
                    labels.OfService(finding),
                    actions.GetValueOrDefault(finding.Signature),
                    finding.Signature,
                    finding.Endpoint,
                    finding.Template,
                    finding.Suggestion,
                    finding.SuggestedFix,
                    finding.CodeLocation,
                    finding.Confidence);
            })
        ];
    }

    // Both sides from closed days; a finding seen today only has no closed side and reads unknown.
    private (string Trend, double? Change) TrendOf(RecapFinding finding, Reads reads)
    {
        return reads.Closed.TryGetValue(finding.Signature, out var closed)
            ? RecapMoves.Trend(
                closed, reads.PreviousBySignature.GetValueOrDefault(finding.Signature),
                options.Value.Recaps.MovementThreshold)
            : (Unknown, null);
    }

    private static RecapMovements MovementsOf(
        Reads reads,
        IReadOnlyList<RecapPriority> priorities,
        IReadOnlyDictionary<string, RecapAction> actions,
        Labels labels)
    {
        var current = reads.All.Select(finding => finding.Signature).ToHashSet(StringComparer.Ordinal);
        var gone = RecapRanking.Order(reads.Previous ?? []).Where(finding => !current.Contains(finding.Signature))
            .ToList();
        var trends = priorities.ToDictionary(priority => priority.Signature!, StringComparer.Ordinal);

        return new RecapMovements(
            Trended(priorities, "new"),
            [.. gone.Select(finding => Move(finding, "gone", null, labels))],
            gone.Count(finding => actions.GetValueOrDefault(finding.Signature)?.Confirmation == "confirmed"),
            Trended(priorities, "up"),
            Trended(priorities, "down"),
            Confirmed(reads, actions, trends, labels, "came_back"),
            Confirmed(reads, actions, trends, labels, "contradicted"));
    }

    private static List<RecapMove> Trended(IReadOnlyList<RecapPriority> priorities, string trend)
    {
        return
        [
            .. priorities.Where(priority => priority.Trend == trend).Select(priority => new RecapMove(
                priority.FindingType, priority.Service, priority.Plain, priority.Trend, priority.Change,
                priority.Label, priority.ServiceLabel, priority.Signature, priority.Endpoint))
        ];
    }

    // The findings of the current read whose done action came back or was contradicted, in ranking order.
    private static List<RecapMove> Confirmed(
        Reads reads,
        IReadOnlyDictionary<string, RecapAction> actions,
        Dictionary<string, RecapPriority> ranked,
        Labels labels,
        string confirmation)
    {
        return
        [
            .. RecapRanking.Order(reads.All)
                .Where(finding => actions.GetValueOrDefault(finding.Signature)?.Confirmation == confirmation)
                .Select(finding => ranked.TryGetValue(finding.Signature, out var priority)
                    ? Move(finding, priority.Trend, priority.Change, labels)
                    : Move(finding, Unknown, null, labels))
        ];
    }

    private static RecapMove Move(RecapFinding finding, string trend, double? change, Labels labels)
    {
        return new RecapMove(
            finding.FindingType, finding.Service, RecapSentences.For(finding.FindingType, HubLang.En), trend, change,
            labels.Of(finding), labels.OfService(finding), finding.Signature, finding.Endpoint);
    }

    // ------------------------------------------------------------ tiles

    private static RecapTiles TilesOf(
        Reads reads,
        Carbon? current,
        Carbon? previous,
        IReadOnlyDictionary<string, RecapAction> actions)
    {
        var estimate = current?.Total;
        return new RecapTiles(
            SumIo(reads.Ranked),
            reads.Previous is null ? null : SumIo(reads.Previous),
            estimate?.Grams,
            previous?.Total.Grams,
            estimate?.SourcesWithoutCoefficient ?? 0,
            estimate?.MissingReason,
            reads.Ranked.Count,
            reads.Previous?.Count ?? 0,
            reads.All.Count - reads.Ranked.Count,
            CountsOf(reads.All.Select(finding => actions.GetValueOrDefault(finding.Signature)).OfType<RecapAction>()));
    }

    // Null when no finding has a known I/O: an unknown volume is not a zero.
    private static long? SumIo(IReadOnlyList<RecapFinding> findings)
    {
        return findings.Any(finding => finding.AvoidableIoOps is not null)
            ? findings.Sum(finding => finding.AvoidableIoOps ?? 0)
            : null;
    }

    private static RecapActionCounts CountsOf(IEnumerable<RecapAction> actions)
    {
        var list = actions.ToList();
        return new RecapActionCounts(
            list.Count(action => action.Status == ActionStatuses.Todo),
            list.Count(action => action.Status == ActionStatuses.InProgress),
            list.Count(action => action.Status == ActionStatuses.Done),
            list.Count(action => action.Confirmation == "confirmed"),
            list.Count(action => action.Confirmation == "contradicted"),
            list.Count(action => action.Confirmation == "came_back"));
    }

    // ------------------------------------------------------------ working types

    private sealed record Span(int Today, RecapPeriod Period, bool InProgress, int ClosedDays)
    {
        public RecapPeriod PreviousFull => Period.Previous();

        // The closed days of the period (the whole of it once over), none on its first day.
        public RecapPeriod? ClosedWindow => Window(Period);

        // The previous period cut to the same number of days, not read at all on the first day.
        public RecapPeriod? PreviousWindow => Window(PreviousFull);

        public int? TruncatedToDays => InProgress ? ClosedDays : null;

        private RecapPeriod? Window(RecapPeriod whole)
        {
            if (!InProgress)
                return whole;
            return ClosedDays >= 1 ? whole.Truncated(ClosedDays) : null;
        }

        public static Span Of(RecapRequest request, int today)
        {
            var period = RecapPeriod.Of(request.Kind, request.At);
            var inProgress = period.ToDay >= today;
            var closed = inProgress ? today - period.FromDay : period.ToDay - period.FromDay + 1;
            return new Span(today, period, inProgress, Math.Max(0, closed));
        }
    }

    private sealed record Read(IReadOnlyList<RecapFinding> Findings, bool Over)
    {
        public static readonly Read None = new([], false);
    }

    private sealed record Reads(
        IReadOnlyList<RecapFinding> All,
        IReadOnlyList<RecapFinding> Ranked,
        IReadOnlyDictionary<string, RecapFinding> Closed,
        IReadOnlyList<RecapFinding>? Previous,
        bool Truncated)
    {
        public IReadOnlyDictionary<string, RecapFinding> PreviousBySignature { get; } =
            (Previous ?? []).ToDictionary(finding => finding.Signature, StringComparer.Ordinal);
    }

    private sealed record Coverage(
        RecapPeriodInfo Current,
        RecapPeriodInfo Previous,
        HashSet<string> Covered,
        IReadOnlyList<string> Late);

    // The business names of the endpoints and of the services the snapshot names.
    private sealed record Labels(
        IReadOnlyDictionary<(string Service, string Endpoint), string> Endpoints,
        IReadOnlyDictionary<string, string> Services)
    {
        public string? Of(RecapFinding finding)
        {
            return Endpoints.GetValueOrDefault((finding.Service, finding.Endpoint));
        }

        public string? OfService(RecapFinding finding)
        {
            return OfService(finding.Service);
        }

        public string? OfService(string? service)
        {
            return service is null ? null : Services.GetValueOrDefault(service);
        }
    }

    private sealed record Carbon(IReadOnlyDictionary<string, Co2Estimate> PerFinding, Co2Estimate Total);
}
