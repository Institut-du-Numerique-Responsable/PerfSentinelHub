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
    ///     The last ack read per source, keyed by source id. A source with no
    ///     row has never had its acks read.
    /// </summary>
    public Task<Dictionary<string, SourceRead>> QueryAckReadsAsync(CancellationToken cancellationToken)
    {
        return QueryReadsAsync(AckReadSelect, cancellationToken);
    }
}

/// <summary>An ack's target at one source: the held copy, if any, and whether the mirror lists an ack of it.</summary>
public sealed record AckTarget(bool Held, string? Service, bool Mirrored, IReadOnlyList<AccessAttribute> Rows);
