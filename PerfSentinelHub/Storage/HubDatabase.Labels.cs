using System.Buffers;
using System.Text;
using System.Text.Json;
using PerfSentinelHub.Access;

namespace PerfSentinelHub.Storage;

// The business-word label of an endpoint: one per (service, endpoint), Hub-wide, set by a person.
public sealed partial class HubDatabase
{
    /// <summary>
    ///     Sets, replaces or, for a null label, removes the label of one endpoint. False when the
    ///     write lock stays held past the wait.
    /// </summary>
    public Task<bool> SetEndpointLabelAsync(
        string service,
        string endpoint,
        string? label,
        string by,
        CancellationToken cancellationToken)
    {
        return WriteLabelAsync(
            label is null
                ? "DELETE FROM endpoint_labels WHERE service = $service AND endpoint = $endpoint;"
                : """
                  INSERT INTO endpoint_labels(service, endpoint, label, set_by, set_at_ms)
                  VALUES ($service, $endpoint, $label, $by, $at_ms)
                  ON CONFLICT(service, endpoint) DO UPDATE SET
                    label = excluded.label, set_by = excluded.set_by, set_at_ms = excluded.set_at_ms;
                  """,
            service, endpoint, label, by, cancellationToken);
    }

    /// <summary>
    ///     Sets, replaces or, for a null label, removes the business name of one service. False when
    ///     the write lock stays held past the wait.
    /// </summary>
    public Task<bool> SetServiceLabelAsync(string service, string? label, string by, CancellationToken cancellationToken)
    {
        return WriteLabelAsync(
            label is null
                ? "DELETE FROM service_labels WHERE service = $service;"
                : """
                  INSERT INTO service_labels(service, label, set_by, set_at_ms)
                  VALUES ($service, $label, $by, $at_ms)
                  ON CONFLICT(service) DO UPDATE SET
                    label = excluded.label, set_by = excluded.set_by, set_at_ms = excluded.set_at_ms;
                  """,
            service, null, label, by, cancellationToken);
    }

    // One write for both tables: $endpoint is bound only for an endpoint label.
    private async Task<bool> WriteLabelAsync(
        string sql,
        string service,
        string? endpoint,
        string? label,
        string by,
        CancellationToken cancellationToken)
    {
        if (!await _writeGate.WaitAsync(WriteGateWait, cancellationToken))
            return false;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$service", service);
            if (endpoint is not null)
                command.Parameters.AddWithValue("$endpoint", endpoint);
            if (label is not null)
            {
                command.Parameters.AddWithValue("$label", label);
                command.Parameters.AddWithValue("$by", by);
                command.Parameters.AddWithValue("$at_ms", timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>The labels of the given (service, endpoint) pairs; a pair without one is absent.</summary>
    public async Task<IReadOnlyDictionary<(string Service, string Endpoint), string>> ReadEndpointLabelsAsync(
        IReadOnlyCollection<(string Service, string Endpoint)> keys,
        CancellationToken cancellationToken)
    {
        var labels = new Dictionary<(string Service, string Endpoint), string>();
        if (keys.Count == 0)
            return labels;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT service, endpoint, label FROM endpoint_labels
                              WHERE (service, endpoint) IN (
                                SELECT json_extract(value, '$[0]'), json_extract(value, '$[1]') FROM json_each($keys));
                              """;
        command.Parameters.AddWithValue("$keys", PairArray(keys));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            labels[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        return labels;
    }

    /// <summary>
    ///     The Hub copies of the findings at one service and endpoint, or of the whole service for a
    ///     null endpoint, one per signature and source, with their own service and attribute rows:
    ///     what naming them is judged on. The read stops after the first copy <paramref name="enough" />
    ///     accepts, since one readable and writable copy decides the request.
    /// </summary>
    public async Task<IReadOnlyList<AccessCopy>> ReadLabelCopiesAsync(
        string service,
        string? endpoint,
        Func<AccessCopy, bool> enough,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = CopiesSelect + "WHERE f.service = $service" +
                              (endpoint is null ? "" : " AND f.endpoint = $endpoint") + CopiesOrder;
        command.Parameters.AddWithValue("$service", service);
        if (endpoint is not null)
            command.Parameters.AddWithValue("$endpoint", endpoint);
        return await ReadCopiesAsync(command, cancellationToken, enough);
    }

    /// <summary>The business names of the given services; a service without one is absent.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ReadServiceLabelsAsync(
        IReadOnlyCollection<string> services,
        CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (services.Count == 0)
            return labels;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT service, label FROM service_labels
                              WHERE service IN (SELECT value FROM json_each($services));
                              """;
        command.Parameters.AddWithValue("$services", JsonArray([.. services]));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            labels[reader.GetString(0)] = reader.GetString(1);
        return labels;
    }

    private static string PairArray(IEnumerable<(string Service, string Endpoint)> keys)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var (service, endpoint) in keys)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(service);
                writer.WriteStringValue(endpoint);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
