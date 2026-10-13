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

    // An incident copy, on the pairs every frozen finding carries. The ('', '') marker
    // H1 writes for a copy with none matches no key.
    public static readonly string IncidentAccess = Access(
        "incidents.source_id",
        "incidents.service",
        """
        SELECT 1 FROM incident_attributes AS ia
        WHERE ia.id = incidents.id AND ia.source_id = incidents.source_id AND ia.attr_key = ap.key
        """,
        "ia.attr_value");

    // The leading CTE and the incident predicate, empty for an unrestricted read so its SQL
    // stays today's.
    private static string AccessWith(AccessScope? access)
    {
        return access is null ? "" : $"WITH {AccessPatternsCte}\n";
    }

    private static string AccessAnd(AccessScope? access)
    {
        return access is null ? "" : $" AND {IncidentAccess}";
    }

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
                rows.Add(new AccessAttribute(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }

        return [.. copies.Select(copy => new AccessCopy(copy.Key.Item2, service, copy.Value))];
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
