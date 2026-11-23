using System.Text.Json;
using Microsoft.Data.Sqlite;
using PerfSentinelHub.Api;
using PerfSentinelHub.Links;

namespace PerfSentinelHub.Storage;

public sealed record IncidentKey(string Id, string SourceId);

/// <summary>The frozen rows of one signature, summed: empty signatures are dropped before this.</summary>
public sealed record FrozenSignature(string Signature, string FindingType, long SeenCount, long FirstSeenMs);

public sealed record LinkContext(
    string Service,
    string Kind,
    long AtMs,
    long WindowFromMs,
    long WindowToMs,
    double? Saturation,
    IReadOnlyList<FrozenSignature> Signatures,
    IReadOnlyDictionary<string, Baseline> Baselines,
    IReadOnlyDictionary<string, Recurrence> Recurrences,
    IReadOnlySet<string> SeenBeforeDeploy);

public sealed record StoredLink(string Signature, LinkLevel Level, IReadOnlyList<LinkReason> Reasons, int RulesVersion);

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

        var signatures = FrozenSignatures(findingsJson);
        var wanted = signatures.Select(s => s.Signature).ToHashSet(StringComparer.Ordinal);
        var (baselines, seenBefore) = await LoadBaselinesAsync(connection, key.SourceId, atMs / DayMs, wanted, cancellationToken);
        var recurrences = await LoadRecurrencesAsync(
            connection, key, environmentSourceIds, service, kind, atMs, wanted, cancellationToken);

        return new LinkContext(service, kind, atMs, windowFromMs, windowToMs, SaturationOf(incidentJson),
            signatures, baselines, recurrences, seenBefore);
    }

    private static double? SaturationOf(string incidentJson)
    {
        try
        {
            using var document = JsonDocument.Parse(incidentJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("saturation", out var value)
                   && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<FrozenSignature> FrozenSignatures(string findingsJson)
    {
        var rows = new Dictionary<string, FrozenSignature>(StringComparer.Ordinal);
        foreach (var (signature, type, seen, first) in FrozenRows(findingsJson))
        {
            rows[signature] = rows.TryGetValue(signature, out var existing)
                ? existing with { SeenCount = existing.SeenCount + seen, FirstSeenMs = Math.Min(existing.FirstSeenMs, first) }
                : new FrozenSignature(signature, type, seen, first);
        }

        return [.. rows.Values];
    }

    // Rows with a missing or empty signature are skipped; a malformed document reads as no rows.
    private static IEnumerable<(string Signature, string Type, long Seen, long First)> FrozenRows(string findingsJson)
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
                // A row without a seen count was seen once, as the daemon defaults it.
                yield return (signature, type, LongOf(row, "seen_count", 1), LongOf(row, "first_seen_ms", 0));
            }
        }
    }

    private static long LongOf(JsonElement row, string name, long fallback) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : fallback;

    private static async Task<(Dictionary<string, Baseline>, HashSet<string>)> LoadBaselinesAsync(
        SqliteConnection connection, string sourceId, long day, HashSet<string> wanted,
        CancellationToken cancellationToken)
    {
        var covered = new HashSet<long>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  SELECT day FROM detection_days
                                  WHERE source_id = $source_id AND day BETWEEN $from_day AND $to_day
                                  GROUP BY day
                                  HAVING sum(gap <> 0 OR overflow_detections > 0) = 0;
                                  """;
            command.Parameters.AddWithValue("$source_id", sourceId);
            command.Parameters.AddWithValue("$from_day", day - BaselineDays);
            command.Parameters.AddWithValue("$to_day", day - 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                covered.Add(reader.GetInt64(0));
        }

        var daysSeen = wanted.ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
        var detections = wanted.ToDictionary(s => s, _ => 0L, StringComparer.Ordinal);
        var seenBefore = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  SELECT signature, day, sum(detections) FROM detection_counts
                                  WHERE signature IN (SELECT value FROM json_each($wanted))
                                    AND source_id = $source_id AND day BETWEEN $from_day AND $to_day
                                    AND detections > 0
                                  GROUP BY signature, day;
                                  """;
            command.Parameters.AddWithValue("$wanted",
                JsonSerializer.Serialize([.. wanted], HubJsonContext.Default.StringArray));
            command.Parameters.AddWithValue("$source_id", sourceId);
            command.Parameters.AddWithValue("$from_day", day - BaselineDays);
            command.Parameters.AddWithValue("$to_day", day - 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var signature = reader.GetString(0);
                seenBefore.Add(signature);
                if (!covered.Contains(reader.GetInt64(1)))
                    continue;
                daysSeen[signature]++;
                detections[signature] += reader.GetInt64(2);
            }
        }

        return (wanted.ToDictionary(s => s, s => new Baseline(covered.Count, daysSeen[s], detections[s]),
            StringComparer.Ordinal), seenBefore);
    }

    private static async Task<Dictionary<string, Recurrence>> LoadRecurrencesAsync(
        SqliteConnection connection, IncidentKey key, IReadOnlyCollection<string> environmentSourceIds,
        string service, string kind, long atMs, HashSet<string> wanted, CancellationToken cancellationToken)
    {
        // By incident id: the same id stored by two sources is one incident, whose signatures are the union.
        var earlier = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                                  SELECT id, findings_json FROM incidents
                                  WHERE source_id IN (SELECT value FROM json_each($source_ids))
                                    AND service = $service AND kind = $kind
                                    AND at_ms >= $from_ms AND at_ms < $at_ms AND id <> $id;
                                  """;
            command.Parameters.AddWithValue("$source_ids",
                JsonSerializer.Serialize([.. environmentSourceIds], HubJsonContext.Default.StringArray));
            command.Parameters.AddWithValue("$service", service);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$from_ms", atMs - RecurrenceLookbackMs);
            command.Parameters.AddWithValue("$at_ms", atMs);
            command.Parameters.AddWithValue("$id", key.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!earlier.TryGetValue(reader.GetString(0), out var carried))
                    earlier[reader.GetString(0)] = carried = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in FrozenRows(reader.GetString(1)))
                    carried.Add(row.Signature);
            }
        }

        return wanted.ToDictionary(s => s, s => new Recurrence(earlier.Count, earlier.Values.Count(c => c.Contains(s))),
            StringComparer.Ordinal);
    }

    /// <summary>Replaces one incident's links in a single transaction, under the write gate.</summary>
    public async Task ReplaceLinksAsync(
        IncidentKey key, string environment, IReadOnlyList<StoredLink> links, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM incident_finding_links WHERE incident_id = $id AND source_id = $source_id;";
            command.Parameters.AddWithValue("$id", key.Id);
            command.Parameters.AddWithValue("$source_id", key.SourceId);
            await command.ExecuteNonQueryAsync(cancellationToken);

            if (links.Count > 0)
            {
                // The incident's own columns are copied so the recap never joins back for them.
                command.CommandText = """
                                      INSERT INTO incident_finding_links(
                                        incident_id, source_id, signature, level, level_rank, reasons_json, rules_version,
                                        incident_at_ms, service, kind, environment)
                                      SELECT $id, $source_id, $signature, $level, $level_rank, $reasons_json, $rules_version,
                                             at_ms, service, kind, $environment
                                      FROM incidents WHERE id = $id AND source_id = $source_id;
                                      """;
                command.Parameters.AddWithValue("$environment", environment);
                var signature = command.Parameters.Add("$signature", SqliteType.Text);
                var level = command.Parameters.Add("$level", SqliteType.Text);
                var levelRank = command.Parameters.Add("$level_rank", SqliteType.Integer);
                var reasons = command.Parameters.Add("$reasons_json", SqliteType.Text);
                var rulesVersion = command.Parameters.Add("$rules_version", SqliteType.Integer);
                foreach (var link in links)
                {
                    signature.Value = link.Signature;
                    level.Value = link.Level.ToString().ToLowerInvariant();
                    levelRank.Value = (int)link.Level;
                    reasons.Value = JsonSerializer.Serialize(link.Reasons, HubJsonContext.Default.IReadOnlyListLinkReason);
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

    /// <summary>Incidents with no links at all, or links computed by older rules.</summary>
    public async Task<IReadOnlyList<IncidentKey>> IncidentsNeedingLinksAsync(int rulesVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT i.id, i.source_id FROM incidents AS i
                              WHERE NOT EXISTS (
                                SELECT 1 FROM incident_finding_links AS l
                                WHERE l.incident_id = i.id AND l.source_id = i.source_id AND l.rules_version >= $rules_version);
                              """;
        command.Parameters.AddWithValue("$rules_version", rulesVersion);
        var keys = new List<IncidentKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            keys.Add(new IncidentKey(reader.GetString(0), reader.GetString(1)));
        return keys;
    }
}
