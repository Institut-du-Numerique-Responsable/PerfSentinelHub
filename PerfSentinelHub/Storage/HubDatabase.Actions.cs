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

    // The newest copy's envelope among the listed sources, or the shared row when every source
    // counts: what a done action keeps once the finding's own rows are purged.
    private const string FreezeUpdate = """
                                        UPDATE finding_actions SET frozen_finding_json = COALESCE(
                                          (SELECT fs.finding_json FROM finding_sources AS fs
                                           WHERE fs.signature = $signature AND fs.finding_json IS NOT NULL
                                             AND ($sources IS NULL OR fs.source_id IN (SELECT value FROM json_each($sources)))
                                           ORDER BY fs.last_seen_ms DESC, fs.source_id LIMIT 1),
                                          CASE WHEN $sources IS NULL THEN (SELECT finding_json FROM findings WHERE signature = $signature) END)
                                        WHERE signature = $signature;
                                        """;

    private const string CopiesSelect = """
                                        SELECT fs.signature, fs.source_id, fs.service,
                                               fa.grouping_key, fa.grouping_value, fa.attr_key, fa.attr_value
                                        FROM finding_sources AS fs
                                        JOIN findings AS f ON f.signature = fs.signature
                                        LEFT JOIN finding_attributes AS fa
                                          ON fa.signature = fs.signature AND fa.source_id = fs.source_id

                                        """;

    private const string CopiesOrder = "\nORDER BY fs.signature, fs.source_id;";

    /// <summary>
    ///     Files a new action and its first event, in one transaction. <c>Exists</c> when the
    ///     signature already has one, <c>Busy</c> when the write lock stays held past the wait.
    ///     <paramref name="ownerDisplay" /> is the name the SSO gave <paramref name="owner" />, if any.
    ///     A done action keeps the finding's envelope from <paramref name="freezeFrom" />'s sources,
    ///     every source when null.
    /// </summary>
    // The action's fields, the caller and the clock, each its own column or check.
#pragma warning disable S107
    public async Task<ActionWriteOutcome> CreateActionAsync(
        string signature,
        string status,
        string? owner,
        string? ownerDisplay,
        string by,
        long nowMs,
        IReadOnlyList<string>? freezeFrom,
        CancellationToken cancellationToken)
#pragma warning restore S107
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

            if (status == ActionStatuses.Done)
                await FreezeAsync(connection, transaction, signature, freezeFrom, cancellationToken);
            await InsertActionEventAsync(connection, transaction, signature,
                new StoredActionEvent(null, status, owner, ownerDisplay, by, nowMs), cancellationToken);
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
    ///     Entering done freezes the envelope as <see cref="CreateActionAsync" /> does, and leaving
    ///     done clears it.
    /// </summary>
    // The action's fields, the caller and the clock, each its own column or check.
#pragma warning disable S107
    public async Task<ActionWriteOutcome> UpdateActionAsync(
        string signature,
        string? status,
        string? owner,
        bool ownerSet,
        string by,
        long nowMs,
        IReadOnlyList<string>? freezeFrom,
        CancellationToken cancellationToken)
#pragma warning restore S107
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
                                         done_at_ms = $done_at_ms,
                                         frozen_finding_json = CASE WHEN $status = 'done' THEN frozen_finding_json END
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

            if (newStatus == ActionStatuses.Done && currentStatus != ActionStatuses.Done)
                await FreezeAsync(connection, transaction, signature, freezeFrom, cancellationToken);
            await InsertActionEventAsync(connection, transaction, signature,
                new StoredActionEvent(currentStatus, newStatus, newOwner, newDisplay, by, nowMs), cancellationToken);
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
        await reader.NextResultAsync(cancellationToken);
        var events = await ReadActionEventsAsync(reader, cancellationToken);

        return
        [
            .. rows.Select(row => new StoredAction(row.Signature, row.Status, row.Owner, row.Display, row.By,
                row.Created, row.Updated, row.Done, events.GetValueOrDefault(row.Signature) ?? []))
        ];
    }

    // The events result set, grouped by signature in the order the select gives.
    private static async Task<Dictionary<string, List<StoredActionEvent>>> ReadActionEventsAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var events = new Dictionary<string, List<StoredActionEvent>>(StringComparer.Ordinal);
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

        return events;
    }

    private static async Task FreezeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string signature,
        IReadOnlyList<string>? freezeFrom,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = FreezeUpdate;
        command.Parameters.AddWithValue(SignatureParameter, signature);
        command.Parameters.AddWithValue("$sources", freezeFrom is null ? DBNull.Value : (object)JsonArray(freezeFrom));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertActionEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string signature,
        StoredActionEvent change,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ActionEventInsert;
        command.Parameters.AddWithValue(SignatureParameter, signature);
        command.Parameters.AddWithValue("$from_status", (object?)change.FromStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$to_status", change.ToStatus);
        command.Parameters.AddWithValue("$owner", (object?)change.Owner ?? DBNull.Value);
        command.Parameters.AddWithValue("$owner_display", (object?)change.OwnerDisplay ?? DBNull.Value);
        command.Parameters.AddWithValue("$by", change.By);
        command.Parameters.AddWithValue("$at_ms", change.AtMs);
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

    // One copy per (signature, source), in the order the select gives, which keeps the rows of a
    // copy together: once the key changes the copy before is whole, and the read stops as soon as
    // enough accepts one.
    internal static async Task<IReadOnlyList<AccessCopy>> ReadCopiesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken,
        Func<AccessCopy, bool>? enough = null)
    {
        var copies = new List<AccessCopy>();
        (string Signature, string Source)? key = null;
        List<AccessAttribute> rows = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = (reader.GetString(0), reader.GetString(1));
            if (row != key)
            {
                if (key is not null && enough?.Invoke(copies[^1]) == true)
                    return copies;
                key = row;
                rows = [];
                copies.Add(new AccessCopy(row.Item2, reader.IsDBNull(2) ? null : reader.GetString(2), rows));
            }

            if (!reader.IsDBNull(3))
                rows.Add(new AccessAttribute(reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetString(6)));
        }

        return copies;
    }
}
