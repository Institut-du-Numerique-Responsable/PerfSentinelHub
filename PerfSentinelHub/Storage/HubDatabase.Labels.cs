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
    public async Task<bool> SetEndpointLabelAsync(
        string service,
        string endpoint,
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
            command.CommandText = label is null
                ? "DELETE FROM endpoint_labels WHERE service = $service AND endpoint = $endpoint;"
                : """
                  INSERT INTO endpoint_labels(service, endpoint, label, set_by, set_at_ms)
                  VALUES ($service, $endpoint, $label, $by, $at_ms)
                  ON CONFLICT(service, endpoint) DO UPDATE SET
                    label = excluded.label, set_by = excluded.set_by, set_at_ms = excluded.set_at_ms;
                  """;
            command.Parameters.AddWithValue("$service", service);
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
    ///     Every Hub copy of every finding at one service and endpoint, one per signature and source,
    ///     with its own service and attribute rows: what naming the endpoint is judged on.
    /// </summary>
    public async Task<IReadOnlyList<AccessCopy>> ReadEndpointCopiesAsync(
        string service,
        string endpoint,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = CopiesSelect + "WHERE f.service = $service AND f.endpoint = $endpoint" + CopiesOrder;
        command.Parameters.AddWithValue("$service", service);
        command.Parameters.AddWithValue("$endpoint", endpoint);
        return await ReadCopiesAsync(command, cancellationToken);
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
