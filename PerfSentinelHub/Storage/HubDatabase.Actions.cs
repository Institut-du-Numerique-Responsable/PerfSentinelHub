using Microsoft.Data.Sqlite;
using PerfSentinelHub.Access;

namespace PerfSentinelHub.Storage;

// Actions on findings: one row per signature and an append-only log of its changes. The
// signature is the key and nothing else, as for an ack, and no foreign key ties a row to
// a finding, so an action outlives the finding's retention.
public sealed partial class HubDatabase
{
    private const string ActionEventInsert = """
                                             INSERT INTO finding_action_events(signature, from_status, to_status, owner, owner_display, by, at_ms)
                                             VALUES ($signature, $from_status, $to_status, $owner, $owner_display, $by, $at_ms);
                                             """;

    /// <summary>
    ///     Files a new action and its first event, in one transaction. <c>Exists</c> when the
    ///     signature already has one, <c>Busy</c> when the write lock stays held past the wait.
    ///     <paramref name="ownerDisplay" /> is the name the SSO gave <paramref name="owner" />, if any.
    /// </summary>
    public async Task<ActionWriteOutcome> CreateActionAsync(
        string signature,
        string status,
        string? owner,
        string? ownerDisplay,
        string by,
        long nowMs,
        CancellationToken cancellationToken)
    {
        if (!await _writeGate.WaitAsync(WriteGateWait, cancellationToken))
            return ActionWriteOutcome.Busy;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(false);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                                 INSERT OR IGNORE INTO finding_actions(
                                   signature, status, owner, owner_display, created_by, created_at_ms, updated_at_ms,
                                   done_at_ms)
                                 VALUES ($signature, $status, $owner, $owner_display, $by, $at_ms, $at_ms, $done_at_ms);
                                 """;
            insert.Parameters.AddWithValue(SignatureParameter, signature);
            insert.Parameters.AddWithValue("$status", status);
            insert.Parameters.AddWithValue("$owner", (object?)owner ?? DBNull.Value);
            insert.Parameters.AddWithValue("$owner_display", (object?)ownerDisplay ?? DBNull.Value);
            insert.Parameters.AddWithValue("$by", by);
            insert.Parameters.AddWithValue("$at_ms", nowMs);
            insert.Parameters.AddWithValue(
                "$done_at_ms",
                status == ActionStatuses.Done ? nowMs : DBNull.Value);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                return ActionWriteOutcome.Exists;

            await InsertActionEventAsync(connection, transaction, signature, null, status, owner, ownerDisplay, by, nowMs,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ActionWriteOutcome.Written;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    ///     Changes the status, the owner, or both, and logs it when one of them differs.
    ///     <paramref name="status" /> null leaves the status; <paramref name="ownerSet" /> false
    ///     leaves the owner and true sets it, to null to clear it. <c>done_at_ms</c> is stamped
    ///     when the action enters done and cleared when it leaves it. An owner set from the body
    ///     is a typed one, so it drops the display name; a status change or the same owner keeps it.
    /// </summary>
    public async Task<ActionWriteOutcome> UpdateActionAsync(
        string signature,
        string? status,
        string? owner,
        bool ownerSet,
        string by,
        long nowMs,
        CancellationToken cancellationToken)
    {
        if (!await _writeGate.WaitAsync(WriteGateWait, cancellationToken))
            return ActionWriteOutcome.Busy;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(false);
            string currentStatus;
            string? currentOwner;
            string? currentDisplay;
            long? currentDoneAt;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText =
                    "SELECT status, owner, owner_display, done_at_ms FROM finding_actions WHERE signature = $signature;";
                read.Parameters.AddWithValue(SignatureParameter, signature);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return ActionWriteOutcome.Missing;
                currentStatus = reader.GetString(0);
                currentOwner = reader.IsDBNull(1) ? null : reader.GetString(1);
                currentDisplay = reader.IsDBNull(2) ? null : reader.GetString(2);
                currentDoneAt = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            }

            var newStatus = status ?? currentStatus;
            var newOwner = ownerSet ? owner : currentOwner;
            var newDisplay = newOwner == currentOwner ? currentDisplay : null;
            if (newStatus == currentStatus && newOwner == currentOwner)
                return ActionWriteOutcome.Written;

            long? doneAt = newStatus == ActionStatuses.Done ? currentDoneAt ?? nowMs : null;
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                                     UPDATE finding_actions
                                     SET status = $status, owner = $owner, owner_display = $owner_display, updated_at_ms = $at_ms,
                                         done_at_ms = $done_at_ms
                                     WHERE signature = $signature;
                                     """;
                update.Parameters.AddWithValue(SignatureParameter, signature);
                update.Parameters.AddWithValue("$status", newStatus);
                update.Parameters.AddWithValue("$owner", (object?)newOwner ?? DBNull.Value);
                update.Parameters.AddWithValue("$owner_display", (object?)newDisplay ?? DBNull.Value);
                update.Parameters.AddWithValue("$at_ms", nowMs);
                update.Parameters.AddWithValue("$done_at_ms", (object?)doneAt ?? DBNull.Value);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertActionEventAsync(connection, transaction, signature, currentStatus, newStatus, newOwner,
                newDisplay, by, nowMs, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ActionWriteOutcome.Written;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>The actions of these signatures, each with its events ordered by time then arrival.</summary>
    public async Task<IReadOnlyList<StoredAction>> ReadActionsAsync(
        IReadOnlyList<string> signatures,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT signature, status, owner, owner_display, created_by, created_at_ms, updated_at_ms, done_at_ms
                              FROM finding_actions
                              WHERE signature IN (SELECT value FROM json_each($signatures))
                              ORDER BY signature;
                              SELECT signature, from_status, to_status, owner, owner_display, by, at_ms
                              FROM finding_action_events
                              WHERE signature IN (SELECT value FROM json_each($signatures))
                              ORDER BY at_ms, id;
                              """;
        command.Parameters.AddWithValue("$signatures", JsonArray(signatures));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(string Signature, string Status, string? Owner, string? Display, string By, long Created,
            long Updated, long? Done)>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetInt64(5),
                reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        var events = new Dictionary<string, List<StoredActionEvent>>(StringComparer.Ordinal);
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var signature = reader.GetString(0);
            if (!events.TryGetValue(signature, out var list))
                events[signature] = list = [];
            list.Add(new StoredActionEvent(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6)));
        }

        return
        [
            .. rows.Select(row => new StoredAction(row.Signature, row.Status, row.Owner, row.Display, row.By,
                row.Created, row.Updated, row.Done, events.GetValueOrDefault(row.Signature) ?? []))
        ];
    }

    private static async Task InsertActionEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string signature,
        string? fromStatus,
        string toStatus,
        string? owner,
        string? ownerDisplay,
        string by,
        long nowMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ActionEventInsert;
        command.Parameters.AddWithValue(SignatureParameter, signature);
        command.Parameters.AddWithValue("$from_status", (object?)fromStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$to_status", toStatus);
        command.Parameters.AddWithValue("$owner", (object?)owner ?? DBNull.Value);
        command.Parameters.AddWithValue("$owner_display", (object?)ownerDisplay ?? DBNull.Value);
        command.Parameters.AddWithValue("$by", by);
        command.Parameters.AddWithValue("$at_ms", nowMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    ///     Every Hub copy of one finding, each with the service it carries at its own source and
    ///     its attribute rows: what an action is judged on, since a signature can be seen at
    ///     several sources under several services.
    /// </summary>
    public async Task<IReadOnlyList<AccessCopy>> ReadFindingCopiesAsync(
        string signature,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = CopiesSelect + "WHERE fs.signature = $signature" + CopiesOrder;
        command.Parameters.AddWithValue(SignatureParameter, signature);
        return await ReadCopiesAsync(command, cancellationToken);
    }

    private const string CopiesSelect = """
                                        SELECT fs.signature, fs.source_id, fs.service,
                                               fa.grouping_key, fa.grouping_value, fa.attr_key, fa.attr_value
                                        FROM finding_sources AS fs
                                        JOIN findings AS f ON f.signature = fs.signature
                                        LEFT JOIN finding_attributes AS fa
                                          ON fa.signature = fs.signature AND fa.source_id = fs.source_id

                                        """;

    private const string CopiesOrder = "\nORDER BY fs.signature, fs.source_id;";

    // One copy per (signature, source), in the order the select gives.
    internal static async Task<IReadOnlyList<AccessCopy>> ReadCopiesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var copies = new Dictionary<(string, string), (string? Service, List<AccessAttribute> Rows)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!copies.TryGetValue(key, out var copy))
                copies[key] = copy = (reader.IsDBNull(2) ? null : reader.GetString(2), []);
            if (!reader.IsDBNull(3))
                copy.Rows.Add(new AccessAttribute(reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetString(6)));
        }

        return [.. copies.Select(copy => new AccessCopy(copy.Key.Item2, copy.Value.Service, copy.Value.Rows))];
    }
}
