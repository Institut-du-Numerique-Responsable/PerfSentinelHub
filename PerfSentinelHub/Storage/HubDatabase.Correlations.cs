using Microsoft.Data.Sqlite;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Storage;

public sealed partial class HubDatabase
{
    // The copy carrying the newer state of one episode: the later last co-occurrence, or,
    // at the same one, the copy that closed it. A close is queued at the last_seen of the
    // update before it, with rolling values that drifted since, so without the second
    // clause two copies of one moment would keep whichever arrived first.
    private const string EpisodeNewer = """
                                        (excluded.last_seen_ms > correlation_episodes.last_seen_ms
                                         OR (excluded.last_seen_ms = correlation_episodes.last_seen_ms
                                             AND excluded.ended_at_ms IS NOT NULL
                                             AND correlation_episodes.ended_at_ms IS NULL))
                                        """;

    // The merge rule, so two copies converge in either order: the earliest start, the latest
    // last co-occurrence and the highest peaks, the rolling values of the newer copy, an end
    // kept once seen. The sides never change, they are what pair_key hashes. SQLite reads
    // every column of the SET from the row as it was, so the order of the lines is free.
    private const string EpisodeUpsert = $"""
                                          INSERT INTO correlation_episodes(
                                            id, source_id, pair_key, source_type, source_service, source_template,
                                            source_grouping, source_grouping_value, target_type, target_service,
                                            target_template, target_grouping, target_grouping_value,
                                            started_at_ms, last_seen_ms, ended_at_ms, gap_ms,
                                            peak_confidence, peak_co_occurrences, last_confidence, last_co_occurrences,
                                            median_lag_ms, sample_trace_id, source_sample_trace_id, first_seen_ms,
                                            last_seen_hub_ms)
                                          VALUES (
                                            $id, $source_id, $pair_key, $source_type, $source_service, $source_template,
                                            $source_grouping, $source_grouping_value, $target_type, $target_service,
                                            $target_template, $target_grouping, $target_grouping_value,
                                            $started_at_ms, $last_seen_ms, $ended_at_ms, $gap_ms,
                                            $peak_confidence, $peak_co_occurrences, $last_confidence,
                                            $last_co_occurrences, $median_lag_ms, $sample_trace_id,
                                            $source_sample_trace_id, $observed_at, $observed_at)
                                          ON CONFLICT(id, source_id) DO UPDATE SET
                                            started_at_ms = MIN(correlation_episodes.started_at_ms, excluded.started_at_ms),
                                            last_seen_ms = MAX(correlation_episodes.last_seen_ms, excluded.last_seen_ms),
                                            ended_at_ms = COALESCE(correlation_episodes.ended_at_ms, excluded.ended_at_ms),
                                            peak_confidence = MAX(correlation_episodes.peak_confidence, excluded.peak_confidence),
                                            peak_co_occurrences = MAX(correlation_episodes.peak_co_occurrences,
                                                                      excluded.peak_co_occurrences),
                                            last_confidence = CASE WHEN {EpisodeNewer}
                                              THEN excluded.last_confidence ELSE correlation_episodes.last_confidence END,
                                            last_co_occurrences = CASE WHEN {EpisodeNewer}
                                              THEN excluded.last_co_occurrences ELSE correlation_episodes.last_co_occurrences END,
                                            median_lag_ms = CASE WHEN {EpisodeNewer}
                                              THEN excluded.median_lag_ms ELSE correlation_episodes.median_lag_ms END,
                                            sample_trace_id = CASE WHEN {EpisodeNewer}
                                              THEN excluded.sample_trace_id ELSE correlation_episodes.sample_trace_id END,
                                            source_sample_trace_id = CASE WHEN {EpisodeNewer}
                                              THEN excluded.source_sample_trace_id
                                              ELSE correlation_episodes.source_sample_trace_id END,
                                            gap_ms = CASE WHEN {EpisodeNewer}
                                              THEN excluded.gap_ms ELSE correlation_episodes.gap_ms END,
                                            last_seen_hub_ms = excluded.last_seen_hub_ms;
                                          """;

    /// <summary>
    ///     Stores a daemon's pushed episodes under the merge rule. Waits for the write lock
    ///     as long as the two other imports do, then gives up: a 503 the daemon retries beats
    ///     a request held open behind a purge. Files the push in source_imports and never
    ///     touches source_state, which only the poll owns.
    /// </summary>
    public async Task<bool> TryUpsertEpisodesAsync(
        string sourceId,
        IReadOnlyList<ParsedEpisode> episodes,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        if (!await _writeGate.WaitAsync(WriteGateWait, cancellationToken))
            return false;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(false);
            foreach (var episode in episodes)
            {
                await UpsertEpisodeAsync(connection, transaction, sourceId, episode, observedAtMs, cancellationToken);
            }

            await RecordImportAsync(connection, transaction, sourceId, observedAtMs, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task UpsertEpisodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        ParsedEpisode episode,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = EpisodeUpsert;
        var parameters = command.Parameters;
        parameters.AddWithValue("$id", episode.Id);
        parameters.AddWithValue(SourceIdParameter, sourceId);
        parameters.AddWithValue("$pair_key", episode.PairKey);
        BindSide(parameters, "source", episode.Source);
        BindSide(parameters, "target", episode.Target);
        parameters.AddWithValue("$started_at_ms", episode.StartedAtMs);
        parameters.AddWithValue("$last_seen_ms", episode.LastSeenMs);
        parameters.AddWithValue("$ended_at_ms", (object?)episode.EndedAtMs ?? DBNull.Value);
        parameters.AddWithValue("$gap_ms", episode.GapMs);
        parameters.AddWithValue("$peak_confidence", episode.PeakConfidence);
        parameters.AddWithValue("$peak_co_occurrences", episode.PeakCoOccurrences);
        parameters.AddWithValue("$last_confidence", episode.LastConfidence);
        parameters.AddWithValue("$last_co_occurrences", episode.LastCoOccurrences);
        parameters.AddWithValue("$median_lag_ms", (object?)episode.MedianLagMs ?? DBNull.Value);
        parameters.AddWithValue("$sample_trace_id", (object?)episode.SampleTraceId ?? DBNull.Value);
        parameters.AddWithValue("$source_sample_trace_id", (object?)episode.SourceSampleTraceId ?? DBNull.Value);
        parameters.AddWithValue(ObservedAtParameter, observedAtMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindSide(SqliteParameterCollection parameters, string side, EpisodeSide value)
    {
        parameters.AddWithValue("$" + side + "_type", value.FindingType);
        parameters.AddWithValue("$" + side + "_service", value.Service);
        parameters.AddWithValue("$" + side + "_template", value.Template);
        parameters.AddWithValue("$" + side + "_grouping", (object?)value.GroupingKey ?? DBNull.Value);
        parameters.AddWithValue("$" + side + "_grouping_value", (object?)value.GroupingValue ?? DBNull.Value);
    }
}
