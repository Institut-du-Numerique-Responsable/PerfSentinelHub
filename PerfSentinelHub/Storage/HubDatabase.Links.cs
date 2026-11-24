using System.Text.Json;
using Microsoft.Data.Sqlite;
using PerfSentinelHub.Api;
using PerfSentinelHub.Links;

namespace PerfSentinelHub.Storage;

public sealed partial class HubDatabase
{
    private const int BaselineDays = 7;
    private const long RecurrenceLookbackMs = 30 * DayMs;

    /// <summary>
    ///     Everything the link rules read about one incident, without the write gate. Null when the
    ///     incident is gone. A baseline day is covered when the source has a ledger row for it and
    ///     none of that day's rows has a gap or an overflow.
    /// </summary>
    public async Task<LinkContext?> LoadLinkContextAsync(
        IncidentKey key, IReadOnlyCollection<string> environmentSourceIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        string service, kind, incidentJson, findingsJson;
        long atMs, windowFromMs, windowToMs;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  SELECT service, kind, at_ms, window_from_ms, window_to_ms, incident_json, findings_json
                                  FROM incidents WHERE id = $id AND source_id = $source_id;
                                  """;
            command.Parameters.AddWithValue("$id", key.Id);
            command.Parameters.AddWithValue("$source_id", key.SourceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            service = reader.GetString(0);
            kind = reader.GetString(1);
            atMs = reader.GetInt64(2);
            windowFromMs = reader.GetInt64(3);
            windowToMs = reader.GetInt64(4);
            incidentJson = reader.GetString(5);
            findingsJson = reader.GetString(6);
        }

        var rows = FrozenRows(findingsJson).ToList();
        var wanted = rows.Select(r => r.Signature).ToHashSet(StringComparer.Ordinal);
        var (signatures, baselines, seenBefore) =
            await LoadBaselinesAsync(connection, key.SourceId, atMs / DayMs, rows, wanted, cancellationToken);
        var recurrences = await LoadRecurrencesAsync(
            connection, key, environmentSourceIds, service, kind, atMs, wanted, cancellationToken);

        return new LinkContext(service, kind, atMs, windowFromMs, windowToMs, SaturationOf(incidentJson),
            signatures, baselines, recurrences, seenBefore, findingsJson);
    }

    // The daemon sends a ratio in [0, 1]; any other value is a malformed push and reads as none.
    private static double? SaturationOf(string incidentJson)
    {
        try
        {
            using var document = JsonDocument.Parse(incidentJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("saturation", out var value)
                   && value.ValueKind == JsonValueKind.Number
                   && value.GetDouble() is >= 0 and <= 1
                ? value.GetDouble()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Rows with a missing or empty signature are skipped; a malformed document reads as no rows. A row's
    // grouping is the value of its first grouping attribute, the one the daemon files it under, empty when none.
    private static IEnumerable<FrozenRow> FrozenRows(string findingsJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(findingsJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                yield break;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("finding", out var finding)
                    || finding.ValueKind != JsonValueKind.Object)
                    continue;
                var signature = finding.TryGetProperty("signature", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString()
                    : null;
                if (string.IsNullOrEmpty(signature))
                    continue;
                var type = finding.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? ""
                    : "";
                var grouping = finding.TryGetProperty("grouping", out var g) && g.ValueKind == JsonValueKind.Array
                               && g.GetArrayLength() > 0 && g[0].ValueKind == JsonValueKind.Object
                               && g[0].TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? ""
                    : "";
                // A row without a seen count was seen once, as the daemon defaults it.
                yield return new FrozenRow(signature, grouping, type, LongOf(row, "seen_count", 1),
                    LongOf(row, "first_seen_ms", 0));
            }
        }
    }

    private static long LongOf(JsonElement row, string name, long fallback) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : fallback;

    private readonly record struct FrozenRow(string Signature, string Grouping, string Type, long Seen, long First);

    // A covered day is split when every ledger of the source sent it split: only then do its grouping rows
    // hold the whole day of a grouping. A row with a grouping reads its own history, one without the
    // signature's sum. One day set serves the whole incident, so its shares divide sums over the same days:
    // the split covered days when any signature reads per grouping, every covered day otherwise.
    private static async Task<(List<FrozenSignature>, Dictionary<string, Baseline>, HashSet<string>)> LoadBaselinesAsync(
        SqliteConnection connection, string sourceId, long day, List<FrozenRow> rows, HashSet<string> wanted,
        CancellationToken cancellationToken)
    {
        var covered = new Dictionary<long, bool>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  SELECT d.day, min(m.day IS NOT NULL) FROM detection_days AS d
                                  LEFT JOIN detection_split_days AS m
                                    ON m.source_id = d.source_id AND m.ledger_id = d.ledger_id AND m.day = d.day
                                  WHERE d.source_id = $source_id AND d.day BETWEEN $from_day AND $to_day
                                  GROUP BY d.day
                                  HAVING sum(d.gap <> 0 OR d.overflow_detections > 0) = 0;
                                  """;
            command.Parameters.AddWithValue("$source_id", sourceId);
            command.Parameters.AddWithValue("$from_day", day - BaselineDays);
            command.Parameters.AddWithValue("$to_day", day - 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                covered[reader.GetInt64(0)] = reader.GetInt64(1) == 1;
        }

        var whole = new Dictionary<(string, long), long>();
        var byGrouping = new Dictionary<(string, string, long), long>();
        var seenBefore = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  SELECT signature, grouping, day, sum(detections) FROM detection_counts
                                  WHERE signature IN (SELECT value FROM json_each($wanted))
                                    AND source_id = $source_id AND day BETWEEN $from_day AND $to_day
                                    AND detections > 0
                                  GROUP BY signature, grouping, day;
                                  """;
            command.Parameters.AddWithValue("$wanted",
                JsonSerializer.Serialize([.. wanted], HubJsonContext.Default.StringArray));
            command.Parameters.AddWithValue("$source_id", sourceId);
            command.Parameters.AddWithValue("$from_day", day - BaselineDays);
            command.Parameters.AddWithValue("$to_day", day - 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var (signature, grouping, countDay, detections) =
                    (reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3));
                seenBefore.Add(signature);
                whole[(signature, countDay)] = whole.GetValueOrDefault((signature, countDay)) + detections;
                byGrouping[(signature, grouping, countDay)] = detections;
            }
        }

        // A signature also frozen without a grouping reads as a whole: its grouped rows are in that sum already.
        var groups = rows.GroupBy(r => r.Signature, StringComparer.Ordinal)
            .Select(g => (Rows: g, AsWhole: g.Any(r => r.Grouping.Length == 0))).ToList();
        var anyGrouped = groups.Any(g => !g.AsWhole);
        // A split day's whole sum is still the complete signature, so a whole read keeps it.
        var days = covered.Where(d => !anyGrouped || d.Value).Select(d => d.Key).ToList();
        var signatures = new List<FrozenSignature>();
        var baselines = new Dictionary<string, Baseline>(StringComparer.Ordinal);
        foreach (var (group, asWhole) in groups)
        {
            var signature = group.Key;
            // Summed by hand, unchecked as before: a malformed seen_count must not fail the whole incident.
            var populations = new Dictionary<string, long>(StringComparer.Ordinal);
            long seen = 0, first = long.MaxValue;
            foreach (var row in group)
            {
                var population = asWhole ? "" : row.Grouping;
                populations[population] = populations.GetValueOrDefault(population) + row.Seen;
                seen += row.Seen;
                first = Math.Min(first, row.First);
            }

            long Count(string grouping, long countDay) => asWhole
                ? whole.GetValueOrDefault((signature, countDay))
                : byGrouping.GetValueOrDefault((signature, grouping, countDay));

            long detections = 0, knownSeen = 0;
            foreach (var (grouping, populationSeen) in populations)
            {
                long sum = 0;
                foreach (var countDay in days)
                    sum += Count(grouping, countDay);
                detections += sum;
                if (sum > 0) knownSeen += populationSeen;
            }

            var daysSeen = days.Count(d => populations.Keys.Any(grouping => Count(grouping, d) > 0));
            signatures.Add(new FrozenSignature(signature, group.First().Type, seen, first, knownSeen));
            baselines[signature] = new Baseline(days.Count, daysSeen, detections);
        }

        return (signatures, baselines, seenBefore);
    }

    // incident_signatures holds each copy's signatures, kept by the triggers of Schema.V24.
    private const string RecurrenceSql = $"""
        SELECT count(DISTINCT id) FROM incidents AS i WHERE {EarlierIncidentsWhere};
        SELECT s.signature, count(DISTINCT i.id)
        FROM incidents AS i
        JOIN incident_signatures AS s ON s.incident_id = i.id AND s.source_id = i.source_id
        WHERE {EarlierIncidentsWhere} AND s.signature IN (SELECT value FROM json_each($wanted))
        GROUP BY s.signature;
        """;

    private const string EarlierIncidentsWhere = """
                                                 i.source_id IN (SELECT value FROM json_each($source_ids))
                                                   AND i.service = $service AND i.kind = $kind
                                                   AND i.at_ms >= $from_ms AND i.at_ms < $at_ms AND i.id <> $id
                                                 """;

    // By incident id: the same id stored by two sources is one incident, whose signatures are the union.
    private static async Task<Dictionary<string, Recurrence>> LoadRecurrencesAsync(
        SqliteConnection connection, IncidentKey key, IReadOnlyCollection<string> environmentSourceIds,
        string service, string kind, long atMs, HashSet<string> wanted, CancellationToken cancellationToken)
    {
        var withSignature = new Dictionary<string, int>(StringComparer.Ordinal);
        int earlier;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = RecurrenceSql;
            command.Parameters.AddWithValue("$source_ids",
                JsonSerializer.Serialize([.. environmentSourceIds], HubJsonContext.Default.StringArray));
            command.Parameters.AddWithValue("$wanted",
                JsonSerializer.Serialize([.. wanted], HubJsonContext.Default.StringArray));
            command.Parameters.AddWithValue("$service", service);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$from_ms", atMs - RecurrenceLookbackMs);
            command.Parameters.AddWithValue("$at_ms", atMs);
            command.Parameters.AddWithValue("$id", key.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            earlier = reader.GetInt32(0);
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                withSignature[reader.GetString(0)] = reader.GetInt32(1);
        }

        return wanted.ToDictionary(s => s, s => new Recurrence(earlier, withSignature.GetValueOrDefault(s)),
            StringComparer.Ordinal);
    }

    /// <summary>Replaces one incident's links in a single transaction, under the write gate.</summary>
    public Task ReplaceLinksAsync(
        IncidentKey key, IReadOnlyList<StoredLink> links, CancellationToken cancellationToken) =>
        ReplaceLinksAsync(key, links, null, cancellationToken);

    /// <summary>
    ///     As above, but writes nothing when the copy no longer holds <paramref name="assessedFindingsJson" />:
    ///     the upsert that replaced them dropped the links and queued the copy again.
    /// </summary>
    public async Task ReplaceLinksAsync(
        IncidentKey key, IReadOnlyList<StoredLink> links, string? assessedFindingsJson,
        CancellationToken cancellationToken)
    {
        var reasonsJson = links.Select(link =>
            JsonSerializer.Serialize(link.Reasons, HubJsonContext.Default.IReadOnlyListLinkReason)).ToArray();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(false);
            if (assessedFindingsJson is not null)
            {
                await using var check = connection.CreateCommand();
                check.Transaction = transaction;
                check.CommandText = """
                                    SELECT 1 FROM incidents
                                    WHERE id = $id AND source_id = $source_id AND findings_json = $findings_json;
                                    """;
                check.Parameters.AddWithValue("$id", key.Id);
                check.Parameters.AddWithValue("$source_id", key.SourceId);
                check.Parameters.AddWithValue("$findings_json", assessedFindingsJson);
                if (await check.ExecuteScalarAsync(cancellationToken) is null)
                    return;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM incident_finding_links WHERE incident_id = $id AND source_id = $source_id;";
            command.Parameters.AddWithValue("$id", key.Id);
            command.Parameters.AddWithValue("$source_id", key.SourceId);
            await command.ExecuteNonQueryAsync(cancellationToken);

            if (links.Count > 0)
            {
                // From the incident row, so a copy purged since its context was loaded gets no links.
                command.CommandText = """
                                      INSERT INTO incident_finding_links(
                                        incident_id, source_id, signature, level, level_rank, reasons_json, rules_version)
                                      SELECT $id, $source_id, $signature, $level, $level_rank, $reasons_json, $rules_version
                                      FROM incidents WHERE id = $id AND source_id = $source_id;
                                      """;
                var signature = command.Parameters.Add("$signature", SqliteType.Text);
                var level = command.Parameters.Add("$level", SqliteType.Text);
                var levelRank = command.Parameters.Add("$level_rank", SqliteType.Integer);
                var reasons = command.Parameters.Add("$reasons_json", SqliteType.Text);
                var rulesVersion = command.Parameters.Add("$rules_version", SqliteType.Integer);
                for (var i = 0; i < links.Count; i++)
                {
                    var link = links[i];
                    signature.Value = link.Signature;
                    level.Value = link.Level.ToString().ToLowerInvariant();
                    levelRank.Value = (int)link.Level;
                    reasons.Value = reasonsJson[i];
                    rulesVersion.Value = link.RulesVersion;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredLink>> ListLinksAsync(IncidentKey key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT signature, level_rank, reasons_json, rules_version FROM incident_finding_links
                              WHERE incident_id = $id AND source_id = $source_id ORDER BY signature;
                              """;
        command.Parameters.AddWithValue("$id", key.Id);
        command.Parameters.AddWithValue("$source_id", key.SourceId);
        var links = new List<StoredLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            links.Add(new StoredLink(
                reader.GetString(0),
                (LinkLevel)reader.GetInt32(1),
                JsonSerializer.Deserialize(reader.GetString(2), HubJsonContext.Default.IReadOnlyListLinkReason) ?? [],
                reader.GetInt32(3)));
        return links;
    }

