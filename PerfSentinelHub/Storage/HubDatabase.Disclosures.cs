using Microsoft.Data.Sqlite;

namespace PerfSentinelHub.Storage;

public sealed partial class HubDatabase
{
    // Every column but the four BLOBs, which a listing never reads: their presence only.
    private const string DisclosureColumns = """
                                             id, report_uuid, organisation, period_type, period_from, period_to,
                                             intent, confidentiality, content_hash, integrity_level, figures_json,
                                             attestation IS NOT NULL, bundle IS NOT NULL, pdf IS NOT NULL,
                                             render_reason, engine_version, hash_checked, binding_checked,
                                             published_at_ms, signature_added_at_ms, withdrawn_at_ms,
                                             withdrawn_reason, withdrawn_by
                                             """;

    /// <summary>False when the report's uuid is already stored: the caller decides between 200 and 409.</summary>
    public async Task<bool> InsertDisclosureAsync(DisclosureRecord record, CancellationToken cancellationToken)
    {
        return await WriteAsync(async command =>
        {
            command.CommandText = """
                                  INSERT INTO disclosures(
                                    id, report_uuid, organisation, period_type, period_from, period_to, intent,
                                    confidentiality, content_hash, integrity_level, figures_json, report,
                                    attestation, bundle, pdf, render_reason, engine_version, hash_checked,
                                    binding_checked, published_at_ms)
                                  VALUES (
                                    $id, $uuid, $organisation, $period_type, $period_from, $period_to, $intent,
                                    $confidentiality, $content_hash, $integrity_level, $figures_json, $report,
                                    $attestation, $bundle, $pdf, $render_reason, $engine_version, $hash_checked,
                                    $binding_checked, $published_at)
                                  ON CONFLICT(report_uuid) DO NOTHING;
                                  """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$uuid", record.ReportUuid);
            command.Parameters.AddWithValue("$organisation", record.Organisation);
            command.Parameters.AddWithValue("$period_type", record.PeriodType);
            command.Parameters.AddWithValue("$period_from", record.PeriodFrom);
            command.Parameters.AddWithValue("$period_to", record.PeriodTo);
            command.Parameters.AddWithValue("$intent", record.Intent);
            command.Parameters.AddWithValue("$confidentiality", record.Confidentiality);
            command.Parameters.AddWithValue("$content_hash", record.ContentHash);
            command.Parameters.AddWithValue("$integrity_level", Nullable(record.IntegrityLevel));
            command.Parameters.AddWithValue("$figures_json", Nullable(record.FiguresJson));
            AddFiles(command, record.Files);
            command.Parameters.AddWithValue("$pdf", Nullable(record.Pdf));
            command.Parameters.AddWithValue("$render_reason", Nullable(record.RenderReason));
            command.Parameters.AddWithValue("$engine_version", Nullable(record.EngineVersion));
            command.Parameters.AddWithValue("$hash_checked", Nullable(record.HashChecked));
            command.Parameters.AddWithValue("$binding_checked", Nullable(record.BindingChecked));
            command.Parameters.AddWithValue("$published_at", record.PublishedAtMs);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }, cancellationToken);
    }

