using PerfSentinelHub.Access;

namespace PerfSentinelHub.Storage;

/// <summary>One incident copy of a recap read, with one of its links when it has any (Signature null otherwise).</summary>
public sealed record RecapIncidentRow(
    string Id, string SourceId, string Kind, string Service, long AtMs, string? Signature, int LevelRank);

// The incidents a recap counts: the same sources, services, namespaces and access as its findings.
public sealed partial class HubDatabase
{
    // Declared after IncidentAccess: static fields initialize in order.
    private static readonly string RecapIncidentSql = $"""
        SELECT incidents.id, incidents.source_id, incidents.kind, incidents.service, incidents.at_ms,
               l.signature, l.level_rank
        FROM incidents
        LEFT JOIN incident_finding_links AS l
          ON l.incident_id = incidents.id AND l.source_id = incidents.source_id AND l.level_rank >= $min_rank
         AND ($signatures IS NULL OR l.signature IN (SELECT value FROM json_each($signatures)))
        WHERE incidents.at_ms BETWEEN $from_ms AND $to_ms
          AND incidents.source_id IN (SELECT value FROM json_each($source_ids))
          AND ($services IS NULL OR incidents.service IN (SELECT value FROM json_each($services)))
          AND ($excluded IS NULL OR incidents.service NOT IN (SELECT value FROM json_each($excluded)))
          AND ($groupings IS NULL
               OR json_extract(incidents.incident_json, '$.namespace') IN (SELECT value FROM json_each($groupings)))
        """;

    private static readonly string ScopedRecapIncidentSql =
        $"WITH {AccessPatternsCte}\n{RecapIncidentSql} AND {IncidentAccess}";

    /// <summary>
    ///     The incidents of a window within <paramref name="sourceIds" />, one row per link of at least
    ///     <paramref name="minRank" /> (and in <paramref name="signatures" /> when given), or one bare row
    ///     for an incident with none. An empty signature list reads the incidents alone.
    /// </summary>
    public async Task<IReadOnlyList<RecapIncidentRow>> ReadRecapIncidentsAsync(
        long fromMs,
        long toMs,
        IReadOnlyList<string> sourceIds,
        IReadOnlyList<string>? services,
        IReadOnlyList<string>? excludedServices,
        IReadOnlyList<string>? groupings,
        AccessScope? access,
        int minRank,
        IReadOnlyList<string>? signatures,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = access is null ? RecapIncidentSql : ScopedRecapIncidentSql;
        BindAccess(command, access);
        command.Parameters.AddWithValue("$from_ms", fromMs);
        command.Parameters.AddWithValue("$to_ms", toMs);
        command.Parameters.AddWithValue("$min_rank", minRank);
        command.Parameters.AddWithValue("$source_ids", JsonArray(sourceIds));
        command.Parameters.AddWithValue("$services", OptionalJson(services));
        command.Parameters.AddWithValue("$excluded", OptionalJson(excludedServices));
        command.Parameters.AddWithValue("$groupings", OptionalJson(groupings));
        command.Parameters.AddWithValue("$signatures", OptionalJson(signatures));

        var rows = new List<RecapIncidentRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new RecapIncidentRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt32(6)));
        return rows;
    }

    private static object OptionalJson(IReadOnlyList<string>? values)
    {
        return values is null ? DBNull.Value : JsonArray(values);
    }
}
