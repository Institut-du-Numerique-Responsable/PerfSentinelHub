using System.Text.Json;
using Microsoft.Data.Sqlite;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Storage;

// The access fragments: AccessPattern.Reads in SQL, assembled from fixed column names only.
// A statement that uses one starts with AccessPatternsCte and binds $access_patterns to
// AccessScope.ToJson(). Every part compares with = or substr, never LIKE or GLOB, which fold
// ASCII case and give '%', '_', '?' and '[' a meaning. A NULL service, a copy with no
// envelope of its own, fails both service branches.
public sealed partial class HubDatabase
{
    // Decodes the patterns once per statement, rather than once per row and pattern.
    public const string AccessPatternsCte = """
                                            ap AS MATERIALIZED (
                                              SELECT json_extract(value, '$.source') AS source,
                                                     json_extract(value, '$.source_prefix') AS source_prefix,
                                                     json_extract(value, '$.service') AS service,
                                                     json_extract(value, '$.service_prefix') AS service_prefix,
                                                     json_extract(value, '$.key') AS key,
                                                     json_extract(value, '$.value') AS value,
                                                     json_extract(value, '$.value_prefix') AS value_prefix
                                              FROM json_each($access_patterns))
                                            """;

    // The finding copies a scope reads, within the $source_ids a read already names. The
    // service is the copy's own, never findings.service, which any import key rewrites.
    public static readonly string VisibleCopiesCte = $"""
                                                      visible AS MATERIALIZED (
                                                        SELECT fs.signature, fs.source_id
                                                        FROM finding_sources AS fs
                                                        WHERE ($source_ids IS NULL OR fs.source_id {InScope})
                                                          AND {FindingAccess("fs.source_id", "fs.signature", "fs.service")})
                                                      """;

    // Built once, so the command takes a field rather than a string formatted at the call.
    private static readonly string UnmatchedSql = $"""
                                                   WITH {AccessPatternsCte},
                                                   unmatched AS MATERIALIZED (
                                                     SELECT fs.signature, fs.source_id, fs.service FROM finding_sources AS fs
                                                     WHERE fs.last_seen_ms >= $since
                                                       AND NOT {FindingAccess("fs.source_id", "fs.signature", "fs.service")})
                                                   SELECT * FROM (
                                                     SELECT 'service', service, NULL, group_concat(DISTINCT source_id), count(*) AS n
                                                     FROM unmatched GROUP BY service ORDER BY n DESC, service LIMIT $limit)
                                                   UNION ALL
                                                   SELECT * FROM (
                                                     SELECT 'attribute', attr_key, attr_value, NULL, count(*) AS n FROM (
                                                       SELECT DISTINCT u.signature, u.source_id, fa.attr_key, fa.attr_value
                                                       FROM unmatched AS u JOIN finding_attributes AS fa
                                                         ON fa.signature = u.signature AND fa.source_id = u.source_id
                                                       WHERE fa.attr_key <> '')
                                                     GROUP BY attr_key, attr_value ORDER BY n DESC, attr_key, attr_value LIMIT $limit)
                                                   UNION ALL
                                                   SELECT 'source', source_id, NULL, NULL, count(*) FROM unmatched GROUP BY source_id;
                                                   """;

    private static void BindAccess(SqliteCommand command, AccessScope? access)
    {
        if (access is not null)
            command.Parameters.AddWithValue("$access_patterns", access.ToJson());
    }

