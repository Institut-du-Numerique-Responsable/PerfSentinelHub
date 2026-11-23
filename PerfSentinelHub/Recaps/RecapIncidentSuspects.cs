using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Recaps;

/// <summary>The signatures a period's incidents point at, from their stored links.</summary>
public static class RecapIncidentSuspects
{
    private const int StrongRank = 3;
    private const int MediumRank = 2;

    public sealed record Ranked(
        string Signature, int Strong, int Medium, IReadOnlyDictionary<string, int> StrongByKind);

    /// <summary>
    ///     Strong and medium links only, counted once per incident id (the highest level of its copies),
    ///     the most strong first, then the most medium, then by signature. Weak-only signatures drop out.
    /// </summary>
    public static List<Ranked> Rank(IReadOnlyList<RecapIncidentRow> rows)
    {
        return
        [
            .. rows.Where(row => row.Signature is not null && row.LevelRank >= MediumRank)
                .GroupBy(row => row.Signature!, StringComparer.Ordinal)
                .Select(signature =>
                {
                    var incidents = signature.GroupBy(row => row.Id, StringComparer.Ordinal)
                        .Select(group => (group.First().Kind, Rank: group.Max(row => row.LevelRank)))
                        .ToList();
                    var strong = incidents.Where(item => item.Rank >= StrongRank).ToList();
                    return new Ranked(
                        signature.Key,
                        strong.Count,
                        incidents.Count - strong.Count,
                        strong.GroupBy(item => item.Kind, StringComparer.Ordinal)
                            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));
                })
                .OrderByDescending(item => item.Strong)
                .ThenByDescending(item => item.Medium)
                .ThenBy(item => item.Signature, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     The suspects with their latest incident (a link of any level counts) and the words of their
    ///     finding; a finding the store no longer holds reads <paramref name="unknown" />.
    /// </summary>
    public static List<RecapSuspect> Named(
        IReadOnlyList<Ranked> kept,
        IReadOnlyList<RecapIncidentRow> linked,
        IReadOnlyDictionary<string, RecapFinding> findings,
        string unknown)
    {
        var rank = 0;
        return
        [
            .. kept.Select(item =>
            {
                var latest = linked.Where(row => row.Signature == item.Signature)
                    .OrderByDescending(row => row.AtMs)
                    .ThenBy(row => row.Id, StringComparer.Ordinal)
                    .ThenBy(row => row.SourceId, StringComparer.Ordinal)
                    .First();
                var finding = findings.GetValueOrDefault(item.Signature);
                return new RecapSuspect(
                    ++rank,
                    item.Signature,
                    finding?.FindingType ?? unknown,
                    finding?.Service ?? latest.Service,
                    string.IsNullOrEmpty(finding?.Endpoint) ? null : finding.Endpoint,
                    item.Strong,
                    item.Medium,
                    item.StrongByKind,
                    latest.Id,
                    latest.SourceId);
            })
        ];
    }
}
