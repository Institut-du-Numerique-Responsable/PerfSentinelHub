using PerfSentinelHub.Access;
using PerfSentinelHub.Api;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Storage;

// The names a recap offers to filter on: what the caller reads within the period, nothing else.
public sealed partial class HubDatabase
{
    // One pattern holds the copy's source and service and, when it has a key, its pair on a row of
    // this very namespace. A namespace with no such row fails closed: finding_groupings takes a value
    // finding_attributes refuses (no key, past 256 characters) or never held (older than that table).
    // Copies still comes first in the statement, for the $source_ids a read names.
    private static readonly string GroupingAccess = Access(
        "fs.source_id",
        "fs.service",
        """
        SELECT 1 FROM finding_attributes AS fa
        WHERE fa.signature = g.signature AND fa.source_id = g.source_id
          AND fa.grouping_value = g.grouping AND fa.attr_key = ap.key
        """,
        "fa.attr_value");

    /// <summary>
    ///     The services of the copies <paramref name="access" /> reads whose finding is in the window.
    ///     A copy's own service only: findings.service is the last writer's, whichever source that was.
    /// </summary>
    public Task<IReadOnlyList<string>> QueryRecapServicesAsync(
        RecapPeriod window,
        IReadOnlyList<string>? sourceIds,
        AccessScope? access,
        CancellationToken cancellationToken)
    {
        return ReadRecapNamesAsync(RecapServicesSql(access is not null), window, sourceIds, access, cancellationToken);
    }

    /// <summary>
    ///     The namespaces of those copies. A copy visible to a narrowed scope keeps every namespace it
    ///     was seen in, so each one must prove itself: see <see cref="GroupingAccess" />.
    /// </summary>
    public Task<IReadOnlyList<string>> QueryRecapGroupingsAsync(
        RecapPeriod window,
        IReadOnlyList<string>? sourceIds,
        AccessScope? access,
        CancellationToken cancellationToken)
    {
        return ReadRecapNamesAsync(RecapGroupingsSql(access is not null), window, sourceIds, access, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ReadRecapNamesAsync(
        string sql,
        RecapPeriod window,
        IReadOnlyList<string>? sourceIds,
        AccessScope? access,
        CancellationToken cancellationToken)
    {
        var parameters = new List<(string Name, object Value)>();
        var query = new FindingQuery(null, null, null, 0, FromMs: window.FromMs, ToMs: window.ToMs);
        AddVolumeWindow(parameters, query, AddWindow(parameters, query));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.Parameters.AddWithValue("$source_ids", sourceIds is null ? DBNull.Value : JsonArray(sourceIds));
        BindAccess(command, access);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }

    // The window judges the row's own copy, not any copy in scope as a findings read does. Copies inside
    // InWindow puts an IN list over the visible copies under each correlated probe: 13.7 s against 62 ms
    // for both name reads on 20,000 findings, narrowed to one source.
    private static string RecapServicesSql(bool access)
    {
#pragma warning disable S2077
        return $"""
                {RecapNamesWith(access)}
                SELECT DISTINCT fs.service FROM finding_sources AS fs
                WHERE fs.service IS NOT NULL AND {Copies(access, "fs")}
                  {InWindow("fs", "p.source_id = fs.source_id", "dc.source_id = fs.source_id")}
                ORDER BY fs.service;
                """;
#pragma warning restore S2077
    }

    private static string RecapGroupingsSql(bool access)
    {
#pragma warning disable S2077
        return $"""
                {RecapNamesWith(access)}
                SELECT DISTINCT g.grouping FROM finding_groupings AS g
                JOIN finding_sources AS fs ON fs.signature = g.signature AND fs.source_id = g.source_id
                WHERE {Copies(access, "g")}{(access ? $" AND {GroupingAccess}" : "")}
                  {InWindow("g", "p.source_id = g.source_id", "dc.source_id = g.source_id")}
                ORDER BY g.grouping;
                """;
#pragma warning restore S2077
    }

    private static string RecapNamesWith(bool access)
    {
        return access ? $"WITH {AccessPatternsCte}, {VisibleCopiesCte}" : "";
    }
}
