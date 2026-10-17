using Microsoft.Data.Sqlite;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Storage;

public sealed partial class HubDatabase
{
    private const string AckReadUpsert = $"INSERT INTO ack_reads{ReadLedgerUpsert}";
    private const string AckReadSelect = $"{ReadLedgerSelect}ack_reads;";

    /// <summary>
    ///     Replaces one source's mirror with what its daemon just listed and
    ///     files the read, in one transaction. Replaced rather than merged: the
    ///     listing is the whole truth of that daemon, so an ack it no longer
    ///     lists was revoked or has expired. On a signature listed twice the
    ///     last row wins, and the daemon lists its baseline last, which is the
    ///     one its own lookup prefers.
    /// </summary>
    public async Task ReplaceSourceAcksAsync(
        string sourceId,
        IReadOnlyList<ParsedAck> acks,
        string state,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(false);
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM source_acks WHERE source_id = $source_id;";
                delete.Parameters.AddWithValue(SourceIdParameter, sourceId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var ack in acks)
                await InsertSourceAckAsync(connection, transaction, sourceId, ack, observedAtMs, cancellationToken);
            await RecordReadAsync(
                connection,
                transaction,
                AckReadUpsert,
                sourceId,
                new SourceRead(observedAtMs, state, null),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task InsertSourceAckAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        ParsedAck ack,
        long observedAtMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT OR REPLACE INTO source_acks(
                                source_id, signature, origin, acked_by, reason, acked_at,
                                expires_at, expires_at_ms, read_at_ms)
                              VALUES (
                                $source_id, $signature, $origin, $acked_by, $reason, $acked_at,
                                $expires_at, $expires_at_ms, $observed_at);
                              """;
        command.Parameters.AddWithValue(SourceIdParameter, sourceId);
        command.Parameters.AddWithValue(SignatureParameter, ack.Signature);
        command.Parameters.AddWithValue("$origin", ack.Origin);
        command.Parameters.AddWithValue("$acked_by", ack.By);
        command.Parameters.AddWithValue("$reason", (object?)ack.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$acked_at", ack.At);
        command.Parameters.AddWithValue("$expires_at", (object?)ack.ExpiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$expires_at_ms", (object?)ack.ExpiresAtMs ?? DBNull.Value);
        command.Parameters.AddWithValue(ObservedAtParameter, observedAtMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Files the outcome of an ack read that mirrored nothing. The rows of
    ///     the last read that succeeded stay, the state filed here says they are
    ///     no longer current, and the purge removes them once no read refreshes them.
    /// </summary>
    public Task RecordAckReadAsync(
        string sourceId,
        long readAtMs,
        string state,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        return RecordReadAsync(AckReadUpsert, sourceId, new SourceRead(readAtMs, state, errorCode), cancellationToken);
    }

    /// <summary>
    ///     What the relay asks before it spends a daemon's write key: whether the Hub holds
    ///     that signature at that source as a finding the source carried, with the copy's own
    ///     service and attribute rows for the access check, and whether the mirror lists an
    ///     ack of it. A revoke also passes on an ack its daemon listed, which may outlive the
    ///     finding, and never needs one: a daemon below 0.24.0 takes an ack the mirror will
    ///     not list. So the daemon's ack store only ever grows by findings the Hub knows.
    /// </summary>
    public async Task<AckTarget> ReadAckTargetAsync(
        string sourceId,
        string signature,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT fs.signature IS NOT NULL, fs.service,
                                     EXISTS(SELECT 1 FROM source_acks WHERE source_id = $source_id AND signature = $signature),
                                     fa.grouping_key, fa.grouping_value, fa.attr_key, fa.attr_value
                              FROM (SELECT 1)
                              LEFT JOIN finding_sources AS fs ON fs.source_id = $source_id AND fs.signature = $signature
                              LEFT JOIN finding_attributes AS fa
                                ON fa.signature = fs.signature AND fa.source_id = fs.source_id;
                              """;
        command.Parameters.AddWithValue(SourceIdParameter, sourceId);
        command.Parameters.AddWithValue(SignatureParameter, signature);

        var target = new AckTarget(false, null, false, []);
        var rows = new List<AccessAttribute>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            target = new AckTarget(
                reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(2), rows);
            if (!reader.IsDBNull(3))
                rows.Add(new AccessAttribute(
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        }

        return target;
    }

    /// <summary>
    ///     The daemon acks the mirror lists at one source and that have not expired, the
    ///     first <paramref name="cap" /> by signature, each with its held copy for the
    ///     access check. The CI baseline is left out: no route revokes it.
    /// </summary>
    public async Task<IReadOnlyList<ListedAck>> ReadSourceAcksAsync(
        string sourceId,
        long nowMs,
        int cap,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT sa.signature, sa.acked_by, sa.reason, sa.acked_at, sa.expires_at,
                                     fs.signature IS NOT NULL, fs.service,
                                     fa.grouping_key, fa.grouping_value, fa.attr_key, fa.attr_value
                              FROM source_acks AS sa
                              LEFT JOIN finding_sources AS fs ON fs.source_id = sa.source_id AND fs.signature = sa.signature
                              LEFT JOIN finding_attributes AS fa
                                ON fa.signature = fs.signature AND fa.source_id = fs.source_id
                              WHERE sa.source_id = $source_id AND sa.origin = 'daemon'
                                AND (sa.expires_at_ms IS NULL OR sa.expires_at_ms > $now)
                              ORDER BY sa.signature;
                              """;
        command.Parameters.AddWithValue(SourceIdParameter, sourceId);
        command.Parameters.AddWithValue("$now", nowMs);

        var acks = new List<ListedAck>();
        var rows = new List<AccessAttribute>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var signature = reader.GetString(0);
            if (acks.Count == 0 || acks[^1].Signature != signature)
            {
                if (acks.Count == cap)
                    break;
                rows = [];
                acks.Add(new ListedAck(
                    signature, reader.GetString(1), NullableString(reader, 2), reader.GetString(3),
                    NullableString(reader, 4), reader.GetBoolean(5), NullableString(reader, 6), rows));
            }

            if (!reader.IsDBNull(7))
                rows.Add(new AccessAttribute(
                    reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10)));
        }

        return acks;
    }

    /// <summary>
    ///     The last ack read per source, keyed by source id. A source with no
    ///     row has never had its acks read.
    /// </summary>
    public Task<Dictionary<string, SourceRead>> QueryAckReadsAsync(CancellationToken cancellationToken)
    {
        return QueryReadsAsync(AckReadSelect, cancellationToken);
    }
}
