using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

/// <summary>
///     One sample of perf_sentinel_hub_findings. The environment is configured,
///     the type and the severity are already folded into their closed vocabularies.
/// </summary>
public sealed record FindingSeries(string Environment, FindingCount Findings);

/// <summary>
///     The findings family of /metrics, counted at most once per scrape interval.
///     The route is anonymous and ungated and a scoped count reads its whole
///     scope, so an uncached one would let any caller make the Hub count at will.
///     The count grows with the signatures stored, so a scrape waits for it only
///     as long as <see cref="CachedCount{T}" /> allows and is otherwise served the
///     previous one, its age published beside it. Every other family is cheap and
///     stays computed at scrape time.
/// </summary>
public sealed class FindingMetrics
{
    private readonly CachedCount<IReadOnlyList<FindingSeries>> _cache;
    private readonly HubDatabase _database;
    private readonly IOptions<HubOptions> _options;

    public FindingMetrics(
        HubDatabase database,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        ILogger<FindingMetrics> logger)
    {
        _database = database;
        _options = options;
        _cache = new CachedCount<IReadOnlyList<FindingSeries>>(CountAsync, timeProvider, logger);
    }

    /// <summary>
    ///     One sample per environment, type, severity and status that holds a
    ///     finding, with the time it was counted. Null until a first count lands.
    ///     An environment is a group of `Hub:Sources`, never a stored row: that
    ///     column goes stale when a source changes environment.
    /// </summary>
    public Task<Counted<IReadOnlyList<FindingSeries>>?> SeriesAsync(CancellationToken cancellationToken)
    {
        return _cache.GetAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<FindingSeries>> CountAsync(CancellationToken cancellationToken)
    {
        var series = new List<FindingSeries>();
        var environments = _options.Value.Sources.GroupBy(source => source.Environment, StringComparer.Ordinal);
        foreach (var environment in environments)
        {
            var counts = await _database.CountFindingsAsync(
                [.. environment.Select(source => source.Id)], cancellationToken);
            // Two stored values can fold into one label, and a scrape that repeats
            // a series is rejected whole, so folded rows are summed.
            series.AddRange(counts
                .GroupBy(count => (
                    Type: FindingLabels.Fold(FindingLabels.Types, count.FindingType),
                    Severity: FindingLabels.Fold(FindingLabels.Severities, count.Severity),
                    count.Status))
                .Select(group => new FindingSeries(
                    environment.Key,
                    new FindingCount(
                        group.Key.Type, group.Key.Severity, group.Key.Status, group.Sum(count => count.Count)))));
        }

        return series;
    }
}