    /// <summary>
    ///     Every Hub copy of a service in an environment, with its attribute rows: the only
    ///     evidence of which team a service belongs to when a trace backend is asked about
    ///     it, since the engine exports no grouping for a span.
    /// </summary>
    public async Task<IReadOnlyList<AccessCopy>> ReadServiceCopiesAsync(
        string environment,
        string service,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT fs.signature, fs.source_id,
                                     fa.grouping_key, fa.grouping_value, fa.attr_key, fa.attr_value
                              FROM finding_sources AS fs
                              LEFT JOIN finding_attributes AS fa
                                ON fa.signature = fs.signature AND fa.source_id = fs.source_id
                              WHERE fs.environment = $environment AND fs.service = $service
                              ORDER BY fs.signature, fs.source_id;
                              """;
        command.Parameters.AddWithValue("$environment", environment);
        command.Parameters.AddWithValue(ServiceParameter, service);

        var copies = new Dictionary<(string, string), List<AccessAttribute>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!copies.TryGetValue(key, out var rows))
                copies[key] = rows = [];
            if (!reader.IsDBNull(2))
                rows.Add(new AccessAttribute(reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5)));
        }

        return [.. copies.Select(copy => new AccessCopy(copy.Key.Item2, service, copy.Value))];
    }

    /// <summary>
    ///     The copies seen since <paramref name="sinceMs" /> that no pattern of
    ///     <paramref name="projects" /> reads, grouped by service and by captured pair, each
    ///     ordered by count then name and cut at <paramref name="limit" />, with every
    ///     source's count of them. One statement; an admin's page load is its only caller.
    /// </summary>
    public async Task<UnmatchedCopies> QueryUnmatchedAsync(
        AccessScope projects,
        long sinceMs,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = UnmatchedSql;
        command.Parameters.AddWithValue("$access_patterns", projects.ToJson());
        command.Parameters.AddWithValue("$since", sinceMs);
        command.Parameters.AddWithValue("$limit", limit);

        var services = new List<UnmatchedService>();
        var attributes = new List<UnmatchedAttribute>();
        var sources = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var count = reader.GetInt32(4);
            switch (reader.GetString(0))
            {
                case "service":
                    // Source ids are [A-Za-z0-9._-], so a comma never belongs to one.
                    services.Add(new UnmatchedService(
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        [.. reader.GetString(3).Split(',').Order(StringComparer.Ordinal)],
                        count));
                    break;
                case "attribute":
                    attributes.Add(new UnmatchedAttribute(reader.GetString(1), reader.GetString(2), count));
                    break;
                default:
                    sources[reader.GetString(1)] = count;
                    break;
            }
        }

        return new UnmatchedCopies(services, attributes, sources);
    }

    /// <summary>
    ///     The copies seen since <paramref name="sinceMs" /> on which a pattern keyed like
    ///     one of <paramref name="projects" /> reads but cannot write: the key carries two
    ///     values, or some grouping identity, the no-grouping marker included, lacks it.
    ///     Ordered by source then signature, cut at <paramref name="limit" />.
    /// </summary>
    public async Task<IReadOnlyList<AccessConflict>> QueryConflictsAsync(
        AccessScope projects,
        long sinceMs,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                               WITH {AccessPatternsCte},
                               k(key) AS (SELECT DISTINCT key FROM ap WHERE key IS NOT NULL),
                               c AS MATERIALIZED (
                                 SELECT fs.signature, fs.source_id, fs.service, k.key,
                                        (SELECT json_group_array(DISTINCT fa.attr_value) FROM finding_attributes AS fa
                                         WHERE fa.signature = fs.signature AND fa.source_id = fs.source_id
                                           AND fa.attr_key = k.key) AS vals,
                                        (SELECT count(DISTINCT json_array(fa.grouping_key, fa.grouping_value))
                                         FROM finding_attributes AS fa
                                         WHERE fa.signature = fs.signature AND fa.source_id = fs.source_id) AS identities,
                                        (SELECT count(DISTINCT json_array(fa.grouping_key, fa.grouping_value))
                                         FROM finding_attributes AS fa
                                         WHERE fa.signature = fs.signature AND fa.source_id = fs.source_id
                                           AND fa.attr_key = k.key) AS keyed
                                 FROM finding_sources AS fs, k
                                 WHERE fs.last_seen_ms >= $since)
                               SELECT signature, source_id, service, key, vals, identities - keyed FROM c
                               WHERE keyed > 0 AND (json_array_length(vals) > 1 OR identities > keyed)
                               ORDER BY source_id, signature, key
                               LIMIT $limit;
                               """;
        command.Parameters.AddWithValue("$access_patterns", projects.ToJson());
        command.Parameters.AddWithValue("$since", sinceMs);
        command.Parameters.AddWithValue("$limit", limit);

        var conflicts = new List<AccessConflict>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var values = JsonDocument.Parse(reader.GetString(4));
            conflicts.Add(new AccessConflict(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                [
                    .. values.RootElement.EnumerateArray().Select(value => value.GetString()!)
                        .Order(StringComparer.Ordinal)
                ],
                reader.GetInt32(5)));
        }

        return conflicts;
    }

    // A finding copy, on every pair it carried at that source within retention, whatever
    // grouping it came with. H1's ('', '', '', '') marker matches no key. Private, like Access:
    // both paste their arguments into SQL, so they take only the column names written here.
    private static string FindingAccess(string source, string signature, string service)
    {
        return Access(
            source,
            service,
            $"""
             SELECT 1 FROM finding_attributes AS fa
             WHERE fa.signature = {signature} AND fa.source_id = {source} AND fa.attr_key = ap.key
             """,
            "fa.attr_value");
    }

    private static string Access(string source, string service, string attributes, string value)
    {
#pragma warning disable S2077
        return $"""
                EXISTS (
                  SELECT 1 FROM ap
                  WHERE {Matches("source", source)}
                    AND {Matches("service", service)}
                    AND (ap.key IS NULL OR EXISTS (
                          {attributes}
                            AND {Compare("value", value)})))
                """;
#pragma warning restore S2077
    }

    private static string Matches(string part, string column)
    {
        return $"(ap.{part} IS NULL OR {Compare(part, column)})";
    }

    private static string Compare(string part, string column)
    {
        return $"CASE ap.{part}_prefix WHEN 1 THEN substr({column}, 1, length(ap.{part})) = ap.{part} " +
               $"ELSE {column} = ap.{part} END";
    }
}
