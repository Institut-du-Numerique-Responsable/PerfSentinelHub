using System.Text.Json;
using Microsoft.Data.Sqlite;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Storage;

public sealed partial class HubDatabase
{
    private const string TokenColumns = """
                                        id, token_hash, subject, identity, provider_groups_json, required_group,
                                        client_address, created_at_ms, expires_at_ms, last_used_at_ms
                                        """;

    /// <summary>
    ///     Stores a new terminal token. In the same transaction the owner's expired rows go, and
    ///     so do its live ones beyond the newest nine, so a subject holds at most ten. False when
    ///     the write gate stays held, which the device flow answers with authorization_pending.
    /// </summary>
    public async Task<bool> TryInsertTerminalTokenAsync(TerminalToken token, long nowMs, CancellationToken cancellationToken)
    {
        if (!await _writeGate.WaitAsync(WriteGateWait, cancellationToken))
            return false;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                                   DELETE FROM terminal_tokens WHERE subject = $subject AND (
                                     expires_at_ms <= $now OR id NOT IN (
                                       SELECT id FROM terminal_tokens
                                       WHERE subject = $subject AND expires_at_ms > $now
                                       ORDER BY created_at_ms DESC, rowid DESC LIMIT $keep));
                                   INSERT INTO terminal_tokens({TokenColumns})
                                   VALUES ($id, $token_hash, $subject, $identity, $provider_groups_json,
                                           $required_group, $client_address, $created_at_ms, $expires_at_ms, NULL);
                                   """;
            command.Parameters.AddWithValue("$id", token.Id);
            command.Parameters.AddWithValue("$token_hash", token.TokenHash);
            command.Parameters.AddWithValue("$subject", token.Subject);
            command.Parameters.AddWithValue("$identity", token.Identity);
            command.Parameters.AddWithValue("$provider_groups_json",
                JsonSerializer.Serialize([.. token.ProviderGroups], HubJsonContext.Default.StringArray));
            command.Parameters.AddWithValue("$required_group", (object?)token.RequiredGroup ?? DBNull.Value);
            command.Parameters.AddWithValue("$client_address", (object?)token.ClientAddress ?? DBNull.Value);
            command.Parameters.AddWithValue("$created_at_ms", token.CreatedAtMs);
            command.Parameters.AddWithValue("$expires_at_ms", token.ExpiresAtMs);
            command.Parameters.AddWithValue("$now", nowMs);
            command.Parameters.AddWithValue("$keep", TerminalAccess.MaxTokensPerSubject - 1);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // A read that never takes the gate: every bearer request makes one.
    public async Task<TerminalToken?> FindTerminalTokenAsync(byte[] tokenHash, long nowMs, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TokenColumns} FROM terminal_tokens WHERE token_hash = $hash AND expires_at_ms > $now;";
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$now", nowMs);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTerminalToken(reader) : null;
    }

    /// <summary>
    ///     Records a use, at most once per five minutes. A use never waits for a writer: with the
    ///     gate busy the update is skipped, and a later use records it.
    /// </summary>
    public async Task TouchTerminalTokenAsync(string id, long nowMs, CancellationToken cancellationToken)
    {
        if (!await _writeGate.WaitAsync(0, CancellationToken.None))
            return;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                                  UPDATE terminal_tokens SET last_used_at_ms = $now
                                  WHERE id = $id AND (last_used_at_ms IS NULL OR last_used_at_ms <= $now - $resolution);
                                  """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", nowMs);
            command.Parameters.AddWithValue("$resolution", TerminalAccess.LastUseResolutionMs);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // Live tokens, newest first: one subject's, or everyone's with subject null.
    public async Task<IReadOnlyList<TerminalToken>> ListTerminalTokensAsync(
        string? subject,
        long nowMs,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT {TokenColumns} FROM terminal_tokens
                               WHERE expires_at_ms > $now AND ($subject IS NULL OR subject = $subject)
                               ORDER BY created_at_ms DESC, rowid DESC LIMIT $limit;
                               """;
        command.Parameters.AddWithValue("$now", nowMs);
        command.Parameters.AddWithValue("$subject", (object?)subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var tokens = new List<TerminalToken>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            tokens.Add(ReadTerminalToken(reader));
        return tokens;
    }

    /// <summary>
    ///     Revokes one token by id, any owner's when owner is null. Stored is false when the gate
    ///     stays held; Owner is the deleted row's subject, null when no row matched.
    /// </summary>
    public async Task<(bool Stored, string? Owner)> TryDeleteTerminalTokenAsync(
        string id,
        string? owner,
        CancellationToken cancellationToken)
    {
        if (!await _writeGate.WaitAsync(WriteGateWait, cancellationToken))
            return (false, null);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                                  DELETE FROM terminal_tokens WHERE id = $id AND ($owner IS NULL OR subject = $owner)
                                  RETURNING subject;
                                  """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$owner", (object?)owner ?? DBNull.Value);
            return (true, await command.ExecuteScalarAsync(cancellationToken) as string);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // RFC 7009: looked up first, so an unknown token never waits for the gate.
    public async Task<(bool Stored, TerminalToken? Deleted)> TryDeleteTerminalTokenByHashAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken)
    {
        if (await FindTerminalTokenAsync(tokenHash, long.MinValue, cancellationToken) is not { } token)
            return (true, null);
        var (stored, owner) = await TryDeleteTerminalTokenAsync(token.Id, null, cancellationToken);
        return (stored, owner is null ? null : token);
    }

    private static TerminalToken ReadTerminalToken(SqliteDataReader reader)
    {
        return new TerminalToken(
            reader.GetString(0),
            (byte[])reader.GetValue(1),
            reader.GetString(2),
            reader.GetString(3),
            JsonSerializer.Deserialize(reader.GetString(4), HubJsonContext.Default.StringArray) ?? [],
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9));
    }
}
