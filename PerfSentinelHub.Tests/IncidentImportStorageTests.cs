using System.Text;
using System.Text.Json.Nodes;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Tests;

public sealed class IncidentImportStorageTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private const string Source = "test";

    // The fixture's single incident, with its id, its end and its findings set by the test.
    private static ParsedIncident Incident(string id, long? endedAtMs, int findings)
    {
        var array = JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "daemon-incidents-0.20.0.json")))!.AsArray();
        var incident = array[0]!.AsObject();
        incident["id"] = id;
        incident["ended_at_ms"] = endedAtMs;
        var frozen = incident["findings"]!.AsArray();
        var first = frozen[0]!.ToJsonString();
        frozen.Clear();
        for (var index = 0; index < findings; index++)
            frozen.Add(JsonNode.Parse(first));
        return Assert.Single(IncidentParser.Parse(Encoding.UTF8.GetBytes(array.ToJsonString())).Incidents);
    }

    [Fact]
    public async Task A_pushed_incident_is_stored_like_a_polled_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string id = "a1000000000000000000000000000001";

        Assert.True(await factory.Database.TryUpsertPushedIncidentsAsync(
            Source, [Incident(id, null, 2)], 1_800_000_000_000, cancellationToken));

        var stored = await factory.Database.FindIncidentAsync(id, null, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.FindingCount);
        Assert.Null(stored.EndedAtMs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Push_and_poll_converge_on_the_richer_copy_whatever_their_order(bool pushFirst)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = pushFirst ? "a1000000000000000000000000000002" : "a1000000000000000000000000000003";
        var rich = Incident(id, null, 3);
        var endedButPoorer = Incident(id, 1_800_000_090_000, 1);

        if (pushFirst)
        {
            await factory.Database.TryUpsertPushedIncidentsAsync(Source, [rich], 1_800_000_000_000, cancellationToken);
            await factory.Database.UpsertIncidentsAsync(Source, [endedButPoorer], 1_800_000_100_000, cancellationToken);
        }
        else
        {
            await factory.Database.UpsertIncidentsAsync(Source, [rich], 1_800_000_000_000, cancellationToken);
            await factory.Database.TryUpsertPushedIncidentsAsync(Source, [endedButPoorer], 1_800_000_100_000,
                cancellationToken);
        }

        var stored = await factory.Database.FindIncidentAsync(id, null, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(3, stored.FindingCount);
        Assert.Equal(1_800_000_090_000, stored.EndedAtMs);
    }

    [Fact]
    public async Task A_push_records_the_import_and_files_no_read()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await factory.Database.TryUpsertPushedIncidentsAsync(
            Source, [Incident("a1000000000000000000000000000004", null, 1)], 1_800_000_200_000, cancellationToken);

        await using var connection = await factory.Database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT (SELECT last_import_ms FROM source_imports WHERE source_id = 'test'),
                                     (SELECT COUNT(*) FROM incident_reads WHERE source_id = 'test');
                              """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(1_800_000_200_000, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
    }
}
