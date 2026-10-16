using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class CorrelationStorageTests : IAsyncLifetime
{
    private const long ObservedAt = 1_800_000_500_000;

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-episodes-{Guid.NewGuid():N}.db");

    private HubDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        _database = new HubDatabase(Options.Create(new HubOptions { DatabasePath = _path }), TimeProvider.System);
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestPool.ClearFor(_path);
        File.Delete(_path);
        File.Delete($"{_path}-shm");
        File.Delete($"{_path}-wal");
        return ValueTask.CompletedTask;
    }

    private async Task StoreAsync(string sourceId, params JsonObject[] copies)
    {
        // One call per copy, so a test decides the order in which they land.
        foreach (var copy in copies)
            Assert.True(await _database.TryUpsertEpisodesAsync(
                sourceId, EpisodeJson.Parsed(copy), ObservedAt, TestContext.Current.CancellationToken));
    }

    // One line per row, so a failure prints every merged column at once.
    private async Task<string> RowAsync(string id, string sourceId = "daemon-a")
    {
        return await ScalarAsync(
            """
            SELECT started_at_ms || ' ' || last_seen_ms || ' ' || IFNULL(ended_at_ms, '-') || ' ' ||
                   peak_confidence || ' ' || peak_co_occurrences || ' ' || last_confidence || ' ' ||
                   last_co_occurrences || ' ' || IFNULL(sample_trace_id, '-')
            FROM correlation_episodes WHERE id = $id AND source_id = $source_id;
            """, id, sourceId);
    }

    private async Task<string> ScalarAsync(string sql, string id = "", string sourceId = "daemon-a")
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$source_id", sourceId);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture)!;
    }

    [Fact]
    public async Task A_pushed_episode_keeps_both_sides_and_records_the_import()
    {
        await StoreAsync("daemon-a", EpisodeJson.Episode(EpisodeJson.Id(1)));

        Assert.Equal("1800000000000 1800000060000 - 0.92 41 0.88 37 4bf92f3577b34da6a3ce929d0e0e4736",
            await RowAsync(EpisodeJson.Id(1)));
        Assert.Equal("n_plus_one_sql order-svc pool_saturation payment-svc 1800000500000 1800000500000",
            await ScalarAsync(
                """
                SELECT source_type || ' ' || source_service || ' ' || target_type || ' ' || target_service || ' ' ||
                       first_seen_ms || ' ' || last_seen_hub_ms
                FROM correlation_episodes WHERE id = $id AND source_id = $source_id;
                """, EpisodeJson.Id(1)));
        Assert.Equal("1800000500000",
            await ScalarAsync("SELECT last_import_ms FROM source_imports WHERE source_id = $source_id;"));
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM source_state WHERE source_id = $source_id;"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_copies_converge_whatever_their_order(bool olderFirst)
    {
        var id = EpisodeJson.Id(2);
        var older = EpisodeJson.Episode(id);
        // A later round: a start precise to one round, rolling values that drifted, an end.
        var newer = EpisodeJson.Episode(id, 1_800_000_010_000, 1_800_000_120_000, 1_800_000_120_000);
        newer["peak_confidence"] = 0.8;
        newer["peak_co_occurrences"] = 30;
        newer["last_confidence"] = 0.7;
        newer["last_co_occurrences"] = 25;
        newer["sample_trace_id"] = "abababababababababababababababab";

        await StoreAsync("daemon-a", olderFirst ? [older, newer] : [newer, older]);

        Assert.Equal("1800000000000 1800000120000 1800000120000 0.92 41 0.7 25 abababababababababababababababab",
            await RowAsync(id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_close_and_an_update_of_the_same_moment_converge_on_the_close(bool updateFirst)
    {
        var id = EpisodeJson.Id(3);
        var update = EpisodeJson.Episode(id);
        // Queued at the last co-occurrence of the update before it, its rolling values drifted since.
        var close = EpisodeJson.Episode(id, endedAtMs: 1_800_000_060_000);
        close["last_confidence"] = 0.8;
        close["last_co_occurrences"] = 30;

        await StoreAsync("daemon-a", updateFirst ? [update, close] : [close, update]);

        Assert.Equal("1800000000000 1800000060000 1800000060000 0.92 41 0.8 30 4bf92f3577b34da6a3ce929d0e0e4736",
            await RowAsync(id));
    }

    [Fact]
    public async Task Two_daemons_keep_a_copy_each()
    {
        var id = EpisodeJson.Id(4);
        var other = EpisodeJson.Episode(id);
        other["peak_confidence"] = 0.5;

        await StoreAsync("daemon-a", EpisodeJson.Episode(id));
        await StoreAsync("daemon-b", other);

        Assert.StartsWith("1800000000000 1800000060000 - 0.92 ", await RowAsync(id));
        Assert.StartsWith("1800000000000 1800000060000 - 0.5 ", await RowAsync(id, "daemon-b"));
    }
}