    /// <summary>
    ///     The signed copy of a report already stored unsigned: each file as received, the
    ///     render it got, and when the signature arrived. A rendered PDF stands for both checks.
    /// </summary>
    public Task ReplaceSignedCopyAsync(
        string id,
        DisclosureFiles files,
        byte[]? pdf,
        string? renderReason,
        string? engineVersion,
        long nowMs,
        CancellationToken cancellationToken)
    {
        return WriteAsync(async command =>
        {
            command.CommandText = """
                                  UPDATE disclosures
                                  SET report = $report, attestation = $attestation, bundle = $bundle, pdf = $pdf,
                                      render_reason = $render_reason, engine_version = $engine_version,
                                      hash_checked = $checked, binding_checked = $binding_checked,
                                      signature_added_at_ms = $now
                                  WHERE id = $id;
                                  """;
            command.Parameters.AddWithValue("$id", id);
            AddFiles(command, files);
            command.Parameters.AddWithValue("$pdf", Nullable(pdf));
            command.Parameters.AddWithValue("$render_reason", Nullable(renderReason));
            command.Parameters.AddWithValue("$engine_version", Nullable(engineVersion));
            command.Parameters.AddWithValue("$checked", pdf is null ? DBNull.Value : true);
            command.Parameters.AddWithValue(
                "$binding_checked", pdf is null || files.Attestation is null ? DBNull.Value : true);
            command.Parameters.AddWithValue("$now", nowMs);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task SetPdfAsync(
        string id,
        byte[]? pdf,
        string? renderReason,
        string? engineVersion,
        bool? hashChecked,
        bool? bindingChecked,
        CancellationToken cancellationToken)
    {
        return WriteAsync(async command =>
        {
            command.CommandText = """
                                  UPDATE disclosures
                                  SET pdf = $pdf, render_reason = $render_reason, engine_version = $engine_version,
                                      hash_checked = $hash_checked, binding_checked = $binding_checked
                                  WHERE id = $id;
                                  """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$pdf", Nullable(pdf));
            command.Parameters.AddWithValue("$render_reason", Nullable(renderReason));
            command.Parameters.AddWithValue("$engine_version", Nullable(engineVersion));
            command.Parameters.AddWithValue("$hash_checked", Nullable(hashChecked));
            command.Parameters.AddWithValue("$binding_checked", Nullable(bindingChecked));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<DisclosureSummary?> FindDisclosureByUuidAsync(string reportUuid, CancellationToken cancellationToken)
    {
        return (await ReadSummariesAsync("WHERE report_uuid = $key", reportUuid, cancellationToken)).FirstOrDefault();
    }

    public async Task<DisclosureSummary?> GetDisclosureAsync(string id, CancellationToken cancellationToken)
    {
        return (await ReadSummariesAsync("WHERE id = $key", id, cancellationToken)).FirstOrDefault();
    }

    /// <summary>Newest period first, withdrawn ones included.</summary>
    public Task<IReadOnlyList<DisclosureSummary>> ListDisclosuresAsync(CancellationToken cancellationToken)
    {
        return ReadSummariesAsync("ORDER BY period_to DESC, id", null, cancellationToken);
    }

    /// <summary>One file as received, or null when the disclosure or that file does not exist.</summary>
    public async Task<byte[]?> ReadFileAsync(string id, DisclosureFile file, CancellationToken cancellationToken)
    {
        var column = file switch
        {
            DisclosureFile.Report => "report",
            DisclosureFile.Attestation => "attestation",
            DisclosureFile.Bundle => "bundle",
            _ => "pdf"
        };
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM disclosures WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(cancellationToken) as byte[];
    }

    /// <summary>False when it does not exist or was already withdrawn: a withdrawal is final.</summary>
    public async Task<bool> WithdrawAsync(string id, string reason, string by, long nowMs, CancellationToken cancellationToken)
    {
        return await WriteAsync(async command =>
        {
            command.CommandText = """
                                  UPDATE disclosures
                                  SET withdrawn_at_ms = $now, withdrawn_reason = $reason, withdrawn_by = $by
                                  WHERE id = $id AND withdrawn_at_ms IS NULL;
                                  """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$by", by);
            command.Parameters.AddWithValue("$now", nowMs);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }, cancellationToken);
    }

    public Task InsertShareLinkAsync(ShareLinkRecord link, CancellationToken cancellationToken)
    {
        return WriteAsync(async command =>
        {
            command.CommandText = """
                                  INSERT INTO disclosure_share_links(
                                    id, disclosure_id, token_hash, created_by, created_at_ms, expires_at_ms)
                                  VALUES ($id, $disclosure_id, $token_hash, $created_by, $created_at, $expires_at);
                                  """;
            command.Parameters.AddWithValue("$id", link.Id);
            command.Parameters.AddWithValue("$disclosure_id", link.DisclosureId);
            command.Parameters.AddWithValue("$token_hash", link.TokenHash);
            command.Parameters.AddWithValue("$created_by", link.CreatedBy);
            command.Parameters.AddWithValue("$created_at", link.CreatedAtMs);
            command.Parameters.AddWithValue("$expires_at", link.ExpiresAtMs);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    ///     The live link a token hash names, or null for an unknown, expired or revoked one, or one
    ///     whose disclosure was withdrawn: one answer for all four, so a probe learns nothing.
    /// </summary>
    public async Task<(string LinkId, string DisclosureId)?> FindShareLinkAsync(
        byte[] tokenHash,
        long nowMs,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT l.id, l.disclosure_id
                              FROM disclosure_share_links AS l
                              JOIN disclosures AS d ON d.id = l.disclosure_id
                              WHERE l.token_hash = $token_hash AND l.revoked_at_ms IS NULL
                                AND l.expires_at_ms > $now AND d.withdrawn_at_ms IS NULL;
                              """;
        command.Parameters.AddWithValue("$token_hash", tokenHash);
        command.Parameters.AddWithValue("$now", nowMs);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    public async Task<bool> RevokeShareLinkAsync(
        string disclosureId,
        string linkId,
        string by,
        long nowMs,
        CancellationToken cancellationToken)
    {
        return await WriteAsync(async command =>
        {
            command.CommandText = """
                                  UPDATE disclosure_share_links SET revoked_at_ms = $now, revoked_by = $by
                                  WHERE id = $id AND disclosure_id = $disclosure_id AND revoked_at_ms IS NULL;
                                  """;
            command.Parameters.AddWithValue("$id", linkId);
            command.Parameters.AddWithValue("$disclosure_id", disclosureId);
            command.Parameters.AddWithValue("$by", by);
            command.Parameters.AddWithValue("$now", nowMs);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ShareLinkSummary>> ListShareLinksAsync(
        string disclosureId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, created_by, created_at_ms, expires_at_ms, revoked_at_ms, revoked_by,
                                     downloads, last_used_at_ms, last_client
                              FROM disclosure_share_links WHERE disclosure_id = $disclosure_id
                              ORDER BY created_at_ms DESC, id;
                              """;
        command.Parameters.AddWithValue("$disclosure_id", disclosureId);
        var links = new List<ShareLinkSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            links.Add(new ShareLinkSummary(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                NullableInt64(reader, 4), NullableString(reader, 5), reader.GetInt64(6), NullableInt64(reader, 7),
                NullableString(reader, 8)));
        return links;
    }

    /// <summary>The counts the share route kept in memory, added in one transaction.</summary>
    public async Task AddShareDownloadsAsync(
        IReadOnlyDictionary<string, ShareDownloads> downloads,
        CancellationToken cancellationToken)
    {
        if (downloads.Count == 0)
            return;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(false);
            foreach (var (linkId, counted) in downloads)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                                      UPDATE disclosure_share_links
                                      SET downloads = downloads + $count,
                                          last_used_at_ms = MAX(COALESCE(last_used_at_ms, 0), $last_used),
                                          last_client = COALESCE($client, last_client)
                                      WHERE id = $id;
                                      """;
                command.Parameters.AddWithValue("$id", linkId);
                command.Parameters.AddWithValue("$count", counted.Count);
                command.Parameters.AddWithValue("$last_used", counted.LastUsedAtMs);
                command.Parameters.AddWithValue("$client", Nullable(counted.LastClient));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task EnqueueDeliveriesAsync(
        string disclosureId,
        string eventName,
        IReadOnlyList<string> webhooks,
        long nowMs,
        CancellationToken cancellationToken)
    {
        if (webhooks.Count == 0)
            return;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(false);
            foreach (var webhook in webhooks)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                                      INSERT INTO disclosure_deliveries(
                                        id, disclosure_id, webhook, event, state, created_at_ms, next_attempt_ms)
                                      VALUES ($id, $disclosure_id, $webhook, $event, $state, $now, $now);
                                      """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("$disclosure_id", disclosureId);
                command.Parameters.AddWithValue("$webhook", webhook);
                command.Parameters.AddWithValue("$event", eventName);
                command.Parameters.AddWithValue("$state", DeliveryStates.Pending);
                command.Parameters.AddWithValue("$now", nowMs);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Pending deliveries whose next attempt has come, oldest first. Survives a restart.</summary>
    public Task<IReadOnlyList<DeliveryRecord>> DueDeliveriesAsync(long nowMs, CancellationToken cancellationToken)
    {
        return ReadDeliveriesAsync(
            "WHERE state = 'pending' AND next_attempt_ms <= $key ORDER BY next_attempt_ms, id", nowMs, cancellationToken);
    }

    public Task<IReadOnlyList<DeliveryRecord>> ListDeliveriesAsync(string disclosureId, CancellationToken cancellationToken)
    {
        return ReadDeliveriesAsync("WHERE disclosure_id = $key ORDER BY created_at_ms, rowid", disclosureId, cancellationToken);
    }

    public Task RecordDeliveryAttemptAsync(
        string id,
        int? status,
        string state,
        long attemptMs,
        long? nextAttemptMs,
        CancellationToken cancellationToken)
    {
        return WriteAsync(async command =>
        {
            command.CommandText = """
                                  UPDATE disclosure_deliveries
                                  SET attempts = attempts + 1, last_attempt_ms = $attempt, last_status = $status,
                                      state = $state, next_attempt_ms = $next
                                  WHERE id = $id;
                                  """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$attempt", attemptMs);
            command.Parameters.AddWithValue("$status", Nullable(status));
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$next", Nullable(nextAttemptMs));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Back to pending, due now, for a delivery that gave up or waits on a backoff.</summary>
    public Task RetryDeliveryAsync(string id, long nowMs, CancellationToken cancellationToken)
    {
        return WriteAsync(async command =>
        {
            command.CommandText = """
                                  UPDATE disclosure_deliveries SET state = 'pending', next_attempt_ms = $now
                                  WHERE id = $id AND state != 'delivered';
                                  """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", nowMs);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<DisclosureSummary>> ReadSummariesAsync(
        string clause,
        string? key,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {DisclosureColumns} FROM disclosures {clause};";
        if (key is not null)
            command.Parameters.AddWithValue("$key", key);
        var summaries = new List<DisclosureSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            summaries.Add(new DisclosureSummary(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), NullableString(reader, 9), NullableString(reader, 10), reader.GetBoolean(11),
                reader.GetBoolean(12), reader.GetBoolean(13), NullableString(reader, 14), NullableString(reader, 15),
                reader.IsDBNull(16) ? null : reader.GetBoolean(16), reader.IsDBNull(17) ? null : reader.GetBoolean(17),
                reader.GetInt64(18), NullableInt64(reader, 19), NullableInt64(reader, 20), NullableString(reader, 21),
                NullableString(reader, 22)));
        return summaries;
    }

    private async Task<IReadOnlyList<DeliveryRecord>> ReadDeliveriesAsync(
        string clause,
        object key,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT id, disclosure_id, webhook, event, state, attempts, last_attempt_ms,
                                      next_attempt_ms, last_status
                               FROM disclosure_deliveries {clause};
                               """;
        command.Parameters.AddWithValue("$key", key);
        var deliveries = new List<DeliveryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            deliveries.Add(new DeliveryRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), NullableInt64(reader, 6), NullableInt64(reader, 7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8)));
        return deliveries;
    }

    // One write under the gate, on a connection of its own.
    private async Task<T> WriteAsync<T>(Func<SqliteCommand, Task<T>> write, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            return await write(command);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static void AddFiles(SqliteCommand command, DisclosureFiles files)
    {
        command.Parameters.AddWithValue("$report", files.Report);
        command.Parameters.AddWithValue("$attestation", Nullable(files.Attestation));
        command.Parameters.AddWithValue("$bundle", Nullable(files.Bundle));
    }

    private static object Nullable(object? value)
    {
        return value ?? DBNull.Value;
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static long? NullableInt64(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }
}

public enum DisclosureFile
{
    Report,
    Attestation,
    Bundle,
    Pdf
}

/// <summary>The three files a CI publishes, each exactly as received.</summary>
public sealed record DisclosureFiles(byte[] Report, byte[]? Attestation, byte[]? Bundle);

public sealed record DisclosureRecord(
    string Id,
    string ReportUuid,
    string Organisation,
    string PeriodType,
    string PeriodFrom,
    string PeriodTo,
    string Intent,
    string Confidentiality,
    string ContentHash,
    string? IntegrityLevel,
    string? FiguresJson,
    DisclosureFiles Files,
    byte[]? Pdf,
    string? RenderReason,
    string? EngineVersion,
    bool? HashChecked,
    bool? BindingChecked,
    long PublishedAtMs);

/// <summary>A disclosure without its files: what every listing and every check reads.</summary>
public sealed record DisclosureSummary(
    string Id,
    string ReportUuid,
    string Organisation,
    string PeriodType,
    string PeriodFrom,
    string PeriodTo,
    string Intent,
    string Confidentiality,
    string ContentHash,
    string? IntegrityLevel,
    string? FiguresJson,
    bool HasAttestation,
    bool HasBundle,
    bool HasPdf,
    string? RenderReason,
    string? EngineVersion,
    bool? HashChecked,
    bool? BindingChecked,
    long PublishedAtMs,
    long? SignatureAddedAtMs,
    long? WithdrawnAtMs,
    string? WithdrawnReason,
    string? WithdrawnBy)
{
    public bool Withdrawn => WithdrawnAtMs is not null;
}

public sealed record ShareLinkRecord(
    string Id,
    string DisclosureId,
    byte[] TokenHash,
    string CreatedBy,
    long CreatedAtMs,
    long ExpiresAtMs);

public sealed record ShareLinkSummary(
    string Id,
    string CreatedBy,
    long CreatedAtMs,
    long ExpiresAtMs,
    long? RevokedAtMs,
    string? RevokedBy,
    long Downloads,
    long? LastUsedAtMs,
    string? LastClient);

/// <summary>Downloads counted in memory since the last flush, with the last one's time and client.</summary>
public sealed record ShareDownloads(long Count, long LastUsedAtMs, string? LastClient);

public sealed record DeliveryRecord(
    string Id,
    string DisclosureId,
    string Webhook,
    string Event,
    string State,
    int Attempts,
    long? LastAttemptMs,
    long? NextAttemptMs,
    int? LastStatus);

public static class DeliveryStates
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
}