    private const string IncidentsNeedingLinksSql = """
        SELECT i.id, i.source_id FROM incidents AS i
        WHERE i.finding_count > 0 AND i.source_id IN (SELECT value FROM json_each($source_ids))
          AND NOT EXISTS (
            SELECT 1 FROM incident_finding_links AS l
            WHERE l.incident_id = i.id AND l.source_id = i.source_id AND l.rules_version >= $rules_version)
          AND EXISTS (
            SELECT 1 FROM incident_signatures AS s WHERE s.incident_id = i.id AND s.source_id = i.source_id);
        """;

    /// <summary>
    ///     Incidents of <paramref name="sourceIds" /> with no links at all, or links computed by older rules.
    ///     An incident none of whose frozen rows has a signature never gets a link, so it is left out, as
    ///     is a source no longer configured: queueing either would repeat at every catch-up.
    /// </summary>
    public async Task<IReadOnlyList<IncidentKey>> IncidentsNeedingLinksAsync(
        int rulesVersion, IReadOnlyCollection<string> sourceIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = IncidentsNeedingLinksSql;
        command.Parameters.AddWithValue("$rules_version", rulesVersion);
        command.Parameters.AddWithValue("$source_ids",
            JsonSerializer.Serialize([.. sourceIds], HubJsonContext.Default.StringArray));
        var keys = new List<IncidentKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            keys.Add(new IncidentKey(reader.GetString(0), reader.GetString(1)));
        return keys;
    }
}
